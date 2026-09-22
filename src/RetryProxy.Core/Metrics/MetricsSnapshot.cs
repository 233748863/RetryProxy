using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RetryProxy.Core.Metrics;

[JsonConverter(typeof(JsonStringEnumConverter<RequestPhase>))]
public enum RequestPhase
{
    [JsonStringEnumMemberName("waiting_response")]
    WaitingResponse,

    [JsonStringEnumMemberName("waiting_generation")]
    WaitingGeneration,

    [JsonStringEnumMemberName("waiting_retry")]
    WaitingRetry,

    [JsonStringEnumMemberName("receiving_response")]
    ReceivingResponse,
}

public static class RequestPhaseExtensions
{
    public static string Label(this RequestPhase phase) => phase switch
    {
        RequestPhase.WaitingResponse => "等待回复",
        RequestPhase.WaitingGeneration => "等待生成",
        RequestPhase.WaitingRetry => "等待重试",
        _ => "接收内容",
    };
}

public sealed class ActiveRequest : IEquatable<ActiveRequest>
{
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("phase")]
    public RequestPhase Phase { get; set; }

    [JsonPropertyName("attempt")]
    public ulong Attempt { get; set; }

    public ActiveRequest Clone() => (ActiveRequest)MemberwiseClone();

    public bool Equals(ActiveRequest? other)
    {
        return other is not null
            && RequestId == other.RequestId
            && Method == other.Method
            && Path == other.Path
            && Phase == other.Phase
            && Attempt == other.Attempt;
    }

    public override bool Equals(object? obj) => Equals(obj as ActiveRequest);

    public override int GetHashCode() => HashCode.Combine(RequestId, Method, Path, Phase, Attempt);
}

public sealed class CacheSnapshot
{
    public const int HistoryLimit = 20;

    [JsonPropertyName("measured_requests")]
    public ulong MeasuredRequests { get; set; }

    [JsonPropertyName("unmeasured_requests")]
    public ulong UnmeasuredRequests { get; set; }

    [JsonPropertyName("input_tokens")]
    public ulong InputTokens { get; set; }

    [JsonPropertyName("cached_tokens")]
    public ulong CachedTokens { get; set; }

    [JsonPropertyName("cache_creation_tokens")]
    public ulong CacheCreationTokens { get; set; }

    [JsonPropertyName("cache_creation_measured_requests")]
    public ulong CacheCreationMeasuredRequests { get; set; }

    [JsonPropertyName("zero_hit_requests")]
    public ulong ZeroHitRequests { get; set; }

    [JsonPropertyName("zero_hit_input_tokens")]
    public ulong ZeroHitInputTokens { get; set; }

    [JsonPropertyName("client_key_requests")]
    public ulong ClientKeyRequests { get; set; }

    [JsonPropertyName("added_key_requests")]
    public ulong AddedKeyRequests { get; set; }

    [JsonPropertyName("missing_session_requests")]
    public ulong MissingSessionRequests { get; set; }

    [JsonPropertyName("unsupported_key_requests")]
    public ulong UnsupportedKeyRequests { get; set; }

    [JsonPropertyName("compatibility_fallbacks")]
    public ulong CompatibilityFallbacks { get; set; }

    /// <summary>最旧在前，独立于当日计数受上限约束。</summary>
    [JsonPropertyName("recent_requests")]
    public List<CacheRequest> RecentRequests { get; set; } = new();

    public double? HitRatePercent() => InputTokens > 0 ? 100.0 * CachedTokens / InputTokens : null;

    public double? RecentHitRatePercent()
    {
        decimal input = 0;
        decimal cached = 0;
        foreach (var request in RecentRequests)
        {
            if (request.Usage() is { } usage)
            {
                input += usage.Input;
                cached += usage.Cached;
            }
        }

        return input > 0 ? (double)(100m * cached / input) : null;
    }

    internal void Record(CacheRequest request)
    {
        if (request.Usage() is { } usage)
        {
            ulong inputTokens;
            ulong cachedTokens;
            ulong createdTokens;
            try
            {
                inputTokens = checked(InputTokens + usage.Input);
                cachedTokens = checked(CachedTokens + usage.Cached);
                createdTokens = checked(CacheCreationTokens + (request.CacheCreationTokens ?? 0));
            }
            catch (OverflowException)
            {
                return;
            }

            InputTokens = inputTokens;
            CachedTokens = cachedTokens;
            CacheCreationTokens = createdTokens;
            MeasuredRequests = Saturating.Add(MeasuredRequests, 1);
            if (request.CacheCreationTokens is not null)
            {
                CacheCreationMeasuredRequests = Saturating.Add(CacheCreationMeasuredRequests, 1);
            }

            if (usage.Cached == 0)
            {
                ZeroHitRequests = Saturating.Add(ZeroHitRequests, 1);
                ZeroHitInputTokens += usage.Input;
            }
        }
        else
        {
            UnmeasuredRequests = Saturating.Add(UnmeasuredRequests, 1);
        }

        if (RecentRequests.Count == HistoryLimit)
        {
            RecentRequests.RemoveAt(0);
        }

        RecentRequests.Add(request);
    }

    public CacheSnapshot Clone()
    {
        var clone = (CacheSnapshot)MemberwiseClone();
        clone.RecentRequests = new List<CacheRequest>(RecentRequests.Count);
        foreach (var request in RecentRequests)
        {
            clone.RecentRequests.Add(request.Clone());
        }

        return clone;
    }
}

public sealed class MetricsSnapshot
{
    [JsonPropertyName("statistics_date")]
    public string StatisticsDate { get; set; } = string.Empty;

    [JsonPropertyName("historical_unfinished_requests")]
    public ulong HistoricalUnfinishedRequests { get; set; }

    [JsonPropertyName("restored_from_legacy_logs")]
    public bool RestoredFromLegacyLogs { get; set; }

    [JsonPropertyName("statistics_warning")]
    public string? StatisticsWarning { get; set; }

    [JsonPropertyName("total_requests")]
    public ulong TotalRequests { get; set; }

    [JsonPropertyName("active_requests")]
    public ulong ActiveRequests { get; set; }

    [JsonPropertyName("successful_requests")]
    public ulong SuccessfulRequests { get; set; }

    [JsonPropertyName("retry_count")]
    public ulong RetryCount { get; set; }

    [JsonPropertyName("failed_requests")]
    public ulong FailedRequests { get; set; }

    [JsonPropertyName("requests")]
    public List<ActiveRequest> Requests { get; set; } = new();

    [JsonPropertyName("cache")]
    public CacheSnapshot Cache { get; set; } = new();

    [JsonPropertyName("gpt_cache")]
    public CacheSnapshot GptCache { get; set; } = new();

    public MetricsSnapshot Clone()
    {
        var clone = (MetricsSnapshot)MemberwiseClone();
        clone.Requests = new List<ActiveRequest>(Requests.Count);
        foreach (var request in Requests)
        {
            clone.Requests.Add(request.Clone());
        }

        clone.Cache = Cache.Clone();
        clone.GptCache = GptCache.Clone();
        return clone;
    }
}

internal static class Saturating
{
    public static ulong Add(ulong left, ulong right)
    {
        var result = left + right;
        return result < left ? ulong.MaxValue : result;
    }

    public static ulong Sub(ulong left, ulong right) => left >= right ? left - right : 0;
}
