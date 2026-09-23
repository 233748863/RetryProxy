using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RetryProxy.Core.Config;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>
/// 移植自 Rust config.rs 的单元测试，断言与原版一一对应。
/// </summary>
public class ProxyConfigTests
{
    private static ParsedProxyConfig Parse(string json) => ProxyConfigJson.Parse(json);

    private static JsonElement Canonical(ProxyConfig config)
    {
        // JsonDocument 生命周期交给测试进程；Clone 让元素脱离文档。
        using var document = ProxyConfigJson.ToCanonicalDocument(config);
        return document.RootElement.Clone();
    }

    private static ParsedProxyConfig RoundTrip(ProxyConfig config) => Parse(ProxyConfigJson.ToCanonicalJson(config));

    private static Dictionary<string, string> Env(params (string Key, string Value)[] pairs)
    {
        return pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    [Fact]
    public void TotalTimeoutMigratesPreservesLongAttemptsAndRoundTrips()
    {
        var (config, migrated) = Parse("""
            {
              "schema_version": 3,
              "providers": [{"name":"local","base_url":"https://example.test"}],
              "routes": [
                {"id":"one","name":"One","provider_name":"local","listen_port":18080},
                {"id":"two","name":"Two","provider_name":"local","listen_port":18081,"timeout_seconds":1200}
              ],
              "selected_route_id":"one"
            }
            """);
        Assert.True(migrated);
        Assert.Equal(600.0, config.Routes[0].TotalTimeoutSeconds);
        Assert.Equal(1200.0, config.Routes[1].TotalTimeoutSeconds);
        config.Routes[0].TotalTimeoutSeconds = 90.0;
        var (loaded, migratedAgain) = RoundTrip(config);
        Assert.False(migratedAgain);
        Assert.Equal(90.0, loaded.TotalTimeoutSeconds);
        Assert.Equal(1200.0, loaded.RuntimeConfigFor("two").TotalTimeoutSeconds);
    }

    [Fact]
    public void TotalTimeoutOverrideIsRouteLocalAndNotPersisted()
    {
        var config = ProxyConfig.Builtin();
        config.RuntimeOverrides = EnvironmentOverrides.Resolve(config, Env(("RETRY_TOTAL_TIMEOUT_SECONDS", "45")));
        config.Normalize();
        Assert.Equal(45.0, config.TotalTimeoutSeconds);
        Assert.Equal(45.0, config.RuntimeConfigFor(config.Routes[0].Id).TotalTimeoutSeconds);
        Assert.Equal(600.0, config.RuntimeConfigFor(config.Routes[1].Id).TotalTimeoutSeconds);
        var saved = Canonical(config);
        Assert.Equal(600.0, saved.GetProperty("total_timeout_seconds").GetDouble());
        Assert.Equal(600.0, saved.GetProperty("routes")[0].GetProperty("total_timeout_seconds").GetDouble());
    }

    [Fact]
    public void GenerationTimeoutMigratesAndRoundTripsIndependently()
    {
        var (config, migrated) = Parse("""
            {
              "schema_version": 4,
              "providers":[{"name":"local","base_url":"http://127.0.0.1:9999"}],
              "routes":[
                {"id":"one","name":"One","provider_name":"local","listen_port":18080},
                {"id":"two","name":"Two","provider_name":"local","listen_port":18081,"generation_timeout_seconds":450}
              ],
              "selected_route_id":"one"
            }
            """);
        Assert.True(migrated);
        Assert.Equal(300.0, config.GenerationTimeoutSeconds);
        Assert.Equal(450.0, config.Routes[1].GenerationTimeoutSeconds);
        config.Routes[0].GenerationTimeoutSeconds = 240.5;
        var (loaded, migratedAgain) = RoundTrip(config);
        Assert.False(migratedAgain);
        Assert.Equal(240.5, loaded.GenerationTimeoutSeconds);
        Assert.Equal(300.0, loaded.TimeoutSeconds);
        Assert.Equal(600.0, loaded.TotalTimeoutSeconds);
        Assert.Equal(450.0, loaded.RuntimeConfigFor("two").GenerationTimeoutSeconds);

        var (legacy, _) = Parse("""
            {
              "schema_version":4,
              "upstream_base_url":"http://127.0.0.1:9999",
              "timeout_seconds":900,
              "generation_timeout_seconds":450
            }
            """);
        Assert.Equal(450.0, legacy.Routes[0].GenerationTimeoutSeconds);
        Assert.Equal(900.0, legacy.TotalTimeoutSeconds);
    }

    [Fact]
    public void GenerationTimeoutOverrideIsRouteLocalAndNotPersisted()
    {
        var environment = Env(("RETRY_GENERATION_TIMEOUT_SECONDS", "45"));
        var config = ProxyConfig.Builtin();
        config.RuntimeOverrides = EnvironmentOverrides.Resolve(config, environment);
        config.Normalize();
        Assert.Equal(45.0, config.GenerationTimeoutSeconds);
        Assert.Equal(45.0, config.RuntimeConfigFor(config.Routes[0].Id).GenerationTimeoutSeconds);
        Assert.Equal(300.0, config.RuntimeConfigFor(config.Routes[1].Id).GenerationTimeoutSeconds);
        var saved = Canonical(config);
        Assert.Equal(300.0, saved.GetProperty("generation_timeout_seconds").GetDouble());
        Assert.Equal(300.0, saved.GetProperty("routes")[0].GetProperty("generation_timeout_seconds").GetDouble());

        var standalone = new ProxyConfig();
        standalone.RuntimeOverrides = EnvironmentOverrides.Resolve(standalone, environment);
        standalone.Normalize();
        Assert.Equal(45.0, standalone.GenerationTimeoutSeconds);
        Assert.Equal(300.0, Canonical(standalone).GetProperty("generation_timeout_seconds").GetDouble());
    }

    [Fact]
    public void GenerationTimeoutRejectsInvalidLimitsInConfigAndRoutes()
    {
        foreach (var generationTimeout in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, 86400.1 })
        {
            var config = new ProxyConfig { GenerationTimeoutSeconds = generationTimeout };
            var error = Assert.Throws<ConfigException>(() => config.Validate(false));
            Assert.Contains("等待生成上限", error.Message);

            var route = ProxyConfig.Builtin().Routes[0];
            route.GenerationTimeoutSeconds = generationTimeout;
            var routeError = Assert.Throws<ConfigException>(() => route.Validate());
            Assert.Contains("等待生成上限", routeError.Message);
        }

        foreach (var generationTimeout in new[] { 0.01, 86400.0 })
        {
            new ProxyConfig { GenerationTimeoutSeconds = generationTimeout }.Validate(false);
        }
    }

