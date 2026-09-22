using System;
using System.Text.Json.Serialization;

namespace RetryProxy.Core.Metrics;

/// <summary>只保留已完成的用户请求用量；不含提示词、凭证或会话标识。</summary>
public sealed class CacheRequest : IEquatable<CacheRequest>
{
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("completed_at_unix_ms")]
    public long CompletedAtUnixMs { get; set; }

    [JsonPropertyName("input_tokens")]
    public ulong? InputTokens { get; set; }

    [JsonPropertyName("cached_tokens")]
    public ulong? CachedTokens { get; set; }

    [JsonPropertyName("cache_creation_tokens")]
    public ulong? CacheCreationTokens { get; set; }

    [JsonPropertyName("input_accounting")]
    public CacheInputAccounting InputAccounting { get; set; }

    [JsonPropertyName("cache_key_status")]
    public string CacheKeyStatus { get; set; } = string.Empty;

    public ulong? TotalInputTokens() => InputAccounting.TotalInputTokens(InputTokens, CachedTokens, CacheCreationTokens);

    /// <summary>返回 (总输入, 缓存命中)；用量不可信时为 null。</summary>
    public (ulong Input, ulong Cached)? Usage()
    {
        var input = TotalInputTokens();
        if (input is null || CachedTokens is null)
        {
            return null;
        }

        return input.Value > 0 && CachedTokens.Value <= input.Value ? (input.Value, CachedTokens.Value) : null;
    }

    public bool UsageIsInvalid()
    {
        var total = TotalInputTokens();
        if (total is not null && CachedTokens is not null && CachedTokens.Value > total.Value)
        {
            return true;
        }

        return InputAccounting == CacheInputAccounting.ExcludesCached
            && InputTokens is not null
            && CachedTokens is not null
            && CacheCreationTokens is not null
            && total is null;
    }

    public double? HitRatePercent()
    {
        var usage = Usage();
        return usage is null ? null : 100.0 * usage.Value.Cached / usage.Value.Input;
    }

    public CacheRequest Clone() => (CacheRequest)MemberwiseClone();

    public bool Equals(CacheRequest? other)
    {
        return other is not null
            && RequestId == other.RequestId
            && Model == other.Model
            && CompletedAtUnixMs == other.CompletedAtUnixMs
            && InputTokens == other.InputTokens
            && CachedTokens == other.CachedTokens
            && CacheCreationTokens == other.CacheCreationTokens
            && InputAccounting == other.InputAccounting
            && CacheKeyStatus == other.CacheKeyStatus;
    }

    public override bool Equals(object? obj) => Equals(obj as CacheRequest);

    public override int GetHashCode() => HashCode.Combine(RequestId, Model, CompletedAtUnixMs, InputTokens, CachedTokens, CacheCreationTokens, InputAccounting, CacheKeyStatus);
}
