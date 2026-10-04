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

    /// <summary>
    /// 计算总输入。<see cref="CacheInputAccounting.IncludesCached"/> 直接用回复里的总输入量；
    /// <see cref="CacheInputAccounting.ExcludesCached"/>（Claude）用 未缓存输入 + 缓存读取 + 缓存写入。
    /// 上游没报缓存写入时按前两项相加，命中率因此是上限值（分母少了未知的写入量）。
    /// 缺 未缓存输入 或 缓存读取、相加溢出时返回 null，调用方按“未获取”处理。
    /// </summary>
    public static ulong? TotalInputTokens(this CacheInputAccounting accounting, ulong? input, ulong? cached, ulong? created)
    {
        switch (accounting)
        {
            case CacheInputAccounting.IncludesCached:
                return input;
            default:
                if (input is null || cached is null)
                {
                    return null;
                }

                try
                {
                    return checked(input.Value + cached.Value + (created ?? 0));
                }
                catch (OverflowException)
                {
                    return null;
                }
        }
    }
}
