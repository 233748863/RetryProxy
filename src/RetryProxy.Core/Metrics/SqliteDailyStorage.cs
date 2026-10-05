using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Storage;

namespace RetryProxy.Core.Metrics;

/// <summary>
/// 每日统计的 SQLite 存储端：logs\data.db 的 daily_requests 表。
/// 写入按主键 (route_id, date, request_id) UPSERT（幂等，取代 jsonl 的"追加 + 末行覆盖"）；
/// 打开时按 (route_id, date) 读回当天记录。该通道在库中还没有任何记录时，沿用一次性导入
/// 从保留的运行日志恢复当天统计（上线当天不丢当天已完成的计数）。
/// </summary>
public sealed class SqliteDailyStorage : IDailyStorage
{
    private readonly DataDatabase _data;
    private readonly string _routeId;
    private readonly string _routeName;

    public SqliteDailyStorage(DataDatabase data, string routeId, string routeName)
    {
        _data = data;
        _routeId = routeId;
        _routeName = routeName;
    }

    /// <summary>库里存日期的格式（本地自然日）。</summary>
    internal static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public DailyJournalOpenResult Open(DateOnly date, bool importLegacy)
    {
        var database = _data.Database;
        using var connection = database.Connect();
        var records = Load(connection, _routeId, date, out var damaged);
        // 恢复标志与旧 jsonl 头字段同义：当天导入过就一直为真，重启后统计页仍提示"今日数据包含旧日志恢复记录"。
        var legacy = IsImported(connection, _routeId, date);
        if (!legacy && importLegacy && !RouteHasAnyRows(connection, _routeId))
        {
            // 与 jsonl 时代一致：只有该通道还没有任何统计记录时才导入；普通轮转与重复的旧日志行不再触发。
            var restored = LegacyLogRestore.Restore(_data.LogDirectory, _routeName, date);
            if (restored.Count > 0)
            {
                _data.Writer.Execute(c => UpsertAll(c, _routeId, date, restored.Values));
                records = new Dictionary<string, DailyRequest>(restored, StringComparer.Ordinal);
                legacy = true;
            }
        }

        return new DailyJournalOpenResult
        {
            Journal = new SqliteDailyJournal(_data.Writer, _routeId, date),
            Records = records,
            RestoredFromLegacyLogs = legacy,
            Warning = damaged > 0 ? $"统计日志中有 {damaged} 条未写完或损坏的记录，已恢复其余可读数据" : null,
        };
    }

    private static Dictionary<string, DailyRequest> Load(SqliteConnection connection, string routeId, DateOnly date, out int damaged)
    {
        damaged = 0;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_json FROM daily_requests WHERE route_id = $route AND date = $date ORDER BY sequence;";
        command.Parameters.AddWithValue("$route", routeId);
        command.Parameters.AddWithValue("$date", DateText(date));
        var records = new Dictionary<string, DailyRequest>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            DailyRequest? record = null;
            try
            {
                record = JsonSerializer.Deserialize<DailyRequest>(reader.GetString(0), RowJson.Options);
            }
            catch (JsonException)
            {
            }

            if (record is { } value && !string.IsNullOrEmpty(value.RequestId) && !value.RequestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal))
            {
                records[value.RequestId] = value;
            }
            else
            {
                damaged++;
            }
        }

        return records;
    }

    private static bool RouteHasAnyRows(SqliteConnection connection, string routeId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM daily_requests WHERE route_id = $route);";
        command.Parameters.AddWithValue("$route", routeId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void UpsertAll(SqliteConnection connection, string routeId, DateOnly date, IEnumerable<DailyRequest> records)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var record in records)
        {
            Upsert(connection, transaction, routeId, date, record);
        }

        // 导入标记与记录同一事务落库：要么都写进去，要么下次打开整体重试。
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO daily_imports (route_id, date) VALUES ($route, $date) ON CONFLICT(route_id, date) DO NOTHING;";
            command.Parameters.AddWithValue("$route", routeId);
            command.Parameters.AddWithValue("$date", DateText(date));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static bool IsImported(SqliteConnection connection, string routeId, DateOnly date)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM daily_imports WHERE route_id = $route AND date = $date);";
        command.Parameters.AddWithValue("$route", routeId);
        command.Parameters.AddWithValue("$date", DateText(date));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>单条 UPSERT；<paramref name="transaction"/> 为 null 时走隐式事务。</summary>
    internal static void Upsert(SqliteConnection connection, SqliteTransaction? transaction, string routeId, DateOnly date, DailyRequest record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO daily_requests (route_id, date, request_id, updated_at_ms, sequence, outcome, retry_count, key_id, key_name, record_json)
            VALUES ($route, $date, $id, $updated, $sequence, $outcome, $retries, $keyId, $keyName, $json)
            ON CONFLICT(route_id, date, request_id) DO UPDATE SET
                updated_at_ms = excluded.updated_at_ms,
                sequence = excluded.sequence,
                outcome = excluded.outcome,
                retry_count = excluded.retry_count,
                key_id = excluded.key_id,
                key_name = excluded.key_name,
                record_json = excluded.record_json;
            """;
        command.Parameters.AddWithValue("$route", routeId);
        command.Parameters.AddWithValue("$date", DateText(date));
        command.Parameters.AddWithValue("$id", record.RequestId);
        command.Parameters.AddWithValue("$updated", record.UpdatedAtUnixMs);
        command.Parameters.AddWithValue("$sequence", (long)record.Sequence);
        command.Parameters.AddWithValue("$outcome", record.Outcome switch
        {
            RequestOutcome.Success => (object)"success",
            RequestOutcome.Failure => (object)"failure",
            _ => DBNull.Value,
        });
        command.Parameters.AddWithValue("$retries", (long)record.RetryCount);
        command.Parameters.AddWithValue("$keyId", record.KeyId);
        command.Parameters.AddWithValue("$keyName", record.KeyName);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record, RowJson.Options));
        command.ExecuteNonQuery();
    }
}

/// <summary>record_json 的序列化参数：字段名沿用 DailyRequest 上的 JsonPropertyName，中文不转义，便于用工具直接查看库。</summary>
internal static class RowJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>某一天的写入口：每条记录一次 UPSERT（单语句即一个隐式事务）。</summary>
internal sealed class SqliteDailyJournal : IDailyJournal
{
    private readonly SqliteWriteQueue _writer;
    private readonly string _routeId;
    private readonly DateOnly _date;

    public SqliteDailyJournal(SqliteWriteQueue writer, string routeId, DateOnly date)
    {
        _writer = writer;
        _routeId = routeId;
        _date = date;
    }

    public void Append(DailyRequest record)
        => _writer.Execute(connection => SqliteDailyStorage.Upsert(connection, transaction: null, _routeId, _date, record));
}
