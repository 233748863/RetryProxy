using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetryProxy.Core.Internal;

/// <summary>
/// System.Text.Json 的薄封装，语义对齐 serde_json：
/// 非对象上取字段返回 null；数字只有非负整数才算 u64；序列化不转义非 ASCII 字符。
/// </summary>
internal static class JsonText
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    public static JsonDocument? TryParse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(bytes.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonDocument? TryParse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonNode? TryParseNode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes);
            return JsonNode.Parse(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonNode? TryParseNode(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(JsonNode node) => node.ToJsonString(Compact);

    /// <summary>对应 <c>value.get(name)</c>：非对象或缺字段时返回 null。</summary>
    public static JsonElement? Get(this JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value.TryGetProperty(name, out var property) ? property : null;
    }

    public static JsonElement? Get(this JsonElement? value, string name)
    {
        return value is { } element ? element.Get(name) : null;
    }

    /// <summary>对应 <c>value.pointer("/a/0/b")</c>（仅支持无转义的简单路径）。</summary>
    public static JsonElement? Pointer(this JsonElement value, string pointer)
    {
        if (pointer.Length == 0)
        {
            return value;
        }

        JsonElement current = value;
        foreach (var token in pointer.Split('/', StringSplitOptions.None)[1..])
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out current))
                {
                    return null;
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(token, out var index) || index < 0 || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    public static string? AsString(this JsonElement? value)
    {
        return value is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;
    }

    public static string? AsString(this JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public static ulong? AsUInt64(this JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.Number } element && element.TryGetUInt64(out var number))
        {
            return number;
        }

        return null;
    }

    public static bool IsNull(this JsonElement? value)
    {
        return value is { ValueKind: JsonValueKind.Null };
    }

    public static bool IsNullOrMissing(this JsonElement? value)
    {
        return value is null || value.Value.ValueKind == JsonValueKind.Null;
    }

    public static bool IsObject(this JsonElement? value)
    {
        return value is { ValueKind: JsonValueKind.Object };
    }

    public static bool IsArray(this JsonElement? value)
    {
        return value is { ValueKind: JsonValueKind.Array };
    }

    /// <summary>对应 serde_json 的 <c>Value::is_i64() || Value::is_u64()</c>。</summary>
    public static bool IsInteger(this JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Number
            && (value.TryGetInt64(out _) || value.TryGetUInt64(out _));
    }

    public static bool IsEmptyObject(this JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        using var properties = value.EnumerateObject();
        return !properties.MoveNext();
    }

    public static bool IsEmptyArray(this JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;
    }
}
