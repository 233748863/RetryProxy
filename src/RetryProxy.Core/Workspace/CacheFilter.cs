using System;
using RetryProxy.Core.Metrics;

namespace RetryProxy.Core.Workspace;

/// <summary>缓存明细的筛选（对应 ui/cache.rs 的 CacheFilter）。</summary>
public enum CacheFilter
{
    All,
    ZeroHit,
    Unmeasured,
}

public static class CacheFilterExtensions
{
    public static readonly CacheFilter[] All = [CacheFilter.All, CacheFilter.ZeroHit, CacheFilter.Unmeasured];

    public static string Label(this CacheFilter filter) => filter switch
    {
        CacheFilter.ZeroHit => "完全未命中",
        CacheFilter.Unmeasured => "未计入",
        _ => "全部",
    };

    public static bool Accepts(this CacheFilter filter, CacheRequest request) => filter switch
    {
        CacheFilter.ZeroHit => request.Usage() is { Cached: 0 },
        CacheFilter.Unmeasured => request.Usage() is null,
        _ => true,
    };

    public static CacheFilter Parse(string key) => key switch
    {
        "zero-hit" => CacheFilter.ZeroHit,
        "unmeasured" => CacheFilter.Unmeasured,
        _ => CacheFilter.All,
    };
}
