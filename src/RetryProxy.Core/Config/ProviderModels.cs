using System;

namespace RetryProxy.Core.Config;

/// <summary>
/// 供应商的模型设置。Claude 用主模型、1M 标记与四个角色；Codex 用模型、上下文窗口与自动压缩阈值。
/// 例（Claude）：主模型 claude-opus-5-5 + 1M，Haiku 单独指定 claude-haiku-4-5，其余角色跟随主模型。
/// </summary>
public sealed class ProviderModels : IEquatable<ProviderModels>
{
    public string Model { get; set; } = string.Empty;

    /// <summary>仅 Claude：主模型是否使用 1M 上下文。</summary>
    public bool Context1M { get; set; }

    public RoleModel Opus { get; set; } = new();

    public RoleModel Sonnet { get; set; } = new();

    public RoleModel Haiku { get; set; } = new();

    public RoleModel Fable { get; set; } = new();

    /// <summary>仅 Codex：写入 config.toml 的 model_context_window；为空表示不设置。</summary>
    public long? ContextWindow { get; set; }

    /// <summary>仅 Codex：写入 config.toml 的 model_auto_compact_token_limit；为空表示不设置。</summary>
    public long? AutoCompactTokenLimit { get; set; }

    internal void NormalizeInPlace()
    {
        Model = Model.Trim();
        Opus.NormalizeInPlace();
        Sonnet.NormalizeInPlace();
        Haiku.NormalizeInPlace();
        Fable.NormalizeInPlace();
    }

    public ProviderModels Clone() => new()
    {
        Model = Model,
        Context1M = Context1M,
        Opus = Opus.Clone(),
        Sonnet = Sonnet.Clone(),
        Haiku = Haiku.Clone(),
        Fable = Fable.Clone(),
        ContextWindow = ContextWindow,
        AutoCompactTokenLimit = AutoCompactTokenLimit,
    };

    public bool Equals(ProviderModels? other)
    {
        return other is not null
            && Model == other.Model
            && Context1M == other.Context1M
            && Opus.Equals(other.Opus)
            && Sonnet.Equals(other.Sonnet)
            && Haiku.Equals(other.Haiku)
            && Fable.Equals(other.Fable)
            && ContextWindow == other.ContextWindow
            && AutoCompactTokenLimit == other.AutoCompactTokenLimit;
    }

    public override bool Equals(object? obj) => Equals(obj as ProviderModels);

    public override int GetHashCode() => HashCode.Combine(Model, Context1M, ContextWindow);
}

/// <summary>
/// Claude 某个角色（Opus / Sonnet / Haiku / Fable）的模型。<see cref="Model"/> 为空表示跟随主模型，
/// 此时 1M 也跟随主模型；指定了模型时用自己的 <see cref="Context1M"/>。
/// </summary>
public sealed class RoleModel : IEquatable<RoleModel>
{
    public string Model { get; set; } = string.Empty;

    public bool Context1M { get; set; }

    internal void NormalizeInPlace()
    {
        Model = Model.Trim();
    }

    public RoleModel Clone() => new() { Model = Model, Context1M = Context1M };

    public bool Equals(RoleModel? other)
    {
        return other is not null && Model == other.Model && Context1M == other.Context1M;
    }

    public override bool Equals(object? obj) => Equals(obj as RoleModel);

    public override int GetHashCode() => HashCode.Combine(Model, Context1M);
}
