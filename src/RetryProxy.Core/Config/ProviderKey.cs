using System;

namespace RetryProxy.Core.Config;

/// <summary>
/// 供应商下的一个 API Key。例：名称 "PLUS"、密钥 "sk-…a1b2"、不覆盖模型。
/// 密钥按用户决定（PRD-供应商管理 P5）明文保存在 config.db。
/// </summary>
public sealed class ProviderKey : IEquatable<ProviderKey>
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>为空表示沿用供应商的模型设置。</summary>
    public KeyModelOverride? ModelOverride { get; set; }

    public string Notes { get; set; } = string.Empty;

    internal void NormalizeInPlace()
    {
        Id = Id.Trim();
        Name = Name.Trim();
        ApiKey = ApiKey.Trim();
        Notes = Notes.Trim();
        ModelOverride?.NormalizeInPlace();
        if (ModelOverride is { Model.Length: 0 })
        {
            ModelOverride = null;
        }
    }

    /// <summary>界面只显示前 4 位与末 4 位；不超过 8 位时全部遮盖，避免露出整串密钥。</summary>
    public string MaskedKey => ApiKey.Length <= 8 ? "····" : $"{ApiKey[..4]}····{ApiKey[^4..]}";

    public ProviderKey Clone() => new()
    {
        Id = Id,
        Name = Name,
        ApiKey = ApiKey,
        ModelOverride = ModelOverride?.Clone(),
        Notes = Notes,
    };

    public bool Equals(ProviderKey? other)
    {
        return other is not null
            && Id == other.Id
            && Name == other.Name
            && ApiKey == other.ApiKey
            && Equals(ModelOverride, other.ModelOverride)
            && Notes == other.Notes;
    }

    public override bool Equals(object? obj) => Equals(obj as ProviderKey);

    public override int GetHashCode() => HashCode.Combine(Id, Name);
}

/// <summary>
/// Key 级别的模型覆盖：Claude 覆盖主模型与 1M 标记，Codex 只用 <see cref="Model"/>。
/// </summary>
public sealed class KeyModelOverride : IEquatable<KeyModelOverride>
{
    public string Model { get; set; } = string.Empty;

    public bool Context1M { get; set; }

    internal void NormalizeInPlace()
    {
        Model = Model.Trim();
    }

    public KeyModelOverride Clone() => new() { Model = Model, Context1M = Context1M };

    public bool Equals(KeyModelOverride? other)
    {
        return other is not null && Model == other.Model && Context1M == other.Context1M;
    }

    public override bool Equals(object? obj) => Equals(obj as KeyModelOverride);

    public override int GetHashCode() => HashCode.Combine(Model, Context1M);
}
