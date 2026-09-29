using System;
using System.Linq;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Client;

public enum ClientImportKind
{
    External,
    ExistingChannel,
    OtherLocalProxy,
    Unsupported,
}

/// <summary>向导预览只提供分类与操作信息，不携带密钥，不修改配置。</summary>
public sealed class ClientImportPreview
{
    public ClientImportKind Kind { get; init; }
    public bool CanImport { get; init; }
    public string Message { get; init; } = string.Empty;
    public string SuggestedName { get; init; } = string.Empty;
    public string ExistingProviderId { get; init; } = string.Empty;
    public string ExistingRouteId { get; init; } = string.Empty;
    public bool KeyAlreadyExists { get; init; }
    /// <summary>Codex 接管将改用 retry_proxy；界面需要提醒历史会话按新名称分组。</summary>
    public bool RequiresProviderRename { get; init; }
    public override string ToString() => $"ClientImportPreview {{ {Kind}, CanImport={CanImport} }}";
}

public static class ClientImport
{
    public static ClientImportPreview Preview(ClientProfile profile, ProxyConfig config, ClientType client)
    {
        var rename = client == ClientType.Codex &&
            (string.IsNullOrWhiteSpace(profile?.ProviderName) || profile.ProviderName == "openai");
        ClientImportPreview Unsupported(string message) => new()
        {
            Kind = ClientImportKind.Unsupported, Message = message, RequiresProviderRename = rename,
        };

        if (profile is null || config is null || !Enum.IsDefined(client) || profile.ClientType != client)
            return Unsupported("客户端配置不匹配，无法导入。");
        if (profile.HasConflictingSettings)
            return Unsupported("客户端当前启用了其他认证或配置覆盖，无法可靠读取当前供应商，请手动添加。");
        if (!TryAddress(profile.BaseUrl, out var address))
            return Unsupported("未读取到有效地址，官方登录不支持导入，请手动添加供应商。");

        // 导入兼容旧客户端的根地址，只认本客户端的已知主机和端口，不要求接管后的路径。
        // 取原始主机文字，避免 URI 把 127.1 等写法规范化成 127.0.0.1 后误认作本通道。
        var hostStart = profile.BaseUrl.IndexOf("://", StringComparison.Ordinal) + 3;
        var hostEnd = profile.BaseUrl.IndexOfAny([':', '/'], hostStart);
        var host = hostEnd < 0 ? profile.BaseUrl[hostStart..] : profile.BaseUrl[hostStart..hostEnd];
        var knownHost = host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        var route = knownHost ? config.Routes.FirstOrDefault(candidate => candidate.ClientType == client
            && candidate.ListenPort == address!.Port) : null;
        var isLocalToken = config.Routes.Any(candidate => !string.IsNullOrEmpty(candidate.LocalToken)
            && string.Equals(candidate.LocalToken, profile.ApiKey, StringComparison.Ordinal));
        if (route is not null)
        {
            var provider = config.Providers.FirstOrDefault(candidate => candidate.Id == route.CurrentProviderId
                && candidate.ClientType == client);
            var duplicate = provider?.Keys.Any(key => key.ApiKey == profile.ApiKey) == true;
            var canImport = provider is not null && ValidKey(profile.ApiKey) && !isLocalToken && !duplicate;
            return new ClientImportPreview
            {
                Kind = ClientImportKind.ExistingChannel,
                CanImport = canImport,
                ExistingRouteId = route.Id,
                ExistingProviderId = provider?.Id ?? string.Empty,
                SuggestedName = "默认",
                KeyAlreadyExists = duplicate,
                RequiresProviderRename = rename,
                Message = isLocalToken ? "当前已使用本地口令，无需导入密钥。"
                    : provider is null ? "本通道尚未选择供应商，请手动添加供应商。"
                    : !ValidKey(profile.ApiKey) ? "未读取到有效密钥，官方登录不支持导入。"
                    : duplicate ? "该密钥已存在，无需重复导入。"
                    : "将把现有密钥添加到本通道当前供应商，名称为“默认”。",
            };
        }

        if (address!.IsLoopback)
            return new ClientImportPreview
            {
                Kind = ClientImportKind.OtherLocalProxy,
                Message = "当前指向本机另一个程序，读不到真实密钥，请手动添加供应商。",
                RequiresProviderRename = rename,
            };
        // 地址被手工改成外部站点时，也不能把本软件任一通道的口令当作上游 Key。
        if (isLocalToken)
            return Unsupported("当前凭据是本地口令，不能作为供应商密钥导入。");
        if (!ValidKey(profile.ApiKey) || !Enum.IsDefined(profile.AuthMode))
            return Unsupported("未读取到有效密钥或认证方式，官方登录不支持导入。");
        if (!ValidModels(profile.Models))
            return Unsupported("客户端的模型参数无效，请手动添加供应商。");

        return new ClientImportPreview
        {
            Kind = ClientImportKind.External,
            CanImport = true,
            Message = "将把现有地址、密钥和模型保存为供应商。",
            SuggestedName = UrlRules.GeneratedProviderName(profile.BaseUrl,
                config.Providers.Where(provider => provider.ClientType == client)),
            RequiresProviderRename = rename,
        };
    }

    /// <summary>只创建外部供应商草稿；ExistingChannel 的 Key 应由协调层挂到 ExistingProviderId 下。</summary>
    public static ProviderEndpoint CreateProvider(ClientProfile profile, ClientType client, string name)
    {
        try
        {
            if (profile is null || !Enum.IsDefined(client) || profile.ClientType != client || profile.HasConflictingSettings
                || !TryAddress(profile.BaseUrl, out var address) || address!.IsLoopback
                || !ValidKey(profile.ApiKey) || !Enum.IsDefined(profile.AuthMode)
                || !ValidModels(profile.Models) || string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException();

            var provider = new ProviderEndpoint
            {
                Id = Guid.NewGuid().ToString("N"),
                ClientType = client,
                Name = name.Trim(),
                BaseUrl = UrlRules.NormalizeBaseUrl(profile.BaseUrl),
                AuthMode = client == ClientType.Codex ? ClaudeAuthMode.Bearer : profile.AuthMode,
                Models = profile.Models.Clone(),
                Keys = [new ProviderKey
                {
                    Id = Guid.NewGuid().ToString("N"), Name = "默认", ApiKey = profile.ApiKey,
                }],
            };
            provider.Validate();
            return provider;
        }
        catch
        {
            throw new ClientConfigException("无法导入供应商，请检查名称、地址、密钥和模型设置。");
        }
    }

    private static bool TryAddress(string? value, out Uri? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            return false;
        try
        {
            UrlRules.ValidateBaseUrl(value, "客户端地址");
            return Uri.TryCreate(value, UriKind.Absolute, out address);
        }
        catch { return false; }
    }

    private static bool ValidKey(string? key) => !string.IsNullOrEmpty(key)
        && !key.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    private static bool ValidModels(ProviderModels? models) => models is not null
        && models.ContextWindow is not <= 0 && models.AutoCompactTokenLimit is not <= 0
        && !(models.ContextWindow is { } window && models.AutoCompactTokenLimit is { } limit && limit > window);
}
