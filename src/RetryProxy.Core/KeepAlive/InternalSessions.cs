using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.KeepAlive;

/// <summary>
/// 保活/准备会话的全局登记表：内部请求带的标记 → 取消令牌（对应 keepalive.rs 的 internal_sessions）。
/// </summary>
public static class InternalSessions
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, CancellationTokenSource> Sessions = new(StringComparer.Ordinal);

    public static void Register(string marker, CancellationTokenSource cancel)
    {
        lock (Lock)
        {
            Sessions[marker] = cancel;
        }
    }

    public static void Unregister(string marker)
    {
        lock (Lock)
        {
            Sessions.Remove(marker);
        }
    }

    public static bool IsEmpty
    {
        get
        {
            lock (Lock)
            {
                return Sessions.Count == 0;
            }
        }
    }

    public static CancellationToken? RequestCancel(HeaderList headers)
    {
        var marker = headers.Get("x-retry-keepalive");
        if (marker is null)
        {
            var turn = headers.Get("x-codex-turn-metadata");
            if (turn is null)
            {
                return null;
            }

            using var document = JsonText.TryParse(turn);
            marker = document?.RootElement.Get("retry_proxy_keepalive").AsString();
            if (marker is null)
            {
                return null;
            }
        }

        return SessionCancel(marker);
    }

    public static CancellationToken? BodyRequestCancel(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty || IsEmpty)
        {
            return null;
        }

        using var document = JsonText.TryParse(body);
        var turn = document?.RootElement.Get("client_metadata").Get("x-codex-turn-metadata").AsString();
        if (turn is null)
        {
            return null;
        }

        using var metadata = JsonText.TryParse(turn);
        var marker = metadata?.RootElement.Get("retry_proxy_keepalive").AsString();
        return marker is null ? null : SessionCancel(marker);
    }

    private static CancellationToken? SessionCancel(string marker)
    {
        lock (Lock)
        {
            return Sessions.TryGetValue(marker, out var cancel) ? cancel.Token : null;
        }
    }
}
