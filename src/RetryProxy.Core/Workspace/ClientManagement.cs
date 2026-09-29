using RetryProxy.Core.Client;
using RetryProxy.Core.Config;
using System.Linq;

namespace RetryProxy.Core.Workspace;

public sealed partial class ProxyWorkspace
{
    /// <summary>客户端文件写完后提交接管状态；失败由 ClientConfigStore 恢复原文件。</summary>
    public string? SaveClientTakeover(ClientType client, ClientTakeoverState state)
    {
        var candidate = Config.Clone();
        candidate.ClientTakeover[client] = state.Clone();
        return SaveCandidate(candidate);
    }

    /// <summary>
    /// 向导确认后保存客户端的原供应商。已有本机通道只补 Key，外部地址新建供应商并选中；
    /// 所有引用在一次配置提交内更新，避免保存供应商成功、选择 Key 失败。
    /// </summary>
    public string? ImportClientProvider(ClientProfile profile, string name)
    {
        var client = profile.ClientType;
        var preview = ClientImport.Preview(profile, Config, client);
        if (!preview.CanImport) return preview.Message;
        var previous = Config;
        var candidate = Config.Clone();
        var route = candidate.RouteFor(client)!;
        ProviderEndpoint provider;
        if (preview.Kind == ClientImportKind.ExistingChannel)
        {
            provider = candidate.ProviderById(route.CurrentProviderId)!;
            var existing = provider.Keys.FirstOrDefault(key => key.ApiKey == profile.ApiKey);
            if (existing is not null) return null;
            var keyName = "默认";
            for (var suffix = 2; provider.Keys.Any(key => key.Name == keyName); suffix++) keyName = $"默认 {suffix}";
            var key = new ProviderKey { Id = ProxyConfig.NewId(), Name = keyName, ApiKey = profile.ApiKey };
            provider.Keys.Add(key);
            route.CurrentKeyId = key.Id;
            provider.AuthMode = profile.AuthMode;
            if (provider.Models.Model.Length == 0) provider.Models = profile.Models.Clone();
        }
        else
        {
            try { provider = ClientImport.CreateProvider(profile, client, name); }
            catch (ClientConfigException error) { return error.Message; }
            candidate.Providers.Add(provider);
            route.CurrentProviderId = provider.Id;
            route.CurrentKeyId = provider.Keys[0].Id;
        }

        if (SaveCandidate(candidate) is { } saveError)
            return saveError.StartsWith("保存失败：", System.StringComparison.Ordinal) ? "供应商保存失败，原配置未修改，请检查文件权限和磁盘空间" : saveError;
        RefreshServices();
        ApplyProviderUpdate(previous, provider.Id);
        if (_startupRequested && !_manuallyStoppedRoutes.Contains(route.Id)) StartRoute(route.Id);
        _uiNotifier?.Invoke();
        return null;
    }
}
