using System;
using System.Collections.Generic;
using System.Linq;
using RetryProxy.Core.Cli;

namespace RetryProxy.Core.Config;

/// <summary>
/// 供应商：属于某一个客户端的上游站点，包含地址、认证方式、模型设置与 Key 列表（PRD-供应商管理 §3）。
/// 例：Claude Code 的 "Any"，地址 https://anyrouter.top，Key "主号"、"群号"。
/// 只有名称和地址的临时实例（如一键准备的手动填写）用 <see cref="ProviderEndpoint(string, string)"/> 创建，不进配置。
/// </summary>
public sealed class ProviderEndpoint : IEquatable<ProviderEndpoint>
{
    public string Id { get; set; } = string.Empty;

    public ClientType ClientType { get; set; } = ClientType.Codex;

    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>仅 Claude 有意义；Codex 固定 Bearer。</summary>
    public ClaudeAuthMode AuthMode { get; set; } = ClaudeAuthMode.Bearer;

    public ProviderModels Models { get; set; } = new();

    public string WebsiteUrl { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public BalanceQuery BalanceQuery { get; set; } = new();

    public List<ProviderKey> Keys { get; set; } = new();

    public ProviderEndpoint()
    {
    }

    public ProviderEndpoint(string name, string baseUrl)
    {
        Name = name.Trim();
        BaseUrl = UrlRules.NormalizeBaseUrl(baseUrl);
    }

    public ProviderKey? KeyById(string keyId) => Keys.FirstOrDefault(key => key.Id == keyId);

    internal void NormalizeInPlace()
    {
        Id = Id.Trim();
        Name = Name.Trim();
        BaseUrl = UrlRules.NormalizeBaseUrl(BaseUrl);
        WebsiteUrl = WebsiteUrl.Trim();
        Notes = Notes.Trim();
        Models.NormalizeInPlace();
        foreach (var key in Keys)
        {
            key.NormalizeInPlace();
        }
    }

    /// <summary>校验名称、地址与 Key；ID 唯一性与同客户端内名称唯一由 <see cref="ProxyConfig.Validate"/> 检查。</summary>
    public void Validate()
    {
        if (Name.Length == 0)
        {
            throw new ConfigException("服务商名称不能为空");
        }

        if (BaseUrl.Length == 0)
        {
            throw new ConfigException("服务商地址不能为空");
        }

        UrlRules.ValidateBaseUrl(BaseUrl, "服务商地址");
        var keyIds = new HashSet<string>();
        var keyNames = new HashSet<string>();
        foreach (var key in Keys)
        {
            if (key.Id.Length == 0)
            {
                throw new ConfigException($"服务商“{Name}”有 Key 缺少 ID");
            }

            if (!keyIds.Add(key.Id))
            {
                throw new ConfigException($"服务商“{Name}”的 Key ID 重复：{key.Id}");
            }

            if (key.Name.Length == 0)
            {
                throw new ConfigException($"服务商“{Name}”有 Key 未填写名称");
            }

            if (!keyNames.Add(key.Name.ToLowerInvariant()))
            {
                throw new ConfigException($"服务商“{Name}”的 Key 名称重复：{key.Name}");
            }

            if (key.ApiKey.Length == 0)
            {
                throw new ConfigException($"服务商“{Name}”的 Key“{key.Name}”未填写密钥");
            }
        }
    }

    public ProviderEndpoint Clone() => new()
    {
        Id = Id,
        ClientType = ClientType,
        Name = Name,
        BaseUrl = BaseUrl,
        AuthMode = AuthMode,
        Models = Models.Clone(),
        WebsiteUrl = WebsiteUrl,
        Notes = Notes,
        BalanceQuery = BalanceQuery.Clone(),
        Keys = Keys.Select(key => key.Clone()).ToList(),
    };

    public bool Equals(ProviderEndpoint? other)
    {
        return other is not null
            && Id == other.Id
            && ClientType == other.ClientType
            && Name == other.Name
            && BaseUrl == other.BaseUrl
            && AuthMode == other.AuthMode
            && Models.Equals(other.Models)
            && WebsiteUrl == other.WebsiteUrl
            && Notes == other.Notes
            && BalanceQuery.Equals(other.BalanceQuery)
            && Keys.SequenceEqual(other.Keys);
    }

    public override bool Equals(object? obj) => Equals(obj as ProviderEndpoint);

    public override int GetHashCode() => HashCode.Combine(Id, Name, BaseUrl);

    public override string ToString() => $"{Name} · {BaseUrl}";
}
