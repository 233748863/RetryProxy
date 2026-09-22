using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using RetryProxy.Core.Cache;

namespace RetryProxy.Core.Metrics;

[JsonConverter(typeof(JsonStringEnumConverter<RequestOutcome>))]
public enum RequestOutcome
{
    [JsonStringEnumMemberName("success")]
    Success,

    [JsonStringEnumMemberName("failure")]
    Failure,
}

/// <summary>
/// 当日统计里一条请求的记录（对应 daily.rs 的 DailyRequest）。
/// 每行覆盖该请求上一条记录，重放两次无副作用；不含提示词、查询串、凭证或上游/会话标识。
/// </summary>
public sealed class DailyRequest
{
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; set; }

    [JsonPropertyName("sequence")]
    public ulong Sequence { get; set; }

    [JsonPropertyName("outcome")]
    public RequestOutcome? Outcome { get; set; }

    [JsonPropertyName("retry_count")]
    public ulong RetryCount { get; set; }

    [JsonPropertyName("last_retry_attempt")]
    public ulong? LastRetryAttempt { get; set; }

    [JsonPropertyName("cache_key")]
    public CacheKeyState? CacheKey { get; set; }

    [JsonPropertyName("cache_fallback")]
    public bool CacheFallback { get; set; }

    [JsonPropertyName("cache")]
    public CacheRequest? Cache { get; set; }

    public void Retry(ulong attempt)
    {
        if (LastRetryAttempt is null || attempt > LastRetryAttempt.Value)
        {
            LastRetryAttempt = attempt;
            RetryCount = Saturating.Add(RetryCount, 1);
        }
    }

    public DailyRequest Clone()
    {
        var clone = (DailyRequest)MemberwiseClone();
        clone.Cache = Cache?.Clone();
        return clone;
    }
}

/// <summary>对当日记录的一次变更。</summary>
internal abstract record DailyChange
{
    public sealed record Started : DailyChange;

    public sealed record Succeeded(CacheRequest? Cache) : DailyChange;

    public sealed record Failed : DailyChange;

    public sealed record Retry(ulong Attempt) : DailyChange;

    public sealed record CacheKey(CacheKeyState State) : DailyChange;

    public sealed record CacheFallback : DailyChange;
}

/// <summary>
/// 当日统计的持久化端（M3 实现 jsonl 日志）；M2 只定义接口，内存态不落盘。
/// </summary>
public interface IDailyJournal
{
    /// <summary>追加一条记录；失败时抛出 <see cref="System.IO.IOException"/>。</summary>
    void Append(DailyRequest record);
}

/// <summary>打开某一天日志的结果。</summary>
public sealed class DailyJournalOpenResult
{
    public IDailyJournal? Journal { get; init; }

    public IReadOnlyDictionary<string, DailyRequest> Records { get; init; } = new Dictionary<string, DailyRequest>();

    public bool RestoredFromLegacyLogs { get; init; }

    public string? Warning { get; init; }

    /// <summary>打开失败时的错误说明（对应 io::ErrorKind 文本）。</summary>
    public string? OpenErrorKind { get; init; }
}

/// <summary>日志存储描述；M3 提供实现。</summary>
public interface IDailyStorage
{
    DailyJournalOpenResult Open(DateOnly date, bool importLegacy);
}
