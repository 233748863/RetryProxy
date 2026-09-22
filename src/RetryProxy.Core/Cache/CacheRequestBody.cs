using System;

namespace RetryProxy.Core.Cache;

/// <summary>一次请求的正文及代理补充缓存标识后的版本。</summary>
public sealed class CacheRequestBody
{
    internal CacheRequestBody(ReadOnlyMemory<byte> original)
    {
        Original = original;
    }

    public ReadOnlyMemory<byte> Original { get; }

    internal ReadOnlyMemory<byte>? Amended { get; set; }

    internal byte[]? Scope { get; set; }

    public CacheKeyState State { get; internal set; } = CacheKeyState.Unchanged;

    public ReadOnlyMemory<byte> Body => Amended ?? Original;

    public bool IsAmended => Amended is not null;
}
