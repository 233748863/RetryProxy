using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Stats;

/// <summary>请求正文里与转发有关的元数据。</summary>
internal readonly struct RequestMetadata
{
    public RequestMetadata(string? model, bool stream, string? reasoningEffort = null, string? modelIdentity = null)
    {
        Model = model;
        Stream = stream;
        ReasoningEffort = reasoningEffort;
        ModelIdentity = modelIdentity;
    }

    public string? ModelIdentity { get; }

    public string? ReasoningEffort { get; }

    public string? Model { get; }

    public bool Stream { get; }

    public static RequestMetadata Parse(ReadOnlyMemory<byte> body)
    {
        using var document = JsonText.TryParse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        var root = document.RootElement;
        var model = DiagnosticText.CleanModel(root.Get("model").AsString());
        var stream = root.Get("stream") is { ValueKind: JsonValueKind.True };
        return new RequestMetadata(model, stream, ReadReasoningEffort(root), DiagnosticText.ModelIdentity(root.Get("model").AsString()));
    }

    // 显式等级才可对照；thinking 的模式、预算和推理 token 数不能换算成等级。
    public static string? ReadReasoningEffort(JsonElement value)
    {
        var effort = value.Pointer("/output_config/effort")
            ?? value.Pointer("/reasoning/effort")
            ?? value.Get("reasoning_effort");
        return DiagnosticText.CleanDiagnosticIdentifier(effort.AsString());
    }
}

/// <summary>日志里可安全出现的标识与文本清洗。</summary>
internal static class DiagnosticText
{
    public const int MaxDiagnosticIdentifierBytes = 128;

    // 比较完整原值的摘要，日志仍只展示有界文本；不把长模型名截断后误判为一致。
    public static string? ModelIdentity(string? value) => value is null ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string ComparisonDisplay(string value) => value.Replace('，', ',').Replace('（', '(').Replace('）', ')')
        // 日志差异标记使用半角括号；模型原值里的同名文本改用方括号，避免一致模型伪装成差异字段。
        .Replace("(不一致)", "[不一致]", StringComparison.Ordinal).Replace(" -> ", " → ", StringComparison.Ordinal);

    public static string? CleanModel(string? model)
    {
        if (model is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        var taken = 0;
        foreach (var rune in model.EnumerateRunes())
        {
            if (IsControl(rune))
            {
                continue;
            }

            if (taken >= 128)
            {
                break;
            }

            builder.Append(rune.ToString());
            taken++;
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    public static string? CleanDiagnosticIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) > MaxDiagnosticIdentifierBytes)
        {
            return null;
        }

        foreach (var character in value)
        {
            var ok = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or '[' or ']';
            if (!ok)
            {
                return null;
            }
        }

        var lower = value.ToLowerInvariant();
        if (lower.StartsWith("sk-", StringComparison.Ordinal) || lower.StartsWith("sess-", StringComparison.Ordinal) || lower.StartsWith("eyj", StringComparison.Ordinal))
        {
            return null;
        }

        return value;
    }

    public static string SanitizeErrorDetail(string value)
    {
        var builder = new StringBuilder();
        var taken = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsControl(rune))
            {
                continue;
            }

            if (taken >= 240)
            {
                break;
            }

            builder.Append(rune.ToString());
            taken++;
        }

        return builder.ToString().Trim();
    }

    /// <summary>对应 Rust <c>char::is_control</c>：Unicode Cc 类。</summary>
    public static bool IsControl(Rune rune)
    {
        return Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Control;
    }
}
