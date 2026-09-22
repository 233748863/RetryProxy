using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Cache;

/// <summary>
/// 为 OpenAI 兼容接口补充 <c>prompt_cache_key</c>（对应 prompt_cache.rs 的 PromptCache）。
/// </summary>
public sealed class PromptCache
{
    public const int MaxRequestInspectionBytes = 4 * 1024 * 1024;
    public const int MaxUnsupportedScopes = 128;
    public const int MaxCacheErrorBytes = 64 * 1024;

    private static readonly string[] ScopeHeaders =
    {
        "authorization", "x-api-key", "api-key", "openai-organization", "openai-project",
    };

    private static readonly string[] IntegrityHeaders =
    {
        "content-md5", "digest", "content-digest", "signature", "signature-input", "x-amz-content-sha256",
    };

    private readonly byte[] _namespace;
    private readonly object _lock = new();
    private readonly LinkedList<byte[]> _unsupported = new();

    public PromptCache(string upstream)
    {
        _namespace = SHA256.HashData(Encoding.UTF8.GetBytes(upstream));
    }

    public CacheRequestBody Prepare(string method, string path, HeaderList headers, ReadOnlyMemory<byte> body, bool background)
    {
        var request = new CacheRequestBody(body);
        var endpoint = OpenAiEndpoint(path);
        if (endpoint is null)
        {
            return request;
        }

        if (background
            || !string.Equals(method, "POST", StringComparison.Ordinal)
            || body.Length > MaxRequestInspectionBytes
            || !EditableBody(headers))
        {
            return request;
        }

        using var document = JsonText.TryParse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return request;
        }

        var root = document.RootElement;
        var model = root.Get("model").AsString();
        if (model is null || !IsGptModel(model))
        {
            return request;
        }

        // 客户端自己带了字段（哪怕是 null / 空 / 非法值）就归客户端管。
        if (root.TryGetProperty("prompt_cache_key", out _))
        {
            request.State = CacheKeyState.Client;
            return request;
        }

        var session = SessionIdentity(headers, root);
        if (session is null)
        {
            request.State = CacheKeyState.MissingSession;
            return request;
        }

        using var scopeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashPart(scopeHash, _namespace);
        HashPart(scopeHash, Encoding.UTF8.GetBytes(endpoint));
        HashPart(scopeHash, Encoding.UTF8.GetBytes(model));
        foreach (var name in ScopeHeaders)
        {
            HashPart(scopeHash, Encoding.UTF8.GetBytes(name));
            var values = headers.GetAll(name).ToList();
            HashPart(scopeHash, BitConverter.GetBytes((ulong)values.Count));
            foreach (var value in values)
            {
                HashPart(scopeHash, Encoding.UTF8.GetBytes(value));
            }
        }

        var scope = scopeHash.GetHashAndReset();
        lock (_lock)
        {
            if (_unsupported.Any(item => item.AsSpan().SequenceEqual(scope)))
            {
                request.State = CacheKeyState.Unsupported;
                return request;
            }
        }

        using var keyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashPart(keyHash, "retry-proxy:prompt-cache:v1"u8.ToArray());
        HashPart(keyHash, scope);
        HashPart(keyHash, Encoding.UTF8.GetBytes(session));
        var digest = Convert.ToHexString(keyHash.GetHashAndReset()).ToLowerInvariant();
        var key = "rp1_" + digest[..60];

        // 只拼接一个顶层字段，不重新序列化任何已有的字符串、数字或工具定义。
        var original = body.Span;
        var closing = original.Length - 1;
        while (closing >= 0 && IsAsciiWhitespace(original[closing]))
        {
            closing--;
        }

