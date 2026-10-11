using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Metrics;

[JsonConverter(typeof(JsonStringEnumConverter<PromptPartKind>))]
public enum PromptPartKind { System, Instructions, Tools, Files, Results, Conversation }

public sealed record PromptPart(PromptPartKind Kind, long Tokens);

/// <summary>只保存汇总数字和会话摘要；不保存正文、文件路径、工具名称或原始会话标识。</summary>
public sealed record PromptAnalysis
{
    public string? SessionHash { get; init; }
    public long StartedAtUnixMs { get; init; }
    public long? Window { get; init; }
    public IReadOnlyList<PromptPart> Parts { get; init; } = [];
    public bool HasServerHistory { get; init; }
    public bool HasUnmeasuredContent { get; init; }
    public string Status { get; init; } = "ok";
    [JsonIgnore] public long EstimatedTokens => Parts.Sum(p => p.Tokens);

    public PromptMeasurement Measure(RequestUsage? usage)
    {
        var normalized = usage?.Normalize();
        var measured = normalized is { Invalid: false, Prompt: not null, PromptIsLowerBound: false };
        decimal? total = measured ? normalized!.Prompt : Status == "ok" ? EstimatedTokens : null;
        var calibrate = measured && !HasServerHistory && !HasUnmeasuredContent && EstimatedTokens > 0 && Status == "ok";
        var parts = Parts.Select(p => new MeasuredPromptPart(p.Kind,
            calibrate ? p.Tokens * total!.Value / EstimatedTokens : p.Tokens,
            EstimatedTokens > 0 ? (double)p.Tokens / EstimatedTokens * 100 : 0)).ToArray();
        var complete = measured || Status == "ok" && !HasServerHistory && !HasUnmeasuredContent;
        return new(total, measured, complete,
            complete && Window is > 0 && total is not null ? (double)(100 * total.Value / Window.Value) : null, parts);
    }
}

public sealed record MeasuredPromptPart(PromptPartKind Kind, decimal Tokens, double Percent);
public sealed record PromptMeasurement(decimal? Tokens, bool Measured, bool Complete, double? FillPercent, IReadOnlyList<MeasuredPromptPart> Parts);

/// <summary>旁路分析，失败不改变请求。受体积和深度约束，处理过的正文不会保存在结果中。</summary>
public static class PromptAnalyzer
{
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    public static PromptAnalysis Capture(ClientType client, HeaderList headers, ReadOnlyMemory<byte> body, long? window, long startedAtUnixMs)
    {
        var result = new PromptAnalysis { Window = window is > 0 ? window : null, StartedAtUnixMs = startedAtUnixMs,
            SessionHash = ConversationHash(client, headers, default) };
        if (body.Length > MaxBodyBytes) return result with { Status = "size_limit" };
        if (headers.Get("content-encoding") is { Length: > 0 } encoding && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            return result with { Status = "encoded" };
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 128 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result with { Status = "unsupported" };
            result = result with { SessionHash = ConversationHash(client, headers, root), HasServerHistory =
                Text(root, "previous_response_id") is { Length: > 0 } || Property(root, "conversation").ValueKind is JsonValueKind.String or JsonValueKind.Object };
            var messages = Property(root, "messages");
            var input = Property(root, "input");
            if (messages.ValueKind != JsonValueKind.Array && input.ValueKind is not (JsonValueKind.Array or JsonValueKind.String))
                return result with { Status = "unsupported" };
            var builder = new Builder();
            builder.Content(Property(root, "system"), PromptPartKind.System);
            builder.Content(Property(root, "instructions"), PromptPartKind.System);
            builder.Add(PromptPartKind.Tools, Property(root, "tools"));
            builder.Add(PromptPartKind.Tools, Property(root, "additional_tools"));
            var items = messages.ValueKind == JsonValueKind.Array ? messages : input;
            if (items.ValueKind == JsonValueKind.String) builder.Content(items, PromptPartKind.Conversation);
            else foreach (var item in items.EnumerateArray()) builder.Message(item);
            return result with { Parts = builder.Parts(), HasUnmeasuredContent = builder.Unmeasured };
        }
        catch (JsonException) { return result with { Status = "invalid_json" }; }
        catch (InvalidOperationException) { return result with { Status = "unsupported" }; }
    }

