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

    /// <summary>
    /// 本次成功请求实际使用的供应商 Key（只存 ID 与显示名称，不含密钥）。
    /// 旧版日志与保活请求没有这一段，读出来是空字符串，界面显示为"未记录"。
    /// </summary>
    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("key_name")]
    public string KeyName { get; set; } = string.Empty;

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

    public sealed record Succeeded(CacheRequest? Cache, string KeyId = "", string KeyName = "") : DailyChange;

    public sealed record Failed : DailyChange;

    public sealed record Retry(ulong Attempt) : DailyChange;

    public sealed record CacheKey(CacheKeyState State) : DailyChange;

    public sealed record CacheFallback : DailyChange;
}

/// <summary>当日统计的持久化端（SQLite 实现见 <see cref="SqliteDailyJournal"/>）。</summary>
public interface IDailyJournal
{
    /// <summary>写入一条记录（UPSERT，幂等）；失败时抛出 <see cref="System.IO.IOException"/> 或 SQLite 异常。</summary>
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

/// <summary>当日统计存储；实现见 <see cref="SqliteDailyStorage"/>。</summary>
public interface IDailyStorage
{
    DailyJournalOpenResult Open(DateOnly date, bool importLegacy);
}
