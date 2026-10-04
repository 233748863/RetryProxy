using System;
using System.Collections.Generic;
using System.IO;

namespace RetryProxy.Core.Metrics;

/// <summary>某一天的汇总；统计页的"最近 7 天"用它。HasData 为 false 表示那天没有任何统计文件。</summary>
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
/// 只读地汇总历史每日统计文件。同一请求在文件里会出现多行（开始、重试、结束各一行），
/// 按请求编号取最后一行，与打开统计时的恢复规则一致；损坏的行跳过，不修改文件。
/// </summary>
public static class DailyHistory
{
    /// <summary>从 firstDate 起按日期升序读取 days 天。</summary>
    public static IReadOnlyList<DailySummary> ReadRange(string logDirectory, string routeId, DateOnly firstDate, int days)
    {
        var list = new List<DailySummary>(days);
        for (var offset = 0; offset < days; offset++)
        {
            list.Add(ReadDay(logDirectory, routeId, firstDate.AddDays(offset)));
        }

        return list;
    }

    public static DailySummary ReadDay(string logDirectory, string routeId, DateOnly date)
    {
        var path = DailyStorage.JournalPathFor(logDirectory, routeId, date);
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return DailySummary.Empty(date);
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return DailySummary.Empty(date);
        }

        var headerEnd = Array.IndexOf(bytes, (byte)'\n');
        var headerLength = headerEnd < 0 ? bytes.Length : headerEnd + 1;
        var header = JournalLine.TryParse(new ReadOnlySpan<byte>(bytes, 0, headerLength));
        if (header is null || header.Record != "header" || header.Date != DailyStorage.DateText(date) || header.RouteId != routeId)
        {
            return DailySummary.Empty(date);
        }

        var records = new Dictionary<string, DailyRequest>(StringComparer.Ordinal);
        var start = headerLength;
        while (start < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', start);
            var count = newline < 0 ? bytes.Length - start : newline - start + 1;
            var parsed = JournalLine.TryParse(new ReadOnlySpan<byte>(bytes, start, count));
            if (parsed is { Record: "request", Value: { } value }
                && !string.IsNullOrEmpty(value.RequestId)
                && !value.RequestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal))
            {
                records[value.RequestId] = value;
            }

            start += count;
        }

        ulong total = 0;
        ulong success = 0;
        ulong failed = 0;
        ulong retries = 0;
        ulong input = 0;
        ulong cached = 0;
        foreach (var record in records.Values)
        {
            total = Saturating.Add(total, 1);
            switch (record.Outcome)
            {
                case RequestOutcome.Success:
                    success = Saturating.Add(success, 1);
                    break;
                case RequestOutcome.Failure:
                    failed = Saturating.Add(failed, 1);
                    break;
            }

            retries = Saturating.Add(retries, record.RetryCount);
            if (record.Cache?.Usage() is { } usage)
            {
                input = Saturating.Add(input, usage.Input);
                cached = Saturating.Add(cached, usage.Cached);
            }
        }

        return new DailySummary(date, true, total, success, failed, retries, input, cached);
    }
}
