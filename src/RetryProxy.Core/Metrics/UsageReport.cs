using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Metrics;

public enum UsageDimension { Model, Provider, Client, Key }
public enum UsageMetric { Tokens, Cost, Requests, Speed }

public sealed record UsageQuery
{
    public DateOnly? FirstDate { get; init; }
    public DateOnly LastDate { get; init; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.Today);
    public string Client { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public RequestOutcome? Outcome { get; init; }
    public UsageDimension Dimension { get; init; } = UsageDimension.Model;
}

public sealed record UsageOption(string Id, string Name);

public sealed record UsageRecord
{
    public const string Unknown = "\u001funrecorded";
    public string RouteId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public DateOnly Date { get; init; }
    public DateTime? CompletedAt { get; init; }
    public RequestOutcome Outcome { get; init; }
    public ulong Retries { get; init; }
    public string Client { get; init; } = Unknown;
    public string ProviderId { get; init; } = Unknown;
    public string ProviderName { get; init; } = string.Empty;
    public string KeyId { get; init; } = Unknown;
    public string KeyName { get; init; } = string.Empty;
    public string Model { get; init; } = Unknown;
    public RequestUsage? Usage { get; init; }
    public NormalizedUsage? Tokens { get; init; }
    public PriceQuote Quote { get; init; } = new(null, false, string.Empty);

    public string KeyIdentity => ProviderId + "\n" + KeyId;

    public UsageOption Group(UsageDimension dimension) => dimension switch
    {
        UsageDimension.Provider => new(ProviderId, string.IsNullOrEmpty(ProviderName) ? "未记录供应商" : ProviderName),
        UsageDimension.Client => new(Client, Client == Unknown ? "未记录客户端" : Client),
        UsageDimension.Key => new(KeyIdentity, (string.IsNullOrEmpty(KeyName) ? "未记录 Key" : KeyName)
            + (string.IsNullOrEmpty(ProviderName) ? string.Empty : " · " + ProviderName)),
        _ => new(Model, Model == Unknown ? "未记录模型" : Model),
    };

    public static UsageRecord From(StoredUsageRecord stored, IReadOnlyDictionary<string, ClientType> routeClients, ModelPricing prices)
    {
        var r = stored.Request;
        var a = r.Attribution;
        var usage = r.Usage ?? RequestUsage.FromLegacy(r.Cache);
        var model = !string.IsNullOrWhiteSpace(usage?.Model) && usage.Model != "未获取" ? usage.Model
            : !string.IsNullOrWhiteSpace(a?.Model) ? a.Model : Unknown;
        var provider = string.IsNullOrEmpty(a?.ProviderId) ? Unknown : a.ProviderId;
        var client = a?.Client ?? (routeClients.TryGetValue(stored.RouteId, out var knownClient) ? knownClient : (ClientType?)null);
        DateTime? completed = null;
        try
        {
            if (r.UpdatedAtUnixMs > 0) completed = DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAtUnixMs).LocalDateTime;
        }
        catch (ArgumentOutOfRangeException) { }
        return new UsageRecord
        {
            RouteId = stored.RouteId, RequestId = r.RequestId, Date = stored.Date, CompletedAt = completed,
            Outcome = r.Outcome!.Value, Retries = r.RetryCount,
            Client = client switch { ClientType.Claude => "Claude Code", ClientType.Codex => "Codex", _ => Unknown },
            ProviderId = provider, ProviderName = a?.ProviderName ?? string.Empty,
            KeyId = string.IsNullOrEmpty(r.KeyId) ? Unknown : r.KeyId, KeyName = r.KeyName,
            Model = model, Usage = usage, Tokens = usage?.Normalize(), Quote = prices.Quote(provider, model, usage),
        };
    }
}

/// <summary>decimal 保留大用量的整数精度；比率只在最终展示时转为 double。</summary>
public sealed class UsageTotals
{
    public long Requests { get; private set; }
    public long Successes { get; private set; }
    public long Failures => Requests - Successes;
    public decimal Retries { get; private set; }
    public decimal Tokens { get; private set; }
    public decimal Input { get; private set; }
    public decimal Output { get; private set; }
    public decimal CacheRead { get; private set; }
    public decimal CacheWrite { get; private set; }
    public decimal Reasoning { get; private set; }
    public long InputReported { get; private set; }
    public long OutputReported { get; private set; }
    public long ReadReported { get; private set; }
    public long WriteReported { get; private set; }
    public long ReasoningReported { get; private set; }
    public long UsageReported { get; private set; }
    public long IncompleteUsage { get; private set; }
    public long InvalidUsage { get; private set; }
    public long CacheMeasured { get; private set; }
    public long CacheUpperBound { get; private set; }
    public decimal MeasuredPrompt { get; private set; }
    public decimal MeasuredRead { get; private set; }
    public decimal Cost { get; private set; }
    public long Priced { get; private set; }
    public long PartiallyPriced { get; private set; }
    public long Unpriced => Requests - Priced;
    public decimal DecodeTokens { get; private set; }
    public double DecodeSeconds { get; private set; }
    public long SpeedMeasured { get; private set; }
    public double FirstContentSeconds { get; private set; }
    public long FirstContentMeasured { get; private set; }
    public double? HitRate => MeasuredPrompt > 0 ? (double)(100m * MeasuredRead / MeasuredPrompt) : null;
    public double? Speed => DecodeSeconds > 0 ? (double)DecodeTokens / DecodeSeconds : null;
    public double? FirstContent => FirstContentMeasured > 0 ? FirstContentSeconds / FirstContentMeasured : null;
    public double? SuccessRate => Requests > 0 ? 100.0 * Successes / Requests : null;

