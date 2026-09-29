using RetryProxy.Core.Config;

namespace RetryProxy.Tests.Support;

/// <summary>测试用的配置样本。</summary>
internal static class TestConfigs
{
    /// <summary>
    /// 内置配置（两条通道、没有服务商），再给两个客户端各加一个服务商作为当前服务商，
    /// 这样通道才能生成运行时配置。服务商 ID 为 provider-codex / provider-claude。
    /// </summary>
    public static ProxyConfig BuiltinWithProviders()
    {
        var config = ProxyConfig.Builtin();
        foreach (var route in config.Routes)
        {
            var provider = new ProviderEndpoint("anyrouter.top", "https://anyrouter.top")
            {
                Id = $"provider-{route.ClientType.AsStr()}",
                ClientType = route.ClientType,
            };
            config.Providers.Add(provider);
            route.CurrentProviderId = provider.Id;
        }

        return config.Normalize();
    }
}
