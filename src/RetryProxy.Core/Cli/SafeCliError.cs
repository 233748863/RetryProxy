using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetryProxy.Core.Cli;

/// <summary>
/// 把 CLI 返回的错误对象归纳成可写日志的中文说明（对应 keepalive_cli.rs 的 safe_cli_error）。
/// Key、原始错误文本与路径一律不带出，只保留错误码、分类与 16 位十六进制诊断编号。
/// </summary>
public static class SafeCliError
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] TextFields = { "message", "code", "type", "subtype", "result", "errors", "codexErrorInfo", "data" };

    private static readonly (string[] Markers, string Reason)[] Keywords =
    {
        (new[] { "401", "invalid_api_key", "authentication_error" }, "CLI 认证失败，请检查本机客户端登录或密钥配置"),
        (new[] { "403", "permission_denied" }, "CLI 请求被供应商拒绝（权限不足）"),
        (new[] { "429", "rate_limit" }, "CLI 请求触发供应商限流"),
        (new[] { "context_length", "context window" }, "CLI 会话达到供应商上下文限制，已清理会话"),
        (new[] { "model_not_found", "unsupported model" }, "本机 CLI 配置的模型不可用"),
        (new[] { "timeout", "timed out" }, "CLI 请求超时"),
        (new[] { "connection", "connect error", "network" }, "CLI 无法连接供应商"),
    };

    private static readonly string[] SafeFields =
    {
        "experimentalRawEvents", "persistExtendedHistory", "sandboxPolicy", "sandbox", "permissions", "approvalPolicy",
        "ephemeral", "cwd", "modelProvider", "developerInstructions", "mcp_servers", "config", "threadId", "clientInfo",
        "capabilities", "input",
    };

    private static readonly (string Marker, string Reason)[] DetailMarkers =
    {
        ("missing field", "缺少字段"),
        ("unknown field", "不支持字段"),
        ("invalid type", "字段类型不兼容"),
        ("unknown variant", "字段取值不兼容"),
    };

    private static readonly (string[] Markers, string Reason)[] DetailKeywords =
    {
        (new[] { "not initialized", "initialize must" }, "客户端初始化握手未完成"),
        (new[] { "experimentalapi", "experimental api" }, "错误涉及客户端实验协议能力"),
        (new[] { "failed to load config", "failed to load bootstrap configuration", "invalid config", "error loading config", "failed to parse config" }, "本机客户端配置加载或覆盖失败"),
        (new[] { "sandbox", "permissions", "approval policy", "approval_policy" }, "错误涉及本机客户端权限设置，未自动放宽权限"),
        (new[] { "ephemeral" }, "错误涉及本机客户端临时会话参数"),
        (new[] { "mcp" }, "错误涉及本机客户端工具服务配置"),
    };

    private static JsonNode? NonNull(JsonNode? node) => node is null || node.GetValueKind() == JsonValueKind.Null ? null : node;

    private static JsonNode? Pointer(JsonNode? node, params string[] path)
    {
        foreach (var segment in path)
        {
            node = node is JsonObject obj && obj.TryGetPropertyValue(segment, out var child) ? child : null;
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    internal static string ToJson(JsonNode? node) => node is null ? "null" : node.ToJsonString(Compact);

    public static string Describe(JsonNode? value)
    {
        var details = NonNull(Pointer(value, "params", "turn", "error"))
            ?? NonNull(Pointer(value, "params", "error"))
            ?? NonNull(Pointer(value, "error"))
            ?? value;
        long? code = null;
        if (details is JsonObject detailObject && detailObject.TryGetPropertyValue("code", out var codeNode) && codeNode is JsonValue codeValue && codeValue.TryGetValue<long>(out var parsedCode))
        {
            code = parsedCode;
        }

        var diagnosticId = Fnv1a64(ToJson(details)).ToString("x16");
        string text;
        if (details is JsonObject obj)
        {
            text = string.Join(" ", TextFields.Where(field => obj.ContainsKey(field)).Select(field => ToJson(obj[field])));
        }
        else
        {
            text = ToJson(details);
        }

        text = ToAsciiLowercase(text);
        var codeText = code is { } number ? $"（错误码 {number}）" : string.Empty;
        var protocolError = code switch
        {
            -32700 => "客户端无法解析请求",
            -32600 => "客户端拒绝请求",
            -32601 => "本机客户端不支持此调用，请检查版本兼容性",
            -32602 => "客户端调用参数不兼容",
            -32603 => "本机客户端内部错误",
            _ => null,
        };
        if (protocolError is not null)
        {
            return $"{protocolError}{codeText}；{SafeProtocolDetail(text)}；诊断编号 {diagnosticId}";
        }

        foreach (var (markers, reason) in Keywords)
        {
            if (markers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
            {
                return $"{reason}{codeText}；诊断编号 {diagnosticId}";
            }
        }

        return $"CLI 未完成本轮回复{codeText}；诊断编号 {diagnosticId}；原始错误输出不写入日志，以保护认证信息";
    }

    internal static string SafeProtocolDetail(string text)
    {
        if (text.Contains("invalid transport", StringComparison.Ordinal) && text.Contains("mcp_servers", StringComparison.Ordinal))
        {
            return "后台工具连接配置无效";
        }

        foreach (var (marker, reason) in DetailMarkers)
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var remainder = text[(index + marker.Length)..].TrimStart(' ', '\t', '\r', '\n', ':');
            var fields = SafeFields.Where(field =>
            {
                var lower = ToAsciiLowercase(field);
                return remainder.StartsWith($"`{lower}`", StringComparison.Ordinal)
                    || remainder.StartsWith($"\\\"{lower}\\\"", StringComparison.Ordinal)
                    || remainder.StartsWith($"'{lower}'", StringComparison.Ordinal);
            }).ToList();
            return fields.Count == 0 ? $"{reason}（字段名未列入安全白名单）" : $"{reason}：{string.Join("、", fields)}";
        }

        foreach (var (markers, reason) in DetailKeywords)
        {
            if (markers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
            {
                return reason;
            }
        }

        return "拒绝原因未识别，原始错误输出不写入日志";
    }

    private static string ToAsciiLowercase(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character is >= 'A' and <= 'Z' ? (char)(character + 32) : character);
        }

        return builder.ToString();
    }

    private static ulong Fnv1a64(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in Encoding.UTF8.GetBytes(text))
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}
