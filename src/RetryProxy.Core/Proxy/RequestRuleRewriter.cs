using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Proxy;

/// <summary>只编辑目标字节；一次计划只运行一次，改投从原始正文重新运行。</summary>
internal static class RequestRuleRewriter
{
    public const int MaxBodyBytes = 8 * 1024 * 1024;
    internal readonly record struct Result(ReadOnlyMemory<byte> Body, int Changed = 0, int Skipped = 0)
    {
        public string LogFields => (Changed > 0 ? $"，请求规则改动 {Changed} 条" : string.Empty)
            + (Skipped > 0 ? $"，请求规则跳过 {Skipped} 条（正文或参数路径无法编辑）" : string.Empty);
    }

    private readonly record struct Edit(int Start, int End, byte[] Value);
    private readonly record struct Located(Edit? Edit = null, bool Blocked = false);

    public static bool IsGenerationPath(string path) => path is "/v1/messages" or "/messages"
        or "/v1/responses" or "/responses" or "/v1/chat/completions" or "/chat/completions";

    public static Result Apply(IReadOnlyList<RequestRule> rules, HeaderList headers, ReadOnlyMemory<byte> body)
    {
        var enabled = rules.Count(rule => rule.Enabled);
        if (enabled == 0) return new(body);
        if (rules.Count > RequestRule.MaxRules || body.Length > MaxBodyBytes || !PromptCache.EditableBody(headers))
            return new(body, Skipped: enabled);

        string? model;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 128 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new(body, Skipped: enabled);
            model = document.RootElement.TryGetProperty("model", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return new(body, Skipped: enabled);
        }

        var current = body;
        int changed = 0, skipped = 0;
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !rule.Matches(model)) continue;
            try
            {
                rule.Validate();
                var reader = new Utf8JsonReader(current.Span, new JsonReaderOptions { MaxDepth = 128 });
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
                var value = rule.Action == RequestRuleAction.Remove ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(rule.ValueJson);
                var located = Locate(ref reader, current.Span, rule.Path.Split('.'), 0, rule.Action, value);
                if (reader.Read()) throw new JsonException();
                if (located.Blocked) { skipped++; continue; }
                if (located.Edit is not { } edit) continue;
                var length = current.Length - (edit.End - edit.Start) + edit.Value.Length;
                if (length > MaxBodyBytes) { skipped++; continue; }
                var rewritten = new byte[length];
                current.Span[..edit.Start].CopyTo(rewritten);
                edit.Value.CopyTo(rewritten.AsSpan(edit.Start));
                current.Span[edit.End..].CopyTo(rewritten.AsSpan(edit.Start + edit.Value.Length));
                current = rewritten;
                changed++;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ConfigException)
            {
                // 单条失败不使请求失败，也不把其他规则已完成的修改撤掉。
                skipped++;
            }
        }
        return new(current, changed, skipped);
    }

    /// <summary>
    /// 进入时停在对象开头，返回时停在其结尾；只递归目标路径，其他值用 Skip 验证并越过。
    /// 路径上的重复成员有歧义，该条整体跳过。删除首项时连同后面的逗号，其他项连同前面的逗号。
    /// </summary>
    private static Located Locate(ref Utf8JsonReader reader, ReadOnlySpan<byte> body, string[] path, int depth,
        RequestRuleAction action, byte[] value)
    {
        int fields = 0, matches = 0, previousEnd = -1;
        var located = new Located();
        var removingFirst = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var propertyStart = (int)reader.TokenStartIndex;
            if (removingFirst && fields == 1 && located.Edit is { } first)
                located = new(first with { End = propertyStart });
            var name = reader.GetString();
            if (!reader.Read()) throw new JsonException();
            var valueStart = (int)reader.TokenStartIndex;
            if (name == path[depth])
            {
                matches++;
                if (depth + 1 < path.Length)
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                        located = Locate(ref reader, body, path, depth + 1, action, value);
                    else { reader.Skip(); located = new(Blocked: true); }
                }
                else
                {
                    reader.Skip();
                    var end = (int)reader.BytesConsumed;
                    if (action == RequestRuleAction.Remove)
                    {
                        removingFirst = fields == 0;
                        located = new(new Edit(previousEnd < 0 ? propertyStart : previousEnd, end, Array.Empty<byte>()));
                    }
                    else if (action == RequestRuleAction.Set && !body[valueStart..end].SequenceEqual(value))
                        located = new(new Edit(valueStart, end, value));
                }
            }
            else reader.Skip();
            previousEnd = (int)reader.BytesConsumed;
            fields++;
        }
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException();
        if (matches > 1) return new(Blocked: true);
        if (matches == 0 && action != RequestRuleAction.Remove)
        {
            var inserted = NewMember(path, depth, value, fields > 0);
            var end = (int)reader.TokenStartIndex;
            return new(new Edit(end, end, inserted));
        }
        return located;
    }

    private static byte[] NewMember(string[] path, int depth, byte[] value, bool needsComma)
    {
        var builder = new StringBuilder(needsComma ? "," : string.Empty);
        for (var i = depth; i < path.Length; i++)
        {
            builder.Append(JsonSerializer.Serialize(path[i])).Append(':');
            if (i + 1 < path.Length) builder.Append('{');
        }
        builder.Append(Encoding.UTF8.GetString(value));
        builder.Append('}', path.Length - depth - 1);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
