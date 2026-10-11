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

/// <summary>按完整请求编号读取当前页用量，不反序列化整月统计；跨日完成时取最新记录。</summary>
public static class RequestLogUsageReader
{
    public static UsageHistoryResult Read(DataDatabase data, DateOnly firstDate, DateOnly lastDate,
        IReadOnlyCollection<string> requestIds, CancellationToken token = default)
    {
        var ids = requestIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0 || lastDate < firstDate) return new([], []);
        if (ids.Length > 100) throw new ArgumentOutOfRangeException(nameof(requestIds), "一次最多读取 100 个请求");
        var records = new Dictionary<(string Route, string Id), StoredUsageRecord>();
        var warnings = new List<string>();
        var store = data.Statistics;
        var firstMonth = new DateOnly(firstDate.Year, firstDate.Month, 1);
        var lastMonth = new DateOnly(lastDate.Year, lastDate.Month, 1);
        foreach (var month in store.Dates().Where(month => month >= firstMonth && month <= lastMonth))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var raw = new List<(string Route, string Date, string Id, string Json)>();
                using (var lease = store.OpenRead(month, token))
                {
                    if (lease is null) continue;
                    using var command = lease.Connection.CreateCommand();
                    // 子查询可从现有主键覆盖索引筛选编号；外层仅按 rowid 读取命中的 JSON。
                    command.CommandText = "SELECT route_id, date, request_id, record_json FROM daily_requests WHERE rowid IN ("
                        + "SELECT rowid FROM daily_requests WHERE date >= $first AND date <= $last AND request_id IN ("
                        + string.Join(",", ids.Select((_, index) => "$id" + index)) + "));";
                    command.Parameters.AddWithValue("$first", firstDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("$last", lastDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    for (var index = 0; index < ids.Length; index++) command.Parameters.AddWithValue("$id" + index, ids[index]);
                    using var cancel = token.Register(command.Cancel);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        raw.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                    }
                }
                var damaged = 0;
                foreach (var row in raw)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var request = JsonSerializer.Deserialize<DailyRequest>(row.Json, RowJson.Options);
                        if (request is null || request.RequestId != row.Id ||
                            !DateOnly.TryParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        { damaged++; continue; }
                        var key = (row.Route, row.Id);
                        if (!records.TryGetValue(key, out var previous) || request.UpdatedAtUnixMs > previous.Request.UpdatedAtUnixMs
                            || request.UpdatedAtUnixMs == previous.Request.UpdatedAtUnixMs && date > previous.Date)
                            records[key] = new StoredUsageRecord(row.Route, date, request);
                    }
                    catch (JsonException) { damaged++; }
                }
                if (damaged > 0) warnings.Add($"{month:yyyy-MM}：跳过 {damaged} 条损坏记录");
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                token.ThrowIfCancellationRequested();
                warnings.Add($"{month:yyyy-MM}：请求用量读取失败");
            }
        }
        return new(records.Values.ToArray(), warnings);
    }
}