        var addition = Encoding.UTF8.GetBytes($",\"prompt_cache_key\":\"{key}\"");
        var amended = new byte[original.Length + addition.Length];
        original[..closing].CopyTo(amended);
        addition.CopyTo(amended.AsSpan(closing));
        original[closing..].CopyTo(amended.AsSpan(closing + addition.Length));
        request.Amended = amended;
        request.Scope = scope;
        request.State = CacheKeyState.Added;
        return request;
    }

    public void Reject(CacheRequestBody request)
    {
        if (request.Scope is { } scope)
        {
            request.Scope = null;
            lock (_lock)
            {
                if (!_unsupported.Any(item => item.AsSpan().SequenceEqual(scope)))
                {
                    if (_unsupported.Count == MaxUnsupportedScopes)
                    {
                        _unsupported.RemoveFirst();
                    }

                    _unsupported.AddLast(scope);
                }
            }
        }

        request.Amended = null;
        request.State = CacheKeyState.Unsupported;
    }

    public static string? OpenAiEndpoint(string path)
    {
        return path.TrimEnd('/') switch
        {
            "/responses" or "/v1/responses" => "responses",
            "/chat/completions" or "/v1/chat/completions" => "chat/completions",
            _ => null,
        };
    }

    public static bool IsGptModel(string model)
    {
        if (!model.StartsWith("gpt-", StringComparison.Ordinal))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetByteCount(model);
        if (bytes > 128)
        {
            return false;
        }

        foreach (var character in model)
        {
            if (character <= ' ' || character > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>只有上游明确拒绝代理补充的字段，才允许兼容重发。</summary>
    public static bool RejectsCacheKey(ReadOnlySpan<byte> body)
    {
        using var document = JsonText.TryParse(body);
        if (document is null)
        {
            return false;
        }

        var value = document.RootElement;
        var error = value.Get("error");
        if (error.IsObject())
        {
            var code = error.Get("code").AsString();
            if (error.Get("param").AsString() == "prompt_cache_key"
                && code is "unknown_parameter" or "unsupported_parameter" or "unrecognized_parameter")
            {
                return true;
            }

            var message = error.Get("message").AsString();
            if (message is not null && Encoding.UTF8.GetByteCount(message) <= 256)
            {
                var normalized = message.Trim().TrimEnd('.').ToLowerInvariant();
                foreach (var prefix in new[] { "unknown parameter: ", "unsupported parameter: ", "unrecognized request argument supplied: " })
                {
                    if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        var parameter = normalized[prefix.Length..];
                        if (parameter is "prompt_cache_key" or "'prompt_cache_key'" or "\"prompt_cache_key\"")
                        {
                            return true;
                        }
                    }
                }
            }
        }

        var detail = value.Get("detail");
        if (!detail.IsArray())
        {
            return false;
        }

        foreach (var item in detail!.Value.EnumerateArray())
        {
            if (item.Get("type").AsString() != "extra_forbidden")
            {
                continue;
            }

            var loc = item.Get("loc");
            if (loc.IsArray() && loc!.Value.GetArrayLength() == 2
                && loc.Value[0].AsString() == "body" && loc.Value[1].AsString() == "prompt_cache_key")
            {
                return true;
            }
        }

        return false;
    }

    private static bool EditableBody(HeaderList headers)
    {
        var encoding = headers.Get("content-encoding");
        if (encoding is not null && !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var contentType = headers.Get("content-type");
        if (contentType is not null)
        {
            var mime = contentType.Split(';')[0].Trim();
            if (!string.Equals(mime, "application/json", StringComparison.OrdinalIgnoreCase)
                && !mime.ToLowerInvariant().EndsWith("+json", StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var name in IntegrityHeaders)
        {
            if (headers.Contains(name))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ValidIdentity(string? value)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) > 256)
        {
            return null;
        }

        foreach (var character in value)
        {
            if (character <= ' ' || character > '~')
            {
                return null;
            }
        }

        return value;
    }

    private static string? TurnIdentity(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > 16 * 1024)
        {
            return null;
        }

        using var document = JsonText.TryParse(value);
        if (document is null)
        {
            return null;
        }

        var metadata = document.RootElement;
        var thread = metadata.Get("thread_id") ?? metadata.Get("threadId");
        return ValidIdentity(thread.AsString());
    }

    private static string? SessionIdentity(HeaderList headers, JsonElement fields)
    {
        foreach (var name in new[] { "thread_id", "session_id" })
        {
            var session = ValidIdentity(headers.Get(name));
            if (session is not null)
            {
                return session;
            }
        }

        var turnHeader = headers.Get("x-codex-turn-metadata");
        if (turnHeader is not null)
        {
            var session = TurnIdentity(turnHeader);
            if (session is not null)
            {
                return session;
            }
        }

        var turnBody = fields.Get("client_metadata").Get("x-codex-turn-metadata").AsString();
        if (turnBody is not null)
        {
            var session = TurnIdentity(turnBody);
            if (session is not null)
            {
                return session;
            }
        }

        var conversation = fields.Get("conversation");
        if (conversation is null)
        {
            return null;
        }

        var identity = conversation.AsString() ?? conversation.Get("id").AsString();
        return ValidIdentity(identity);
    }

    private static void HashPart(IncrementalHash hash, byte[] value)
    {
        hash.AppendData(BitConverter.GetBytes((ulong)value.Length));
        hash.AppendData(value);
    }

    private static bool IsAsciiWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0c;
}
