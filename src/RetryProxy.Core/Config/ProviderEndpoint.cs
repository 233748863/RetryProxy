using System;

namespace RetryProxy.Core.Config;

/// <summary>
/// 上游服务商：名称 + 基础地址。
/// </summary>
public sealed class ProviderEndpoint : IEquatable<ProviderEndpoint>
{
    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public ProviderEndpoint()
    {
    }

    public ProviderEndpoint(string name, string baseUrl)
    {
        Name = name.Trim();
        BaseUrl = UrlRules.NormalizeBaseUrl(baseUrl);
    }

    internal void NormalizeInPlace()
    {
        Name = Name.Trim();
        BaseUrl = UrlRules.NormalizeBaseUrl(BaseUrl);
    }

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
    }

    public ProviderEndpoint Clone() => new() { Name = Name, BaseUrl = BaseUrl };

    public bool Equals(ProviderEndpoint? other)
    {
        return other is not null && Name == other.Name && BaseUrl == other.BaseUrl;
    }

    public override bool Equals(object? obj) => Equals(obj as ProviderEndpoint);

    public override int GetHashCode() => HashCode.Combine(Name, BaseUrl);

    public override string ToString() => $"{Name} · {BaseUrl}";
}