    [Fact]
    public void InvalidTotalTimeoutIsRejected()
    {
        foreach (var totalTimeout in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, 86400.1 })
        {
            var config = new ProxyConfig { TotalTimeoutSeconds = totalTimeout };
            var error = Assert.Throws<ConfigException>(() => config.Validate(false));
            Assert.Contains("总等待上限", error.Message);
        }
    }

    [Fact]
    public void EnvironmentOverridesSelectedRouteOnly()
    {
        var (config, _) = Parse("""
            {
              "providers":[{"name":"a","base_url":"https://a.example"},{"name":"b","base_url":"https://b.example"}],
              "selected_route_id":"a",
              "routes":[
                {"id":"a","name":"A","provider_name":"a","listen_port":18080},
                {"id":"b","name":"B","provider_name":"b","listen_port":18081}
              ]
            }
            """);
        var runtime = config.Clone();
        runtime.RuntimeOverrides = EnvironmentOverrides.Resolve(config, Env(("RETRY_PROXY_PORT", "19000")));
        runtime.Normalize();
        Assert.Equal(19000, runtime.ListenPort);
        Assert.Equal(18081, runtime.Routes[1].ListenPort);
    }

    [Fact]
    public void BuiltinConfigMatchesCurrentReleaseDefaults()
    {
        var config = ProxyConfig.Builtin();
        Assert.Equal(2, config.Providers.Count);
        Assert.Equal("https://anyrouter.top", config.Providers[0].BaseUrl);
        Assert.Equal("https://sotamodel.net", config.Providers[1].BaseUrl);
        Assert.Equal(2, config.Routes.Count);
        Assert.Equal(18080, config.Routes[0].ListenPort);
        Assert.Equal(18081, config.Routes[1].ListenPort);
        Assert.All(config.Routes, route => Assert.Equal(ConfigDefaults.KeepaliveIdleMinutes, route.KeepaliveIdleMinutes));
    }

    /// <summary>
    /// 每条通道的启停意愿要能原样存回来读出来。软件打开时按这个字段决定
    /// 哪些通道自动启动，存丢了用户下次打开就发现通道全停着。
    /// </summary>
    [Fact]
    public void EachRouteKeepsItsOwnDesiredRunningAcrossASaveAndLoad()
    {
        var config = ProxyConfig.Builtin();
        config.Routes[0].DesiredRunning = true;
        config.Routes[1].DesiredRunning = true;
        var (loaded, _) = RoundTrip(config);
        Assert.All(loaded.Routes, route => Assert.True(route.DesiredRunning));

        config.Routes[1].DesiredRunning = false;
        (loaded, _) = RoundTrip(config);
        Assert.True(loaded.Routes[0].DesiredRunning);
        Assert.False(loaded.Routes[1].DesiredRunning);
    }

    [Fact]
    public void LegacyRouteKeepaliveStaysOnItsOwnRoute()
    {
        var (config, migrated) = Parse("""
            {
              "schema_version": 2,
              "providers": [{"name": "a", "base_url": "https://a.example"}],
              "selected_route_id": "one",
              "routes": [
                {
                  "id": "one", "name": "One", "provider_name": "a", "listen_port": 18080,
                  "keepalive_enabled": true, "keepalive_idle_minutes": 7.5
                },
                {"id": "two", "name": "Two", "provider_name": "a", "listen_port": 18081}
              ]
            }
            """);
        Assert.True(migrated);
        Assert.True(config.RuntimeConfigFor("one").KeepaliveEnabled);
        Assert.Equal(7.5, config.Routes[0].KeepaliveIdleMinutes);
        Assert.False(config.RuntimeConfigFor("two").KeepaliveEnabled);
        Assert.Equal(ConfigDefaults.KeepaliveIdleMinutes, config.Routes[1].KeepaliveIdleMinutes);
        var saved = Canonical(config);
        Assert.False(saved.GetProperty("providers")[0].TryGetProperty("keepalive_enabled", out _));
        Assert.True(saved.GetProperty("routes")[0].GetProperty("keepalive_enabled").GetBoolean());

        var (roundTrip, migratedAgain) = RoundTrip(config);
        Assert.False(migratedAgain);
        Assert.Equal(config.Providers, roundTrip.Providers);
        Assert.Equal(config.Routes, roundTrip.Routes);
    }

    [Fact]
    public void ProviderKeepaliveMigratesToIndependentRoutesAndRoundTrips()
    {
        var (config, migrated) = Parse("""
            {
              "schema_version": 5,
              "providers":[
                {"name":" a ","base_url":"https://a.example/","keepalive_enabled":true,"keepalive_idle_minutes":9.5,"keepalive_context_limit":62000},
                {"name":"b","base_url":"https://b.example","keepalive_enabled":false,"keepalive_idle_minutes":11.0,"keepalive_context_limit":70000}
              ],
              "routes":[
                {"id":"one","name":"One","provider_name":"a","listen_port":18080},
                {"id":"two","name":"Two","provider_name":" A ","listen_port":18081},
                {"id":"other","name":"Other","provider_name":"b","listen_port":18082}
              ]
            }
            """);
        Assert.True(migrated);
        config.Validate(false);
        Assert.True(config.RuntimeConfigFor("one").KeepaliveEnabled);
        Assert.True(config.RuntimeConfigFor("two").KeepaliveEnabled);
        Assert.False(config.RuntimeConfigFor("other").KeepaliveEnabled);
        foreach (var route in config.Routes.Take(2))
        {
            Assert.Equal(9.5, route.KeepaliveIdleMinutes);
            Assert.Equal(62000, route.KeepaliveContextLimit);
        }

        Assert.Equal(11.0, config.Routes[2].KeepaliveIdleMinutes);
        Assert.Equal(70000, config.Routes[2].KeepaliveContextLimit);

        config.Routes[1].KeepaliveEnabled = false;
        config.Routes[1].KeepaliveIdleMinutes = 2.5;
        config.Routes[1].KeepaliveContextLimit = 12345;
        config.SelectedRouteId = "two";
        config.Normalize();
        Assert.False(config.KeepaliveEnabled);
        Assert.Equal(2.5, config.KeepaliveIdleMinutes);
        Assert.Equal(12345, config.KeepaliveContextLimit);
        var first = config.RuntimeConfigFor("one");
        Assert.True(first.KeepaliveEnabled);
        Assert.Equal(9.5, first.KeepaliveIdleMinutes);
        Assert.Equal(62000, first.KeepaliveContextLimit);
        var second = config.RuntimeConfigFor("two");
        Assert.False(second.KeepaliveEnabled);
        Assert.Equal(2.5, second.KeepaliveIdleMinutes);
        Assert.Equal(12345, second.KeepaliveContextLimit);
        Assert.Equal(config, config.Clone().Normalize());
        var (loaded, migratedAgain) = RoundTrip(config);
        Assert.False(migratedAgain);
        Assert.Equal(config, loaded);
    }

    [Fact]
    public void ExplicitRouteSettingsOverrideLegacyProviderFieldsIndividually()
    {
        var (config, migrated) = Parse("""
            {
              "schema_version":5,
              "providers":[{"name":"a","base_url":"https://a.example","keepalive_enabled":true,"keepalive_idle_minutes":12.0,"keepalive_context_limit":90000}],
              "routes":[
                {"id":"one","name":"One","provider_name":"a","listen_port":18080,"keepalive_enabled":false,"keepalive_idle_minutes":9.0,"keepalive_context_limit":111},
                {"id":"two","name":"Two","provider_name":"a","listen_port":18081,"keepalive_enabled":true,"keepalive_idle_minutes":5.0},
                {"id":"three","name":"Three","provider_name":"a","listen_port":18082,"keepalive_enabled":false,"keepalive_context_limit":100}
              ]
            }
            """);
        Assert.True(migrated);
        Assert.Equal(ConfigDefaults.CurrentSchemaVersion, config.SchemaVersion);
        var actual = config.Routes
            .Select(route => (route.KeepaliveEnabled, route.KeepaliveIdleMinutes, route.KeepaliveContextLimit))
            .ToList();
        Assert.Equal(
            new List<(bool, double, long)> { (false, 9.0, 111), (true, 5.0, 90000), (false, 12.0, 100) },
            actual);
        Assert.Equal(config, RoundTrip(config).Config);
    }

    [Fact]
    public void LegacySingleAddressKeepsItsProviderKeepaliveSettings()
    {
        var (config, migrated) = Parse("""
            {
              "upstream_base_url":"https://a.example",
              "providers":[{"name":"a","base_url":"https://a.example","keepalive_enabled":true,"keepalive_idle_minutes":8.0,"keepalive_context_limit":64000}]
            }
            """);
        Assert.True(migrated);
        Assert.Single(config.Routes);
        Assert.True(config.KeepaliveEnabled);
        Assert.Equal(8.0, config.KeepaliveIdleMinutes);
        Assert.Equal(64000, config.KeepaliveContextLimit);
    }

    [Fact]
    public void RouteKeepaliveSettingsAreValidated()
    {
        var route = ProxyConfig.Builtin().Routes[0];
        foreach (var idle in new[] { 0.0, 0.49, 1440.01, double.NaN, double.PositiveInfinity })
        {
            route.KeepaliveIdleMinutes = idle;
            Assert.Throws<ConfigException>(() => route.Validate());
        }

        foreach (var idle in new[] { 0.5, 1440.0 })
        {
            route.KeepaliveIdleMinutes = idle;
            route.Validate();
        }

        route.KeepaliveContextLimit = 0;
        Assert.Throws<ConfigException>(() => route.Validate());
    }

    [Fact]
    public void InvalidLegacyProviderKeepaliveFieldsAreNotSilentlyDefaulted()
    {
        foreach (var (key, invalid) in new[]
                 {
                     ("keepalive_enabled", "\"true\""),
                     ("keepalive_idle_minutes", "\"3\""),
                     ("keepalive_context_limit", "-1"),
                 })
        {
            var json = $$"""
                {
                  "providers":[{"name":"a","base_url":"https://a.example","{{key}}":{{invalid}}}],
                  "routes":[{"id":"one","name":"One","provider_name":"a","listen_port":18080}]
                }
                """;
            Assert.Throws<ConfigException>(() => Parse(json));
        }
    }

    [Fact]
    public void ExplicitClientTypesRoundTripWithoutNameOrPortInference()
    {
        var (config, migrated) = Parse($$"""
            {
              "schema_version":{{ConfigDefaults.CurrentSchemaVersion}},
              "providers":[{"name":"a","base_url":"https://a.example"}],
              "routes":[
                {"id":"one","name":"Claude named channel","provider_name":"a","listen_port":18081,"client_type":"codex"},
                {"id":"two","name":"Codex named channel","provider_name":"a","listen_port":18080,"client_type":"claude"}
              ]
            }
            """);
        Assert.False(migrated);
        Assert.Equal(ClientType.Codex, config.RuntimeConfigFor("one").ClientType);
        Assert.Equal(ClientType.Claude, config.RuntimeConfigFor("two").ClientType);
        config.Routes[0].Name = "renamed";
        config.Routes[0].ListenPort = 19080;
        var (loaded, migratedAgain) = RoundTrip(config);
        Assert.False(migratedAgain);
        Assert.Equal(ClientType.Codex, loaded.Routes[0].ClientType);
        Assert.Equal(ClientType.Claude, loaded.Routes[1].ClientType);
    }

    [Fact]
    public void CurrentConfigsRequireAValidExplicitClientType()
    {
        foreach (var value in new string?[] { null, "null", "\"auto\"", "true" })
        {
            var clientType = value is null ? string.Empty : $$""","client_type":{{value}}""";
            var json = $$"""
                {
                  "schema_version":{{ConfigDefaults.CurrentSchemaVersion}},
                  "providers":[{"name":"a","base_url":"https://a.example"}],
                  "routes":[{"id":"one","name":"Claude","provider_name":"a","listen_port":18081{{clientType}}}]
                }
                """;
            var error = Assert.Throws<ConfigException>(() => Parse(json));
            Assert.Contains("client_type", error.Message);
        }
    }

    [Fact]
    public void CurrentSingleAddressConfigsRequireAnExplicitClientType()
    {
        var missing = $$"""
            {
              "schema_version":{{ConfigDefaults.CurrentSchemaVersion}},
              "upstream_base_url":"https://a.example",
              "listen_port":18081
            }
            """;
        var error = Assert.Throws<ConfigException>(() => Parse(missing));
        Assert.Contains("client_type", error.Message);

        var explicitCodex = $$"""
            {
              "schema_version":{{ConfigDefaults.CurrentSchemaVersion}},
              "upstream_base_url":"https://a.example",
              "listen_port":18081,
              "client_type":"codex"
            }
            """;
        var (config, _) = Parse(explicitCodex);
        Assert.Equal(ClientType.Codex, config.Routes[0].ClientType);
        Assert.Equal(ClientType.Codex, config.ClientType);
    }

    [Fact]
    public void LegacyClientTypeIsMigratedOnceThenSavedExplicitly()
    {
        var (config, migrated) = Parse("""
            {
              "schema_version":5,
              "providers":[{"name":"a","base_url":"https://a.example"}],
              "routes":[
                {"id":"one","name":"custom","provider_name":"a","listen_port":18081},
                {"id":"two","name":"codex cli","provider_name":"a","listen_port":18080}
              ]
            }
            """);
        Assert.True(migrated);
        var stored = Canonical(config);
        Assert.Equal("claude", stored.GetProperty("routes")[0].GetProperty("client_type").GetString());
        Assert.Equal("codex", stored.GetProperty("routes")[1].GetProperty("client_type").GetString());

        // 改端口、改名字后重新读取，客户端类型不再被推断覆盖。
        config.Routes[0].ListenPort = 19090;
        config.Routes[1].Name = "Claude renamed";
        var (loaded, migratedAgain) = RoundTrip(config);
        Assert.False(migratedAgain);
        Assert.Equal(ClientType.Claude, loaded.Routes[0].ClientType);
        Assert.Equal(ClientType.Codex, loaded.Routes[1].ClientType);
    }

    [Fact]
    public void CanonicalJsonUsesFixedKeyOrder()
    {
        using var document = ProxyConfigJson.ToCanonicalDocument(ProxyConfig.Builtin());
        var topLevel = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "schema_version", "client_type", "upstream_base_url", "listen_port", "max_retries",
                "timeout_seconds", "generation_timeout_seconds", "total_timeout_seconds",
                "base_delay_seconds", "max_delay_seconds", "desired_running", "providers",
                "selected_route_id", "routes",
            },
            topLevel);
        var routeKeys = document.RootElement.GetProperty("routes")[0].EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "id", "name", "provider_name", "client_type", "listen_port", "max_retries",
                "timeout_seconds", "generation_timeout_seconds", "total_timeout_seconds",
                "base_delay_seconds", "max_delay_seconds", "desired_running",
                "keepalive_enabled", "keepalive_idle_minutes", "keepalive_context_limit",
            },
            routeKeys);
    }

    [Fact]
    public void ValidationMessagesMatchRustWording()
    {
        var duplicateName = ProxyConfig.Builtin();
        duplicateName.Providers.Add(new ProviderEndpoint("ANYROUTER.TOP", "https://other.example"));
        Assert.Equal("服务商名称重复：ANYROUTER.TOP", Assert.Throws<ConfigException>(() => duplicateName.Validate(false)).Message);

        var matchingAddress = ProxyConfig.Builtin();
        matchingAddress.Providers.Add(new ProviderEndpoint("independent", matchingAddress.Providers[0].BaseUrl));
        matchingAddress.Validate(false);

        var duplicatePort = ProxyConfig.Builtin();
        duplicatePort.Routes[1].ListenPort = 18080;
        Assert.Equal("本地端口重复：18080", Assert.Throws<ConfigException>(() => duplicatePort.Validate(false)).Message);

        var missingProvider = ProxyConfig.Builtin();
        missingProvider.Routes[1].ProviderName = "nowhere";
        Assert.Equal("转发通道“Claude Code”引用的服务商不存在：nowhere", Assert.Throws<ConfigException>(() => missingProvider.Validate(false)).Message);

        var badUrl = new ProviderEndpoint("x", "https://user:pw@example.com");
        Assert.Equal("服务商地址不能包含用户名或密码", Assert.Throws<ConfigException>(() => badUrl.Validate()).Message);

        var query = new ProviderEndpoint("x", "https://example.com/v1?x=1");
        Assert.Equal("服务商地址不能包含查询参数或片段", Assert.Throws<ConfigException>(() => query.Validate()).Message);

        var empty = new ProxyConfig();
        Assert.Equal("请先新增并选择服务商", Assert.Throws<ConfigException>(() => empty.Validate(true)).Message);
    }
}