    public double Value(UsageMetric metric) => metric switch
    {
        UsageMetric.Cost => (double)Cost,
        UsageMetric.Requests => Requests,
        UsageMetric.Speed => Speed ?? 0,
        _ => (double)Tokens,
    };

    public void Add(UsageRecord row)
    {
        Requests++;
        if (row.Outcome == RequestOutcome.Success) Successes++;
        Retries += row.Retries;
        if (row.Tokens is { } n)
        {
            Tokens += n.KnownTokens;
            if (n.HasUsage) UsageReported++;
            if (!n.Complete) IncompleteUsage++;
            if (n.Invalid) InvalidUsage++;
            if (n.Input is { } input) { Input += input; InputReported++; }
            if (n.Output is { } output) { Output += output; OutputReported++; }
            if (n.CacheRead is { } read) { CacheRead += read; ReadReported++; }
            if (n.CacheWrite is { } write) { CacheWrite += write; WriteReported++; }
            if (n.Reasoning is { } reasoning) { Reasoning += reasoning; ReasoningReported++; }
            if (n.HitRate is not null)
            {
                MeasuredPrompt += n.Prompt!.Value;
                MeasuredRead += n.CacheRead!.Value;
                CacheMeasured++;
                if (n.PromptIsLowerBound) CacheUpperBound++;
            }
        }
        else IncompleteUsage++;
        if (row.Quote.Amount is { } amount)
        {
            Cost += amount;
            Priced++;
            if (row.Quote.IsLowerBound) PartiallyPriced++;
        }
        if (row.Usage?.Decode() is { } decode)
        {
            DecodeTokens += decode.Tokens;
            DecodeSeconds += decode.Seconds;
            SpeedMeasured++;
        }
        if (row.Usage is { IsStreaming: true, FirstContentSeconds: > 0 } usage
            && double.IsFinite(usage.FirstContentSeconds.Value)
            && usage.DurationSeconds >= usage.FirstContentSeconds)
        {
            FirstContentSeconds += usage.FirstContentSeconds.Value;
            FirstContentMeasured++;
        }
    }
}

public sealed record UsageGroup(UsageOption Identity, UsageTotals Totals);
public sealed class UsageBucket
{
    public DateTime Start { get; init; }
    public DateOnly LastDate { get; init; }
    public UsageTotals Totals { get; } = new();
    public Dictionary<string, UsageTotals> Parts { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal void Add(UsageRecord row, UsageDimension dimension)
    {
        Totals.Add(row);
        var id = row.Group(dimension).Id;
        if (!Parts.TryGetValue(id, out var totals)) Parts[id] = totals = new UsageTotals();
        totals.Add(row);
    }
}

public sealed record UsageReport
{
    public required UsageQuery Query { get; init; }
    public required DateOnly FirstDate { get; init; }
    public required IReadOnlyList<UsageRecord> Records { get; init; }
    public required UsageTotals Totals { get; init; }
    public required IReadOnlyList<UsageGroup> Groups { get; init; }
    public required IReadOnlyList<UsageBucket> Series { get; init; }
    public required IReadOnlyDictionary<DateOnly, UsageTotals> Heatmap { get; init; }
    public required IReadOnlyList<UsageOption> Providers { get; init; }
    public required IReadOnlyList<UsageOption> Models { get; init; }
    public required IReadOnlyList<UsageOption> Keys { get; init; }
    public required IReadOnlyList<UsageOption> Clients { get; init; }
    public required string Bucket { get; init; }
    public long UnplacedHours { get; init; }

    public static DateOnly HeatmapFirst(DateOnly today) => today.AddDays(-((int)today.DayOfWeek + 6) % 7 - 52 * 7);

