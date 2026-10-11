using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Storage;

namespace RetryProxy.Core.Metrics;

public sealed record StoredUsageRecord(string RouteId, DateOnly Date, DailyRequest Request);
public sealed record UsageHistoryResult(IReadOnlyList<StoredUsageRecord> Records, IReadOnlyList<string> Warnings);

/// <summary>
/// 分月缓存已结束请求。检查主库和 WAL，避免重复解析不变的历史月份；在后台线程调用。
/// 只读现有统计，不回填旧记录，不借当前供应商配置推测历史归属。
/// </summary>
public sealed class UsageHistory
{
    private sealed record Stamp(long Length, long Written, long WalLength, long WalWritten);
    private sealed record MonthCache(Stamp Stamp, IReadOnlyList<StoredUsageRecord> Records, int Damaged);
    private readonly Dictionary<DateOnly, MonthCache> _months = new();

    public UsageHistoryResult Read(DataDatabase data, CancellationToken token = default)
    {
        var store = data.Statistics;
        var dates = store.Dates();
        var warnings = new List<string>();
        var records = new Dictionary<(string Route, string Id), StoredUsageRecord>();
        foreach (var removed in _months.Keys.Except(dates).ToArray()) _months.Remove(removed);
        foreach (var month in dates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var stamp = FileStamp(store.GetPath(month));
                if (!_months.TryGetValue(month, out var cached) || cached.Stamp != stamp)
                {
                    cached = ReadMonth(store, month, stamp, token);
                    _months[month] = cached;
                }
                if (cached.Damaged > 0) warnings.Add($"{month:yyyy-MM}：跳过 {cached.Damaged} 条损坏记录");
                foreach (var row in cached.Records)
                {
                    var key = (row.RouteId, row.Request.RequestId);
                    if (!records.TryGetValue(key, out var previous)
                        || row.Request.UpdatedAtUnixMs > previous.Request.UpdatedAtUnixMs
                        || row.Request.UpdatedAtUnixMs == previous.Request.UpdatedAtUnixMs && row.Date > previous.Date)
                        records[key] = row;
                }
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                // 不把读取失败的月份呈现成“零请求”，也不让其它可读月份消失。
                _months.Remove(month);
                warnings.Add($"{month:yyyy-MM}：统计读取失败（{IoErrorKind.Describe(error)}）");
            }
        }
        token.ThrowIfCancellationRequested();
        return new UsageHistoryResult(records.Values.ToList(), warnings);
    }

    public void Invalidate() => _months.Clear();

    private static Stamp FileStamp(string path)
    {
        var file = new FileInfo(path);
        var wal = new FileInfo(path + "-wal");
        return new Stamp(file.Exists ? file.Length : 0, file.Exists ? file.LastWriteTimeUtc.Ticks : 0,
            wal.Exists ? wal.Length : 0, wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0);
    }

    private static MonthCache ReadMonth(PartitionedDatabase store, DateOnly month, Stamp stamp, CancellationToken token)
    {
        var raw = new List<(string Route, string Date, string Id, string Json)>();
        // 先取原始行、释放数据库租约，再解析；耗时的 JSON 工作不占用代理写入的锁。
        using (var lease = store.OpenRead(month, token))
        {
            if (lease is null) return new MonthCache(stamp, [], 0);
            using var command = lease.Connection.CreateCommand();
            command.CommandText = "SELECT route_id, date, request_id, record_json FROM daily_requests WHERE outcome IN ('success', 'failure');";
            using var cancel = token.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                raw.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        var result = new List<StoredUsageRecord>(raw.Count);
        var damaged = 0;
        foreach (var row in raw)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var request = JsonSerializer.Deserialize<DailyRequest>(row.Json, RowJson.Options);
                if (request is null || string.IsNullOrEmpty(request.RequestId) || request.RequestId != row.Id
                    || request.Outcome is null || !DateOnly.TryParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                { damaged++; continue; }
                if (request.RequestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal)) continue;
                // 老记录只有 cache，没有 output 等分析字段，交给聚合层按“部分用量”呈现。
                result.Add(new StoredUsageRecord(row.Route, date, request));
            }
            catch (JsonException) { damaged++; }
        }
        return new MonthCache(stamp, result, damaged);
    }
}
