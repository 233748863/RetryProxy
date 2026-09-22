using System;
using System.Text.Json.Serialization;

namespace RetryProxy.Core.Metrics;

/// <summary>输入 token 是否已包含缓存命中部分（对应 metrics.rs 的 CacheInputAccounting）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CacheInputAccounting>))]
public enum CacheInputAccounting
{
    [JsonStringEnumMemberName("includes_cached")]
    IncludesCached,

    [JsonStringEnumMemberName("excludes_cached")]
    ExcludesCached,
}

public static class CacheInputAccountingRules
{
    public static CacheInputAccounting? ForPath(string path)
    {
        return path.TrimEnd('/') switch
        {
            "/responses" or "/v1/responses" or "/chat/completions" or "/v1/chat/completions" => CacheInputAccounting.IncludesCached,
            "/messages" or "/v1/messages" => CacheInputAccounting.ExcludesCached,
            _ => null,
        };
    }

    public static ulong? TotalInputTokens(this CacheInputAccounting accounting, ulong? input, ulong? cached, ulong? created)
    {
        switch (accounting)
        {
            case CacheInputAccounting.IncludesCached:
                return input;
            default:
                if (input is null || cached is null || created is null)
                {
                    return null;
                }

                try
                {
                    return checked(input.Value + cached.Value + created.Value);
                }
                catch (OverflowException)
                {
                    return null;
                }
        }
    }
}