    internal static long Estimate(string value)
    {
        long ascii = 0, other = 0;
        foreach (var rune in value.EnumerateRunes()) { if (rune.IsAscii) ascii++; else other++; }
        return (ascii + 3) / 4 + other;
    }

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    private static string? Text(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    private static string? ValidIdentity(string? value) => value is { Length: > 0 and <= 256 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':') ? value : null;

    private static string? MetadataIdentity(string? json)
    {
        if (json is null || json.Length > 16 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ValidIdentity(Text(doc.RootElement, "thread_id") ?? Text(doc.RootElement, "threadId") ?? Text(doc.RootElement, "session_id"));
        }
        catch (JsonException) { return null; }
    }

    private static string? ConversationHash(ClientType client, HeaderList headers, JsonElement root)
    {
        string? id = null;
        foreach (var name in new[] { "thread_id", "session_id", "x-session-id", "x-claude-code-session-id" })
            if ((id = ValidIdentity(headers.Get(name))) is not null) break;
        id ??= MetadataIdentity(headers.Get("x-codex-turn-metadata"));
        id ??= MetadataIdentity(Text(Property(root, "client_metadata"), "x-codex-turn-metadata"));
        if (client == ClientType.Claude)
        {
            var metadata = Property(root, "metadata");
            id ??= ValidIdentity(Text(metadata, "session_id"));
            var user = Text(metadata, "user_id");
            id ??= MetadataIdentity(user);
            if (id is null && user is { Length: <= 4096 } && user.LastIndexOf("_session_", StringComparison.Ordinal) is var pos && pos >= 0)
                id = ValidIdentity(user[(pos + 9)..]);
        }
        var conversation = Property(root, "conversation");
        id ??= ValidIdentity(conversation.ValueKind == JsonValueKind.String ? conversation.GetString() : Text(conversation, "id"));
        return id is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{client}\n{id}"))).ToLowerInvariant();
    }

    private sealed class Builder
    {
        private readonly long[] _tokens = new long[6];
        private readonly Dictionary<string, bool> _reads = new(StringComparer.Ordinal);
        public bool Unmeasured { get; private set; }
        public IReadOnlyList<PromptPart> Parts() => Enum.GetValues<PromptPartKind>().Select(k => new PromptPart(k, _tokens[(int)k])).ToArray();

        public void Add(PromptPartKind kind, JsonElement value)
        {
            if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
            _tokens[(int)kind] += Estimate(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
        }

        private void Remember(JsonElement item)
        {
            var id = Text(item, "call_id") ?? Text(item, "id");
            var name = Text(item, "name") ?? Text(Property(item, "function"), "name");
            if (id is { Length: <= 256 } && name is not null)
                _reads[id] = name.Equals("read", StringComparison.OrdinalIgnoreCase) || name.Equals("read_file", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("read_text_file", StringComparison.OrdinalIgnoreCase) || name.Equals("ReadFile", StringComparison.OrdinalIgnoreCase);
        }

        private PromptPartKind ResultKind(JsonElement item)
        {
            var id = Text(item, "tool_use_id") ?? Text(item, "tool_call_id") ?? Text(item, "call_id");
            return id is not null && _reads.TryGetValue(id, out var read) && read ? PromptPartKind.Files : PromptPartKind.Results;
        }

        public void Message(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) { Content(item, PromptPartKind.Conversation); return; }
            var type = Text(item, "type");
            if (type is "function_call" or "custom_tool_call") { Remember(item); Add(PromptPartKind.Conversation, item); return; }
            if (type is "function_call_output" or "custom_tool_call_output") { Content(Property(item, "output"), ResultKind(item)); return; }
            if (type == "reasoning") { Unmeasured = true; Content(Property(item, "summary"), PromptPartKind.Conversation); return; }
            var role = Text(item, "role");
            var kind = role is "system" or "developer" ? PromptPartKind.System : role == "tool" ? ResultKind(item) : PromptPartKind.Conversation;
            Content(Property(item, "content"), kind);
            if (Property(item, "tool_calls") is { ValueKind: JsonValueKind.Array } calls)
                foreach (var call in calls.EnumerateArray()) { Remember(call); Add(PromptPartKind.Conversation, call); }
        }

        public void Content(JsonElement content, PromptPartKind kind)
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString()!;
                // 仅认明确的客户端包裹标记；一般文字中的文件名不作为指令判断依据。
                var instruction = text.StartsWith("# AGENTS.md instructions", StringComparison.Ordinal)
                    || text.StartsWith("<system-reminder>", StringComparison.Ordinal)
                    || text.StartsWith("<user_instructions>", StringComparison.Ordinal)
                    || text.StartsWith("Contents of ", StringComparison.Ordinal) && text.Contains("CLAUDE.md", StringComparison.Ordinal);
                _tokens[(int)(instruction && kind is PromptPartKind.System or PromptPartKind.Conversation ? PromptPartKind.Instructions : kind)] += Estimate(text);
                return;
            }
            if (content.ValueKind == JsonValueKind.Array) { foreach (var part in content.EnumerateArray()) Content(part, kind); return; }
            if (content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
            if (content.ValueKind != JsonValueKind.Object) { Add(kind, content); return; }
            switch (Text(content, "type"))
            {
                case "image": case "image_url": case "input_image": case "input_audio": case "audio": case "input_file": case "document":
                    Unmeasured = true; return;
                case "tool_use": Remember(content); Add(PromptPartKind.Conversation, content); return;
                case "tool_result": Content(Property(content, "content"), ResultKind(content)); return;
                case "redacted_thinking": Unmeasured = true; return;
                case "thinking": Content(Property(content, "thinking"), kind); return;
                default:
                    if (Property(content, "text").ValueKind == JsonValueKind.String) Content(Property(content, "text"), kind);
                    else { Add(kind, content); Unmeasured = true; }
                    return;
            }
        }
    }
}
