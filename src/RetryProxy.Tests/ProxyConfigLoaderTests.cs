using System;
using System.Collections.Generic;
using RetryProxy.Core.Config;
using Xunit;

namespace RetryProxy.Tests;

public class ProxyConfigLoaderTests
{
    private static Dictionary<string, string> Env(params (string Key, string Value)[] pairs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            result[key] = value;
        }

        return result;
    }

    [Fact]
    public void FallsBackToBuiltinWhenNothingIsPersisted()
    {
        var result = ProxyConfigLoader.Load(null, Env(), () => null);
        Assert.Equal(ProxyConfigSource.Builtin, result.Source);
        Assert.Equal(ProxyConfig.Builtin(), result.Config);
        Assert.False(result.Migrated);
    }

    [Fact]
    public void ImportsLegacyRegistryConfigOnceWhenNoConfigFileExists()
    {
        const string legacy = """
            {
              "schema_version": 5,
              "upstream_base_url": "https://legacy.example",
              "listen_port": 18081,
              "providers": [{"name":"legacy","base_url":"https://legacy.example"}]
            }
            """;
        var result = ProxyConfigLoader.Load(null, Env(), () => legacy);
        Assert.Equal(ProxyConfigSource.Registry, result.Source);
        Assert.True(result.Migrated);
        var route = Assert.Single(result.Config.Routes);
        Assert.Equal("legacy-default", route.Id);
        Assert.Equal(ClientType.Claude, route.ClientType);
        Assert.Equal("legacy", route.ProviderName);
    }

    [Fact]
    public void ConfigFileTakesPrecedenceOverRegistry()
    {
        var persisted = ProxyConfig.Builtin();
        persisted.Routes[0].Name = "来自文件";
        persisted.Normalize();
        var registryReads = 0;
        var result = ProxyConfigLoader.Load(persisted, Env(), () =>
        {
            registryReads++;
            return "{}";
        });
        Assert.Equal(ProxyConfigSource.ConfigFile, result.Source);
        Assert.Equal(0, registryReads);
        Assert.Equal("来自文件", result.Config.Routes[0].Name);
    }

    [Fact]
    public void TestInjectionWinsAndMarksSaveAsNoop()
    {
        const string injected = """
            {
              "schema_version": 6,
              "providers": [{"name":"mock","base_url":"http://127.0.0.1:9"}],
              "routes": [{"id":"r","name":"R","provider_name":"mock","client_type":"codex","listen_port":19999}],
              "selected_route_id": "r"
            }
            """;
        var environ = Env((ProxyConfigLoader.TestConfigEnv, injected), ("RETRY_MAX_RETRIES", "3"));
        var result = ProxyConfigLoader.Load(ProxyConfig.Builtin(), environ, () => throw new InvalidOperationException("不应读取注册表"));
        Assert.Equal(ProxyConfigSource.EnvironmentInjection, result.Source);
        Assert.True(ProxyConfigLoader.IsTestInjectionActive(environ));
        Assert.False(ProxyConfigLoader.IsTestInjectionActive(Env()));
        Assert.Equal(19999, result.Config.ListenPort);
        Assert.Equal(3, result.Config.MaxRetries);
        Assert.Equal(6, result.Config.Routes[0].MaxRetries);
    }

    [Fact]
    public void InvalidEnvironmentValuesAreRejectedWithRustWording()
    {
        var error = Assert.Throws<ConfigException>(() =>
            ProxyConfigLoader.Load(null, Env(("RETRY_PROXY_PORT", "abc")), () => null));
        Assert.Equal("环境变量 RETRY_PROXY_PORT 的值无效", error.Message);
    }

    [Fact]
    public void BrokenRegistryJsonIsReportedNotSwallowed()
    {
        var error = Assert.Throws<ConfigException>(() => ProxyConfigLoader.Load(null, Env(), () => "{not json"));
        Assert.StartsWith("无法读取持久化配置：", error.Message);
    }
}
