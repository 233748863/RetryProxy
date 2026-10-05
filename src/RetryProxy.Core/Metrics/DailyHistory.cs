using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Storage;

namespace RetryProxy.Core.Metrics;

/// <summary>某一天的汇总；统计页的"最近 7 天"用它。HasData 为 false 表示那天在库中没有任何统计记录。</summary>
public sealed record DailySummary(
    DateOnly Date,
    bool HasData,
    ulong TotalRequests,
    ulong SuccessfulRequests,
    ulong FailedRequests,
    ulong RetryCount,
    ulong InputTokens,
    ulong CachedTokens)
{
    public static DailySummary Empty(DateOnly date) => new(date, false, 0, 0, 0, 0, 0, 0);

    public double? HitRatePercent() => InputTokens > 0 ? 100.0 * CachedTokens / InputTokens : null;
}

/// <summary>
/// 只读地汇总库里的历史每日统计（daily_requests 表）。每行就是该请求的最新状态（UPSERT 保证），
/// 不需要再按请求取最后一行。token 口径与 <see cref="CacheRequest.Usage"/> 一致，但用 SQL 的
/// json_extract 现算，口径变化仍然作用于历史数据；损坏的行（json_valid 为假）跳过，不改库。
/// </summary>
public static class DailyHistory
{
    /// <summary>从 firstDate 起按日期升序读取 days 天。</summary>
    public static IReadOnlyList<DailySummary> ReadRange(DataDatabase data, string routeId, DateOnly firstDate, int days)
    {
        var list = new List<DailySummary>(Math.Max(days, 0));
        if (days <= 0)
        {
            return list;
        }

        var first = firstDate;
        var last = firstDate.AddDays(days - 1);
        Dictionary<string, DayTotals> byDate;
        try
        {
            byDate = Query(data, routeId, first, last);
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            byDate = new Dictionary<string, DayTotals>(StringComparer.Ordinal);
        }

        for (var offset = 0; offset < days; offset++)
        {
            var date = firstDate.AddDays(offset);
            list.Add(byDate.TryGetValue(SqliteDailyStorage.DateText(date), out var day)
                ? new DailySummary(date, true, day.Total, day.Success, day.Failed, day.Retries, day.Input, day.Cached)
                : DailySummary.Empty(date));
        }

        return list;
    }

    public static DailySummary ReadDay(DataDatabase data, string routeId, DateOnly date)
    {
        var days = ReadRange(data, routeId, date, 1);
        return days.Count > 0 ? days[0] : DailySummary.Empty(date);
    }

    private sealed record DayTotals(ulong Total, ulong Success, ulong Failed, ulong Retries, ulong Input, ulong Cached);

    private static Dictionary<string, DayTotals> Query(DataDatabase data, string routeId, DateOnly first, DateOnly last)
    {
        using var connection = data.Database.Connect();
        using var command = connection.CreateCommand();
        // token 口径：includes_cached 直接取总输入；excludes_cached 为 未缓存输入 + 缓存读取 + 缓存写入（未报写入按 0），
        // 总输入为 0、缓存读取超过总输入或字段缺失的行按"未获取"排除——与 CacheRequest.Usage() 完全一致。
        command.CommandText = """
            WITH rows AS (
                SELECT date,
                       outcome,
                       retry_count,
                       request_id,
                       json_extract(record_json, '$.cache.input_accounting') AS accounting,
                       json_extract(record_json, '$.cache.input_tokens') AS input_tokens,
                       json_extract(record_json, '$.cache.cached_tokens') AS cached_tokens,
                       COALESCE(json_extract(record_json, '$.cache.cache_creation_tokens'), 0) AS created_tokens
                FROM daily_requests
                WHERE route_id = $route AND date >= $first AND date <= $last AND json_valid(record_json)
            )
            SELECT date,
                   COUNT(*),
                   COALESCE(SUM(CASE WHEN outcome = 'success' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN outcome = 'failure' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(retry_count), 0),
                   COALESCE(SUM(CASE
                       WHEN accounting = 'includes_cached' AND input_tokens IS NOT NULL AND cached_tokens IS NOT NULL
                            AND input_tokens > 0 AND cached_tokens <= input_tokens
                           THEN input_tokens
                       WHEN accounting = 'excludes_cached' AND input_tokens IS NOT NULL AND cached_tokens IS NOT NULL
                            AND input_tokens + cached_tokens + created_tokens > 0
                            AND cached_tokens <= input_tokens + cached_tokens + created_tokens
                           THEN input_tokens + cached_tokens + created_tokens
                       ELSE NULL END), 0),
                   COALESCE(SUM(CASE
                       WHEN accounting = 'includes_cached' AND input_tokens IS NOT NULL AND cached_tokens IS NOT NULL
                            AND input_tokens > 0 AND cached_tokens <= input_tokens
                           THEN cached_tokens
                       WHEN accounting = 'excludes_cached' AND input_tokens IS NOT NULL AND cached_tokens IS NOT NULL
                            AND input_tokens + cached_tokens + created_tokens > 0
                            AND cached_tokens <= input_tokens + cached_tokens + created_tokens
                           THEN cached_tokens
                       ELSE NULL END), 0)
            FROM rows
            WHERE request_id NOT LIKE '保活-%'
            GROUP BY date;
            """;
        command.Parameters.AddWithValue("$route", routeId);
        command.Parameters.AddWithValue("$first", SqliteDailyStorage.DateText(first));
        command.Parameters.AddWithValue("$last", SqliteDailyStorage.DateText(last));
        var byDate = new Dictionary<string, DayTotals>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            byDate[reader.GetString(0)] = new DayTotals(
                (ulong)reader.GetInt64(1),
                (ulong)reader.GetInt64(2),
                (ulong)reader.GetInt64(3),
                (ulong)reader.GetInt64(4),
                (ulong)reader.GetInt64(5),
                (ulong)reader.GetInt64(6));
        }

        return byDate;
    }
}
