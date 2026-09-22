using System.Text.Json.Serialization;

namespace RetryProxy.Core.Cache;

/// <summary>代理对 <c>prompt_cache_key</c> 的处理结果（对应 prompt_cache.rs 的 CacheKeyState）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CacheKeyState>))]
public enum CacheKeyState
{
    [JsonStringEnumMemberName("unchanged")]
    Unchanged,

    [JsonStringEnumMemberName("client")]
    Client,

    [JsonStringEnumMemberName("added")]
    Added,

    [JsonStringEnumMemberName("missing_session")]
    MissingSession,

    [JsonStringEnumMemberName("unsupported")]
    Unsupported,
}

public static class CacheKeyStateExtensions
{
    public static string? Label(this CacheKeyState state) => state switch
    {
        CacheKeyState.Client => "客户端已设置",
        CacheKeyState.Added => "代理已补全",
        CacheKeyState.MissingSession => "未补全（缺少会话信息）",
        CacheKeyState.Unsupported => "保持原请求（上游不支持）",
        _ => null,
    };
}