    public static UsageReport Build(IReadOnlyList<StoredUsageRecord> stored, IReadOnlyDictionary<string, ClientType> routeClients,
        ModelPricing prices, UsageQuery query, CancellationToken token = default)
    {
        if (query.LastDate > query.Today || query.FirstDate > query.LastDate) throw new ArgumentException("请选择有效的日期范围");
        var all = new List<UsageRecord>(stored.Count);
        foreach (var r in stored)
        {
            token.ThrowIfCancellationRequested();
            if (r.Request.Outcome is null || r.Request.RequestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal)) continue;
            all.Add(UsageRecord.From(r, routeClients, prices));
        }
        // 防御性去重放在筛选之前：同一请求不能在两个不同日期各出现一次。
        all = all.GroupBy(r => (r.RouteId, r.RequestId)).Select(g => g.OrderByDescending(r => r.Date).ThenByDescending(r => r.CompletedAt).First()).ToList();
        var matching = all.Where(r => (query.Client.Length == 0 || r.Client == query.Client)
            && (query.Provider.Length == 0 || r.ProviderId == query.Provider)
            && (query.Model.Length == 0 || string.Equals(r.Model, query.Model, StringComparison.OrdinalIgnoreCase))
            && (query.Key.Length == 0 || r.KeyIdentity == query.Key)
            && (query.Outcome is null || r.Outcome == query.Outcome)).ToList();
        var first = query.FirstDate ?? (matching.Count == 0 ? query.LastDate : matching.Min(r => r.Date));
        if (first > query.LastDate) first = query.LastDate;
        var selected = matching.Where(r => r.Date >= first && r.Date <= query.LastDate)
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.CompletedAt).ThenBy(r => r.RequestId, StringComparer.Ordinal).ToList();
        var total = new UsageTotals();
        var groups = new Dictionary<string, UsageGroup>(StringComparer.OrdinalIgnoreCase);
        var days = query.LastDate.DayNumber - first.DayNumber + 1;
        // 保留每个自然日；超过 90 天按周合并，最长显示 104 个桶，其后按月，避免不可读的密集柱子。
        var kind = days == 1 ? "hour" : days <= 90 ? "day" : days <= 728 ? "week" : "month";
        var series = BuildBuckets(first, query.LastDate, kind);
        var byStart = series.ToDictionary(b => b.Start);
        long unplaced = 0;
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            total.Add(row);
            var group = row.Group(query.Dimension);
            if (!groups.TryGetValue(group.Id, out var part)) groups[group.Id] = part = new UsageGroup(group, new UsageTotals());
            part.Totals.Add(row);
            var index = BucketStart(row.Date, row.CompletedAt, first, kind);
            if (index is null) { unplaced++; continue; }
            if (byStart.TryGetValue(index.Value, out var bucket)) bucket.Add(row, query.Dimension);
        }
        var heat = new Dictionary<DateOnly, UsageTotals>();
        var heatFirst = HeatmapFirst(query.Today);
        foreach (var row in matching.Where(r => r.Date >= heatFirst && r.Date <= query.Today))
        {
            if (!heat.TryGetValue(row.Date, out var value)) heat[row.Date] = value = new UsageTotals();
            value.Add(row);
        }
        return new UsageReport
        {
            Query = query, FirstDate = first, Records = selected, Totals = total,
            Groups = groups.Values.ToList(), Series = series, Heatmap = heat, Bucket = kind, UnplacedHours = unplaced,
            Providers = Options(all, UsageDimension.Provider), Models = Options(all, UsageDimension.Model),
            Keys = Options(all, UsageDimension.Key), Clients = Options(all, UsageDimension.Client),
        };
    }

    private static IReadOnlyList<UsageOption> Options(IEnumerable<UsageRecord> records, UsageDimension dimension) => records
        .Select(r => r.Group(dimension)).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
        .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static DateTime? BucketStart(DateOnly date, DateTime? time, DateOnly first, string kind) => kind switch
    {
        "hour" => time is { } t ? date.ToDateTime(new TimeOnly(t.Hour, 0)) : null,
        "week" => first.AddDays((date.DayNumber - first.DayNumber) / 7 * 7).ToDateTime(TimeOnly.MinValue),
        "month" => new DateTime(date.Year, date.Month, 1),
        _ => date.ToDateTime(TimeOnly.MinValue),
    };

    private static List<UsageBucket> BuildBuckets(DateOnly first, DateOnly last, string kind)
    {
        var buckets = new List<UsageBucket>();
        if (kind == "hour")
        {
            for (var h = 0; h < 24; h++) buckets.Add(new UsageBucket { Start = first.ToDateTime(new TimeOnly(h, 0)), LastDate = first });
            return buckets;
        }
        var at = kind == "month" ? new DateOnly(first.Year, first.Month, 1) : first;
        while (at <= last)
        {
            var next = kind == "month" ? at.AddMonths(1) : at.AddDays(kind == "week" ? 7 : 1);
            buckets.Add(new UsageBucket { Start = at.ToDateTime(TimeOnly.MinValue), LastDate = next.AddDays(-1) > last ? last : next.AddDays(-1) });
            at = next;
        }
        return buckets;
    }
}
