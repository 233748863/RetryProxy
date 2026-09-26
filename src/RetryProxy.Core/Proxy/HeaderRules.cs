using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Proxy;

/// <summary>请求头 / 响应头 / 内部元数据的清理规则（对应 proxy.rs 的 copy_*_headers 与 strip_internal_request_metadata）。</summary>
internal static class HeaderRules
{
    public const string KeepAliveMarkerHeader = "x-retry-keepalive";
    public const string PreparationIdHeader = "x-retry-preparation-id";

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailer", "transfer-encoding", "upgrade",
    };

    public static HashSet<string> ConnectionTokens(HeaderList headers)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in headers.GetAll("connection"))
        {
            foreach (var token in value.Split(','))
            {
                var trimmed = token.Trim().ToLowerInvariant();
                if (trimmed.Length > 0)
                {
                    tokens.Add(trimmed);
                }
            }
        }

        return tokens;
    }

    public static HeaderList CopyRequestHeaders(HeaderList source)
    {
        var tokens = ConnectionTokens(source);
        var output = new HeaderList();
        foreach (var (name, value) in source)
        {
            var lower = name.ToLowerInvariant();
            if (HopByHop.Contains(lower)
                || lower is "host" or "content-length" or KeepAliveMarkerHeader or PreparationIdHeader
                || tokens.Contains(lower))
            {
                continue;
            }

            if (lower == "x-codex-turn-metadata")
            {
                var metadata = JsonText.TryParseNode(value);
                if (metadata is JsonObject fields && fields.Remove("retry_proxy_keepalive"))
                {
                    if (fields.Count > 0)
                    {
                        output.Append(name, JsonText.Serialize(fields));
                    }

                    continue;
                }
            }

            output.Append(name, value);
        }

        return output;
    }

    public static HeaderList CopyResponseHeaders(HeaderList source)
    {
        var tokens = ConnectionTokens(source);
        var output = new HeaderList();
        foreach (var (name, value) in source)
        {
            var lower = name.ToLowerInvariant();
            if (HopByHop.Contains(lower) || lower == "content-length" || tokens.Contains(lower))
            {
                continue;
            }

            output.Append(name, value);
        }

        return output;
    }

    public static ReadOnlyMemory<byte> DisableClaudeToolUse(ReadOnlyMemory<byte> body)
    {
        if (JsonText.TryParseNode(body.Span) is not JsonObject request
            || request["tools"] is not JsonArray { Count: > 0 })
        {
            return body;
        }

        request["tool_choice"] = new JsonObject { ["type"] = "none" };
        return JsonSerializer.SerializeToUtf8Bytes(request, JsonText.Compact);
    }

    /// <summary>去掉客户端元数据里代理自己塞进去的保活标记；不是那种结构的正文原样返回。</summary>
    public static ReadOnlyMemory<byte> StripInternalRequestMetadata(ReadOnlyMemory<byte> body)
    {
        if (JsonText.TryParseNode(body.Span) is not JsonObject request)
        {
            return body;
        }

        if (request["client_metadata"] is not JsonObject metadata)
        {
            return body;
        }

        if (metadata["x-codex-turn-metadata"] is not JsonValue turnValue || !turnValue.TryGetValue<string>(out var turnText))
        {
            return body;
        }

        if (JsonText.TryParseNode(turnText) is not JsonObject fields)
        {
            return body;
        }

        if (!fields.Remove("retry_proxy_keepalive"))
        {
            return body;
        }

        if (fields.Count == 0)
        {
            metadata.Remove("x-codex-turn-metadata");
        }
        else
        {
            metadata["x-codex-turn-metadata"] = JsonValue.Create(JsonText.Serialize(fields));
        }

        if (metadata.Count == 0)
        {
            request.Remove("client_metadata");
        }

        return JsonSerializer.SerializeToUtf8Bytes(request, JsonText.Compact);
    }
}
