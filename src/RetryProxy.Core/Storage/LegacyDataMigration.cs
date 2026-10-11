using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace RetryProxy.Core.Storage;

/// <summary>旧库一致性快照按日期复制；每批事务、可重跑，全部复制完成后才归档原库。</summary>
internal static class LegacyDataMigration
{
    private sealed record Table(string Name, string Columns, string Keys, bool Monthly);
    private static readonly Table[] Tables =
    [
        new("daily_requests", "route_id,date,request_id,updated_at_ms,sequence,outcome,retry_count,key_id,key_name,record_json", "route_id,date,request_id", true),
        new("daily_imports", "route_id,date", "route_id,date", true),
        new("diagnostic_events", "id,session_id,request_id,date,sequence,kind,entry_json,request_json,payload_bytes,created_at_ms", "session_id,request_id,sequence", false),
        new("diagnostic_marks", "session_id,date", "session_id,date", false),
        new("diagnostic_request_marks", "session_id,request_id,date", "session_id,request_id,date", false),
    ];

    public static void Run(string path, PartitionedDatabase statistics, PartitionedDatabase diagnostics)
    {
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString()))
        {
            source.Open();
            using var versionCommand = source.CreateCommand();
            versionCommand.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            if (!int.TryParse(versionCommand.ExecuteScalar() as string, out var version) || version < 1 || version > DatabaseSchemas.DataVersion)
                throw new InvalidDataException("旧数据库版本不受支持，原文件已保留");
            versionCommand.CommandText = "PRAGMA data_version;";
            var before = Convert.ToInt64(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            using (var transaction = source.BeginTransaction(deferred: true))
            {
                foreach (var table in Tables)
                {
                    if (!Exists(source, transaction, table.Name)) continue;
                    var store = table.Monthly ? statistics : diagnostics;
                    foreach (var date in Dates(source, transaction, table.Name))
                        CopyDate(source, transaction, store, table, date);
                }
                transaction.Commit();
            }
            if (Convert.ToInt64(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != before)
                throw new IOException("旧数据库仍在写入，请关闭使用旧库的程序后重试迁移");
        }
        statistics.Dispose();
        diagnostics.Dispose();
        PartitionedDatabase.Checkpoint(path);
        var backup = path + ".migrated.bak";
        if (File.Exists(backup)) backup = path + "." + Guid.NewGuid().ToString("N") + ".migrated.bak";
        using var guard = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
        File.Move(path, backup);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }

    private static bool Exists(SqliteConnection source, SqliteTransaction transaction, string table)
    {
        using var command = source.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", table);
        return (long)command.ExecuteScalar()! != 0;
    }

    private static List<DateOnly> Dates(SqliteConnection source, SqliteTransaction transaction, string table)
    {
        using var command = source.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT DISTINCT date FROM {table} ORDER BY date;";
        using var reader = command.ExecuteReader();
        var dates = new List<DateOnly>();
        while (reader.Read())
        {
            if (!DateOnly.TryParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new InvalidDataException("旧数据库包含无法归档的日期，原文件已保留");
            dates.Add(date);
        }
        return dates;
    }

    private static void CopyDate(SqliteConnection source, SqliteTransaction transaction, PartitionedDatabase store, Table table, DateOnly date)
    {
        var text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var command = source.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {table.Columns} FROM {table.Name} WHERE date = $date;";
        command.Parameters.AddWithValue("$date", text);
        using var reader = command.ExecuteReader();
        var batch = new List<object[]>(256);
        long copied = 0;
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            batch.Add(values);
            copied++;
            if (batch.Count == 256) { Insert(store, table, date, batch); batch.Clear(); }
        }
        if (batch.Count > 0) Insert(store, table, date, batch);
        using var lease = store.OpenRead(date) ?? throw new IOException("迁移目标数据库不存在");
        using var check = lease.Connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM {table.Name} WHERE date = $date;";
        check.Parameters.AddWithValue("$date", text);
        if ((long)check.ExecuteScalar()! < copied) throw new InvalidDataException("迁移记录校验失败，原文件已保留");
    }

    private static void Insert(PartitionedDatabase store, Table table, DateOnly date, List<object[]> rows)
        => store.Execute(date, connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var columns = table.Columns.Split(',');
            var names = Enumerable.Range(0, columns.Length).Select(index => "$p" + index).ToArray();
            var conflict = table.Name == "daily_requests"
                ? "DO UPDATE SET " + string.Join(",", columns.Except(table.Keys.Split(',')).Select(column => column + "=excluded." + column)) + " WHERE excluded.updated_at_ms >= daily_requests.updated_at_ms"
                : "DO NOTHING";
            command.CommandText = $"INSERT INTO {table.Name} ({table.Columns}) VALUES ({string.Join(',', names)}) ON CONFLICT({table.Keys}) {conflict};";
            foreach (var name in names) command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
            foreach (var row in rows)
            {
                for (var i = 0; i < row.Length; i++) command.Parameters[i].Value = row[i];
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        });
}
