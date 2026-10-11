using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RetryProxy.Core.Config;

public enum RequestRuleAction { Set, SetIfMissing, Remove }

/// <summary>供应商的有序参数规则；ValueJson 只在配置内保存，诊断不输出规则内容。</summary>
public sealed class RequestRule : IEquatable<RequestRule>
{
    public const int MaxRules = 64;
    public const int MaxValueBytes = 16 * 1024;
    private static readonly HashSet<string> ProtectedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "stream", "stream_options", "input", "messages", "system", "instructions", "tools",
        "metadata", "user", "conversation", "conversation_id", "session", "session_id", "thread_id",
        "previous_response_id", "prompt_cache_key", "authorization", "api_key", "access_token",
    };

    public bool Enabled { get; set; } = true;
    public string ModelPattern { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public RequestRuleAction Action { get; set; }
    public string ValueJson { get; set; } = "null";

    internal void NormalizeInPlace()
    {
        ModelPattern = ModelPattern.Trim();
        Path = Path.Trim();
        ValueJson = Action == RequestRuleAction.Remove ? "null" : ValueJson.Trim();
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Action)) throw new ConfigException("请选择有效的请求规则操作");
        if (ModelPattern.Length > 256 || ModelPattern.Any(char.IsControl))
            throw new ConfigException("规则模型条件最多 256 个字符，且不能包含控制字符");
        var parts = Path.Split('.');
        if (Path.Length > 256 || parts.Length > 8 || parts.Any(part => part.Length == 0
            || !IsLetter(part[0]) && part[0] != '_'
            || part.Any(c => !IsLetter(c) && !char.IsAsciiDigit(c) && c is not '_' and not '-')))
            throw new ConfigException("参数路径需用点分隔英文名称，最多 8 层、256 个字符");
        if (ProtectedRoots.Contains(parts[0]))
            throw new ConfigException("模型、流式开关、对话内容、工具定义及会话标识不能通过请求规则修改");
        if (Action == RequestRuleAction.Remove) return;
        if (Encoding.UTF8.GetByteCount(ValueJson) > MaxValueBytes)
            throw new ConfigException("规则参数值最多 16 KiB");
        try { using var document = JsonDocument.Parse(ValueJson, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new ConfigException("规则参数值必须是有效 JSON，最多嵌套 32 层"); }
    }

    private static bool IsLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    internal bool Matches(string? model)
    {
        var pattern = ModelPattern;
        if (pattern.Length == 0 || pattern == "*") return true;
        if (model is null) return false;
        // 单一通配符，不使用正则表达式；星号后的失败仅推进模型位置。
        int p = 0, m = 0, star = -1, retry = 0;
        while (m < model.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(model[m])) { p++; m++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = m; }
            else if (star >= 0) { p = star + 1; m = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    public RequestRule Clone() => new() { Enabled = Enabled, ModelPattern = ModelPattern, Path = Path, Action = Action, ValueJson = ValueJson };
    public bool Equals(RequestRule? other) => other is not null && Enabled == other.Enabled && ModelPattern == other.ModelPattern
        && Path == other.Path && Action == other.Action && ValueJson == other.ValueJson;
    public override bool Equals(object? obj) => Equals(obj as RequestRule);
    public override int GetHashCode() => HashCode.Combine(Enabled, ModelPattern, Path, Action, ValueJson);
    public override string ToString() => $"RequestRule {{ Enabled = {Enabled}, Action = {Action} }}";
}
