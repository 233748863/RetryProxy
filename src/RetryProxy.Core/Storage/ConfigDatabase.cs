using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace RetryProxy.Core.Storage;

/// <summary>
/// User\config.db（配置库：config 表存 proxy/common/other/preparations 四行 JSON）的共享入口：惰性打开。
/// 读取用 <see cref="ReadRows"/>（库文件不存在时返回空表且不创建文件）；写入用 <see cref="WriteRows"/>
/// （每库一个写线程，单事务覆盖）。打开失败不缓存：下次访问会重试，调用方把异常映射成各自的提示文案。
/// </summary>
public sealed class ConfigDatabase : IDisposable
{
    private readonly object _lock = new();
    private SqliteDatabase? _database;
    private SqliteWriteQueue? _writer;

    public ConfigDatabase(string userDirectory)
    {
        UserDirectory = System.IO.Path.GetFullPath(userDirectory);
        Path = System.IO.Path.Combine(UserDirectory, "config.db");
    }

    /// <summary>本库所在的 User 目录（备份目录 backup 与它同级）。</summary>
    public string UserDirectory { get; }

    public string Path { get; }

    public SqliteDatabase Database
    {
        get
        {
            lock (_lock)
            {
                return _database ??= SqliteDatabase.Open(Path, DatabaseSchemas.ConfigVersion, DatabaseSchemas.EnsureConfig);
            }
        }
    }

    public SqliteWriteQueue Writer
    {
        get
        {
            lock (_lock)
            {
                return _writer ??= Database.CreateWriteQueue();
            }
        }
    }

    /// <summary>读取 config 表全部行（key → value）。库文件不存在时返回空表且不创建文件。</summary>
    /// <param name="readOnly">注入模式使用只读连接，禁止初始化或升级结构；零字节文件按空配置处理。</param>
    public Dictionary<string, string> ReadRows(bool readOnly = false)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!System.IO.File.Exists(Path) || (readOnly && new System.IO.FileInfo(Path).Length == 0))
        {
            return rows;
        }

        // 注入读取不能经过 Database 属性：该入口会建表、更新结构版本并切换日志模式。
        using var connection = readOnly
            ? new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5,
            }.ToString())
            : Database.Connect();
        if (readOnly) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM config;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows[reader.GetString(0)] = reader.GetString(1);
        }

        return rows;
    }

    /// <summary>在单事务里写入/覆盖给定行；updated_at 统一取当前 UTC 毫秒。</summary>
    public void WriteRows(IReadOnlyDictionary<string, string> rows)
    {
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Writer.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO config (key, value, updated_at) VALUES ($key, $value, $updatedAt) ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;";
            var key = command.Parameters.Add("$key", SqliteType.Text);
            var value = command.Parameters.Add("$value", SqliteType.Text);
            command.Parameters.AddWithValue("$updatedAt", updatedAt);
            foreach (var row in rows)
            {
                key.Value = row.Key;
                value.Value = row.Value;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    /// <summary>
    /// 用 SQLite 在线备份取得包含已提交 WAL 事务的一致副本。
    /// 直接只读打开源文件，不经过建表/升级入口；故障备份不能修改原库或要求当前库版本可读。
    /// </summary>
    public void BackupTo(string destinationPath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        // 最终只交付一个完整数据库文件，不能让备份自身依赖旁边的 WAL。
        using var command = destination.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        command.ExecuteNonQuery();
    }

    /// <summary>停止写队列并释放文件句柄；之后仍可继续使用（会重新打开）。</summary>
    public void Dispose()
    {
        SqliteWriteQueue? writer;
        SqliteDatabase? database;
        lock (_lock)
        {
            writer = _writer;
            database = _database;
            _writer = null;
            _database = null;
        }

        writer?.Dispose();
        database?.Dispose();
    }
}
