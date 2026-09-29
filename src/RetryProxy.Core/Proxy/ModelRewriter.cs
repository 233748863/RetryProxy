using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Stats;

namespace RetryProxy.Core.Proxy;

/// <summary>
/// 模型映射（PRD-供应商管理 §6.2），只用于带本地口令、已注入 Key 的 JSON 请求。
/// Claude：按请求模型名里的关键字 opus / sonnet / haiku / fable 映射到当前 Key 对应角色的模型，关键字都不匹配时用主模型；
/// 目标角色没勾 1M 时删掉 <c>anthropic-beta</c> 里的 <c>context-1m-*</c>（只删不加）。
/// Codex：请求的模型不在已知列表里时才改成当前 Key 的模型，用户手选的有效模型保持不变。
/// 按字节替换顶层 <c>model</c> 字符串，不重新序列化正文（与 D10 一致）；供应商没设模型时什么都不改。
/// </summary>
internal static class ModelRewriter
{
    private static readonly byte[] ModelProperty = "model"u8.ToArray();

    /// <summary>改写结果；<see cref="From"/> 为 null 表示正文没有改动。</summary>
    internal readonly record struct Result(ReadOnlyMemory<byte> Body, string? From, string? To);

    /// <summary>Claude 某个角色的目标模型与 1M 标记。</summary>
    internal readonly record struct ClaudeRole(string Model, bool Context1M);

    /// <summary>
    /// 改写正文里的模型；Claude 目标角色没勾 1M 时顺带改 <paramref name="headers"/>。
    /// 例：Claude 请求 <c>claude-opus-5</c>，当前 Key 主模型 <c>glm-5</c>（未勾 1M）→ 正文改为 <c>glm-5</c>，
    /// <c>anthropic-beta: context-1m-2025-08-07,x</c> → <c>anthropic-beta: x</c>。
    /// </summary>
    public static Result Apply(ClientType clientType, ChannelSnapshot snapshot, HeaderList headers, ReadOnlyMemory<byte> body)
    {
        var unchanged = new Result(body, null, null);
        if (!PromptCache.EditableBody(headers) || Locate(body.Span) is not { } located)
        {
            return unchanged;
        }

        string? target;
        if (clientType == ClientType.Claude)
        {
            if (ClaudeTarget(snapshot, located.Model) is not { } role)
            {
                return unchanged;
            }

            if (!role.Context1M)
            {
                RemoveContext1M(headers);
            }

            target = role.Model;
        }
        else
        {
            target = CodexTarget(snapshot, located.Model);
        }

        return target is null || target == located.Model
            ? unchanged
            : new Result(Replace(body.Span, located.Ranges, target), located.Model, target);
    }

    /// <summary>
    /// Claude 请求模型对应的目标：Key 的模型覆盖代替主模型与主模型的 1M；角色单独指定了模型时用它自己的模型与 1M，
    /// 否则跟随主模型。目标模型为空（供应商没设模型）时返回 null。
    /// 例：请求 <c>claude-haiku-4-5</c>，Haiku 指定 <c>glm-4.7-air</c> → <c>glm-4.7-air</c>；请求 <c>my-model</c> → 主模型。
    /// </summary>
    public static ClaudeRole? ClaudeTarget(ChannelSnapshot snapshot, string requested)
    {
        var models = snapshot.Models;
        var main = snapshot.ModelOverride is { Model.Length: > 0 } custom
            ? new ClaudeRole(custom.Model, custom.Context1M)
            : new ClaudeRole(models.Model, models.Context1M);
        var name = requested.ToLowerInvariant();
        var role = name.Contains("opus", StringComparison.Ordinal) ? models.Opus
            : name.Contains("sonnet", StringComparison.Ordinal) ? models.Sonnet
            : name.Contains("haiku", StringComparison.Ordinal) ? models.Haiku
            : name.Contains("fable", StringComparison.Ordinal) ? models.Fable
            : null;
        var target = role is { Model.Length: > 0 } ? new ClaudeRole(role.Model, role.Context1M) : main;
        return target.Model.Length == 0 ? null : target;
    }

    /// <summary>Codex 请求的模型不在已知列表（不区分大小写）里时返回当前 Key 的模型，否则返回 null。</summary>
    public static string? CodexTarget(ChannelSnapshot snapshot, string requested)
    {
        var model = snapshot.ModelOverride is { Model.Length: > 0 } custom ? custom.Model : snapshot.Models.Model;
        var trimmed = requested.Trim();
        return model.Length == 0 || snapshot.KnownModels.Any(known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
            ? null
            : model;
    }

    /// <summary>
    /// 删掉 <c>anthropic-beta</c> 里的 <c>context-1m-*</c>，删空了就去掉这个头；其余标记保持原顺序。
    /// 例：<c>context-1m-2025-08-07,fine-grained-tool-streaming-2025-05-14</c> → <c>fine-grained-tool-streaming-2025-05-14</c>。
    /// </summary>
    public static void RemoveContext1M(HeaderList headers)
    {
        headers.Update("anthropic-beta", value =>
        {
            var tokens = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var kept = tokens.Where(token => !token.StartsWith("context-1m", StringComparison.OrdinalIgnoreCase)).ToList();
            return kept.Count == tokens.Length ? value : kept.Count == 0 ? null : string.Join(",", kept);
        });
    }

    /// <summary>顶层 model 的字符串值及其在正文里的字节范围（含引号）。重复出现时全部改写，按最后一个判断。</summary>
    private sealed record Located(string Model, List<(int Start, int End)> Ranges);

    private static Located? Locate(ReadOnlySpan<byte> body)
    {
        var ranges = new List<(int Start, int End)>();
        string? model = null;
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isModel = reader.ValueTextEquals(ModelProperty);
                if (!reader.Read())
                {
                    return null;
                }

                if (isModel && reader.TokenType == JsonTokenType.String)
                {
                    ranges.Add(((int)reader.TokenStartIndex, (int)reader.BytesConsumed));
                    model = reader.GetString();
                }
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            // 结构不合法，或 model 字符串里有无效 UTF-8（GetString 无法转码）：不改写，原样转发。
            return null;
        }

        return model is null || DiagnosticText.CleanModel(model) is null ? null : new Located(model, ranges);
    }

    private static byte[] Replace(ReadOnlySpan<byte> body, List<(int Start, int End)> ranges, string model)
    {
        var encoded = JsonEncodedText.Encode(model, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).EncodedUtf8Bytes;
        var output = new ArrayBufferWriter<byte>(body.Length + (encoded.Length + 2) * ranges.Count);
        var position = 0;
        foreach (var (start, end) in ranges)
        {
            output.Write(body[position..start]);
            output.Write("\""u8);
            output.Write(encoded);
            output.Write("\""u8);
            position = end;
        }

        output.Write(body[position..]);
        return output.WrittenSpan.ToArray();
    }
}
