using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>
/// 移植自 Rust config.rs 的单元测试，另含 schema 7（PRD-供应商管理 §8）的读写与迁移用例。
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
    public void LegacyPreparationFieldsAreDroppedWithoutRemovingTheRoute()
    {
        var parsed = Parse("""
            {
              "providers": [{"name":"existing","base_url":"https://example.test"}],
              "routes": [{
                "id":"old-route","name":"Existing route","provider_name":"existing","listen_port":18080,
                "dedicated_preparation":true,"protected_api_key":"legacy-encrypted-key","preparation_model":"old-model"
              }],
              "selected_route_id":"old-route"
            }
            """);
        Assert.Single(parsed.Config.Providers);
        Assert.Equal(2, parsed.Config.Routes.Count);
        Assert.Equal("old-route", parsed.Config.Routes[0].Id);
        var canonical = ProxyConfigJson.ToCanonicalJson(parsed.Config);
        Assert.DoesNotContain("dedicated_preparation", canonical);
        Assert.DoesNotContain("protected_api_key", canonical);
        Assert.DoesNotContain("preparation_model", canonical);
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
        var config = TestConfigs.BuiltinWithProviders();
        config.RuntimeOverrides = EnvironmentOverrides.Resolve(config, Env(("RETRY_TOTAL_TIMEOUT_SECONDS", "45")));
        config.Normalize();
        Assert.Equal(45.0, config.TotalTimeoutSeconds);
        Assert.Equal(45.0, config.RuntimeConfigFor(config.Routes[0].Id).TotalTimeoutSeconds);
        Assert.Equal(600.0, config.RuntimeConfigFor(config.Routes[1].Id).TotalTimeoutSeconds);
        var saved = Canonical(config);
        Assert.False(saved.TryGetProperty("total_timeout_seconds", out _));
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
        var config = TestConfigs.BuiltinWithProviders();
        config.RuntimeOverrides = EnvironmentOverrides.Resolve(config, environment);
        config.Normalize();
        Assert.Equal(45.0, config.GenerationTimeoutSeconds);
        Assert.Equal(45.0, config.RuntimeConfigFor(config.Routes[0].Id).GenerationTimeoutSeconds);
        Assert.Equal(300.0, config.RuntimeConfigFor(config.Routes[1].Id).GenerationTimeoutSeconds);
        var saved = Canonical(config);
        Assert.Equal(300.0, saved.GetProperty("routes")[0].GetProperty("generation_timeout_seconds").GetDouble());

        var standalone = new ProxyConfig();
        standalone.RuntimeOverrides = EnvironmentOverrides.Resolve(standalone, environment);
        standalone.Normalize();
        Assert.Equal(45.0, standalone.GenerationTimeoutSeconds);
        Assert.DoesNotContain("45", ProxyConfigJson.ToCanonicalJson(standalone));
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
        runtime.RuntimeOverrides = EnvironmentOverrides.Resolve(config, Env(("RETRY_PROXY_PORT", "19000"), ("UPSTREAM_BASE_URL", "http://127.0.0.1:9")));
        runtime.Normalize();
        Assert.Equal(19000, runtime.ListenPort);
        Assert.Equal(18081, runtime.Routes[1].ListenPort);
        // UPSTREAM_BASE_URL 只覆盖选中通道的当前服务商地址，不改服务商本身。
        Assert.Equal("http://127.0.0.1:9", runtime.RuntimeConfigFor("a").UpstreamBaseUrl);
        Assert.Equal("https://b.example", runtime.RuntimeConfigFor("b").UpstreamBaseUrl);
        Assert.Equal("https://a.example", runtime.ProviderById(runtime.Routes[0].CurrentProviderId)!.BaseUrl);
    }

    [Fact]
    public void BuiltinConfigHasOneChannelPerClientAndNoProviders()
    {
        var config = ProxyConfig.Builtin();
        Assert.Empty(config.Providers);
        Assert.Equal(
            new[] { ("legacy-default", "Codex", ClientType.Codex, 18080), ("2da608c46f0842039fdf8ad07e46cf20", "Claude Code", ClientType.Claude, 18081) },
            config.Routes.Select(route => (route.Id, route.Name, route.ClientType, route.ListenPort)));
        Assert.All(config.Routes, route => Assert.Equal(ConfigDefaults.KeepaliveIdleMinutes, route.KeepaliveIdleMinutes));
        Assert.All(config.Routes, route => Assert.Matches("^[0-9a-f]{32}$", route.LocalToken));
        Assert.All(config.Routes, route => Assert.Empty(route.CurrentProviderId));
        Assert.NotEqual(config.Routes[0].LocalToken, config.Routes[1].LocalToken);
        Assert.Equal(new[] { ClientType.Codex, ClientType.Claude }, config.ClientTakeover.Keys.OrderBy(client => client));
        Assert.All(config.ClientTakeover.Values, state => Assert.False(state.Enabled));
        config.Validate(false);
        Assert.Equal("请先为 Codex 新增并选择服务商", Assert.Throws<ConfigException>(() => config.RuntimeConfigFor("legacy-default")).Message);
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
        var parsed = Parse("""
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
        var config = parsed.Config;
        Assert.True(parsed.Migrated);
        config.Validate(false);
        // Other 与 One 都是 Codex：没有选中通道时保留列表里的第一条。
        Assert.Equal(new[] { "one", "two" }, config.Routes.Select(route => route.Id));
        Assert.Contains("每个客户端只保留一条通道，已移除：Other（Codex，端口 18082）", parsed.Notes);
        Assert.True(config.RuntimeConfigFor("one").KeepaliveEnabled);
        Assert.True(config.RuntimeConfigFor("two").KeepaliveEnabled);
        foreach (var route in config.Routes)
        {
            Assert.Equal(9.5, route.KeepaliveIdleMinutes);
            Assert.Equal(62000, route.KeepaliveContextLimit);
        }

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
        // 只取 JSON 里给出的通道；缺客户端时迁移会补建一条默认通道，不在比较范围内。
        static List<(bool, double, long)> Keepalive(string routes, params string[] ids)
        {
            var (config, migrated) = Parse($$"""
                {
                  "schema_version":5,
                  "providers":[{"name":"a","base_url":"https://a.example","keepalive_enabled":true,"keepalive_idle_minutes":12.0,"keepalive_context_limit":90000}],
                  "routes":[{{routes}}]
                }
                """);
            Assert.True(migrated);
            Assert.Equal(ConfigDefaults.CurrentSchemaVersion, config.SchemaVersion);
            Assert.Equal(config, RoundTrip(config).Config);
            return config.Routes
                .Where(route => ids.Contains(route.Id))
                .Select(route => (route.KeepaliveEnabled, route.KeepaliveIdleMinutes, route.KeepaliveContextLimit))
                .ToList();
        }

        Assert.Equal(
            new List<(bool, double, long)> { (false, 9.0, 111), (true, 5.0, 90000) },
            Keepalive("""
                {"id":"one","name":"One","provider_name":"a","listen_port":18080,"keepalive_enabled":false,"keepalive_idle_minutes":9.0,"keepalive_context_limit":111},
                {"id":"two","name":"Two","provider_name":"a","listen_port":18081,"keepalive_enabled":true,"keepalive_idle_minutes":5.0}
                """, "one", "two"));
        Assert.Equal(
            new List<(bool, double, long)> { (false, 12.0, 100) },
            Keepalive("""
                {"id":"three","name":"Three","provider_name":"a","listen_port":18082,"keepalive_enabled":false,"keepalive_context_limit":100}
                """, "three"));
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
        Assert.Equal(2, config.Routes.Count);
        Assert.Equal("legacy-default", config.SelectedRouteId);
        Assert.True(config.KeepaliveEnabled);
        Assert.Equal(8.0, config.KeepaliveIdleMinutes);
        Assert.Equal(64000, config.KeepaliveContextLimit);
        Assert.Equal("https://a.example", config.UpstreamBaseUrl);
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
              "providers":[
                {"id":"pa","client_type":"codex","name":"a","base_url":"https://a.example"},
                {"id":"pb","client_type":"claude","name":"a","base_url":"https://a.example"}
              ],
              "routes":[
                {"id":"one","name":"Claude named channel","client_type":"codex","current_provider_id":"pa","local_token":"t1","listen_port":18081},
                {"id":"two","name":"Codex named channel","client_type":"claude","current_provider_id":"pb","local_token":"t2","listen_port":18080}
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
        Assert.Equal(("t1", "t2"), (loaded.Routes[0].LocalToken, loaded.Routes[1].LocalToken));
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
                  "routes":[{"id":"one","name":"Claude","listen_port":18081{{clientType}}}]
                }
                """;
            var error = Assert.Throws<ConfigException>(() => Parse(json));
            Assert.Contains("client_type", error.Message);

            var provider = $$"""
                {
                  "schema_version":{{ConfigDefaults.CurrentSchemaVersion}},
                  "providers":[{"id":"p","name":"a","base_url":"https://a.example"{{clientType}}}]
                }
                """;
            Assert.Contains("client_type", Assert.Throws<ConfigException>(() => Parse(provider)).Message);
        }
    }

    [Fact]
    public void Schema6SingleAddressConfigsRequireAnExplicitClientType()
    {
        var missing = """
            {
              "schema_version":6,
              "upstream_base_url":"https://a.example",
              "listen_port":18081
            }
            """;
        var error = Assert.Throws<ConfigException>(() => Parse(missing));
        Assert.Contains("client_type", error.Message);

        var explicitCodex = """
            {
              "schema_version":6,
              "upstream_base_url":"https://a.example",
              "listen_port":18081,
              "client_type":"codex"
            }
            """;
        var (config, _) = Parse(explicitCodex);
        Assert.Equal(ClientType.Codex, config.Routes[0].ClientType);
        Assert.Equal(ClientType.Codex, config.ClientType);
        // 18081 已被旧通道占用，补建的 Claude Code 通道顺延到 18082。
        Assert.Equal(18082, config.RouteFor(ClientType.Claude)!.ListenPort);
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

    /// <summary>
    /// 用正在使用的 dist\User\config.json 的结构（2026-09-28）：两条通道都用 "Any"，选中 Claude 通道。
    /// 迁移后通道 ID 不变（每日统计按它分目录），各客户端得到自己的 "Any"。
    /// </summary>
    [Fact]
    public void DistSchema6ConfigMigratesKeepingRouteIds()
    {
        var parsed = Parse("""
            {
             "schema_version": 6, "client_type": "claude", "upstream_base_url": "https://anyrouter.top", "listen_port": 18081,
             "max_retries": 100000, "timeout_seconds": 600, "generation_timeout_seconds": 300, "total_timeout_seconds": 600,
             "base_delay_seconds": 0.5, "max_delay_seconds": 2, "desired_running": true,
             "providers": [{"name": "Any", "base_url": "https://anyrouter.top"}],
             "selected_route_id": "c344a3ddfcd2411a883544a351dedbae",
             "routes": [
              {"id": "bbd24d995d29461baa327992bf744618", "name": "codex", "provider_name": "Any", "client_type": "codex", "listen_port": 18080,
               "max_retries": 100000, "timeout_seconds": 600, "generation_timeout_seconds": 300, "total_timeout_seconds": 600,
               "base_delay_seconds": 0.5, "max_delay_seconds": 2, "desired_running": true, "keepalive_enabled": true,
               "keepalive_idle_minutes": 7, "keepalive_context_limit": 50000, "keepalive_reasoning_effort": "medium", "pass_through_compression": false},
              {"id": "c344a3ddfcd2411a883544a351dedbae", "name": "claude", "provider_name": "Any", "client_type": "claude", "listen_port": 18081,
               "max_retries": 100000, "timeout_seconds": 600, "generation_timeout_seconds": 300, "total_timeout_seconds": 600,
               "base_delay_seconds": 0.5, "max_delay_seconds": 2, "desired_running": true, "keepalive_enabled": true,
               "keepalive_idle_minutes": 7, "keepalive_context_limit": 50000, "keepalive_reasoning_effort": "medium", "pass_through_compression": false}
             ]
            }
            """);
        var config = parsed.Config;
        Assert.True(parsed.Migrated);
        Assert.Empty(parsed.Notes);
        config.Validate(false);
        Assert.Equal(
            new[] { ("bbd24d995d29461baa327992bf744618", "Codex", 18080), ("c344a3ddfcd2411a883544a351dedbae", "Claude Code", 18081) },
            config.Routes.Select(route => (route.Id, route.Name, route.ListenPort)));
        Assert.Equal("c344a3ddfcd2411a883544a351dedbae", config.SelectedRouteId);
        Assert.Equal(new[] { ClientType.Codex, ClientType.Claude }, config.Providers.Select(provider => provider.ClientType));
        Assert.All(config.Providers, provider =>
        {
            Assert.Equal(("Any", "https://anyrouter.top"), (provider.Name, provider.BaseUrl));
            Assert.Empty(provider.Keys);
            Assert.Equal(ClaudeAuthMode.Bearer, provider.AuthMode);
            Assert.Equal(BalanceQueryMode.Auto, provider.BalanceQuery.Mode);
        });
        Assert.NotEqual(config.Providers[0].Id, config.Providers[1].Id);
        foreach (var route in config.Routes)
        {
            var provider = config.ProviderById(route.CurrentProviderId)!;
            Assert.Equal(route.ClientType, provider.ClientType);
            Assert.Empty(route.CurrentKeyId);
            Assert.Null(config.CurrentKeyOf(route));
            Assert.Matches("^[0-9a-f]{32}$", route.LocalToken);
            Assert.Equal((100000L, 2.0, true, true, 7.0, ReasoningEffort.Medium),
                (route.MaxRetries, route.MaxDelaySeconds, route.DesiredRunning, route.KeepaliveEnabled, route.KeepaliveIdleMinutes, route.KeepaliveReasoningEffort));
        }

        Assert.All(config.ClientTakeover.Values, state => Assert.False(state.Enabled));
        Assert.Equal("https://anyrouter.top", config.RuntimeConfigFor("bbd24d995d29461baa327992bf744618").UpstreamBaseUrl);

        var (loaded, migratedAgain) = RoundTrip(config);
        Assert.False(migratedAgain);
        Assert.Equal(config, loaded);
        Assert.Equal(ProxyConfigJson.ToCanonicalJson(config), ProxyConfigJson.ToCanonicalJson(loaded));
    }

    [Fact]
    public void MigrationKeepsTheSelectedRouteOfEachClientAndProvidersWithoutRoutes()
    {
        var parsed = Parse("""
            {
              "schema_version": 6,
              "providers": [
                {"name":"main","base_url":"https://main.example"},
                {"name":"backup","base_url":"https://backup.example"},
                {"name":"spare","base_url":"https://spare.example"}
              ],
              "selected_route_id": "second",
              "routes": [
                {"id":"first","name":"Codex A","provider_name":"main","client_type":"codex","listen_port":18080},
                {"id":"second","name":"Codex B","provider_name":"backup","client_type":"codex","listen_port":18082}
              ]
            }
            """);
        var config = parsed.Config;
        Assert.Equal("second", config.RouteFor(ClientType.Codex)!.Id);
        Assert.Equal("second", config.SelectedRouteId);
        Assert.Equal("backup", config.ProviderById(config.RouteFor(ClientType.Codex)!.CurrentProviderId)!.Name);
        // 被移除通道用过的 main 仍作为 Codex 服务商保留；没有通道用过的 spare 两个客户端各留一份。
        Assert.Equal(
            new[] { ("main", ClientType.Codex), ("backup", ClientType.Codex), ("spare", ClientType.Codex), ("spare", ClientType.Claude) },
            config.Providers.Select(provider => (provider.Name, provider.ClientType)));
        var claude = config.RouteFor(ClientType.Claude)!;
        Assert.Equal((18081, "Claude Code", string.Empty), (claude.ListenPort, claude.Name, claude.CurrentProviderId));
        Assert.Equal(
            new[]
            {
                "每个客户端只保留一条通道，已移除：Codex A（Codex，端口 18080）",
                "服务商“spare”没有通道在用，已同时保留到 Codex 与 Claude Code",
                "已为 Claude Code 新建通道（端口 18081）",
            },
            parsed.Notes);
        config.Validate(false);
    }

    [Fact]
    public void Schema7ProvidersKeysModelsAndTakeoverRoundTrip()
    {
        var config = TestConfigs.BuiltinWithProviders();
        var claude = config.Providers.Single(provider => provider.ClientType == ClientType.Claude);
        claude.AuthMode = ClaudeAuthMode.ApiKey;
        claude.WebsiteUrl = "https://anyrouter.top/console";
        claude.Notes = "群里的站";
        claude.Models = new ProviderModels
        {
            Model = "claude-opus-5-5",
            Context1M = true,
            Haiku = new RoleModel { Model = "claude-haiku-4-5", Context1M = false },
            Fable = new RoleModel { Model = "claude-fable-5", Context1M = true },
        };
        claude.BalanceQuery = new BalanceQuery { Mode = BalanceQueryMode.Auto, Detected = BalanceQueryMode.OpenAiBilling };
        claude.Keys.Add(new ProviderKey { Id = "k1", Name = "主号", ApiKey = "sk-main-fixture" });
        claude.Keys.Add(new ProviderKey
        {
            Id = "k2", Name = "群号", ApiKey = "sk-group-fixture", Notes = "备用",
            ModelOverride = new KeyModelOverride { Model = "claude-sonnet-5", Context1M = true },
        });
        var codex = config.Providers.Single(provider => provider.ClientType == ClientType.Codex);
        codex.Models = new ProviderModels { Model = "gpt-5.5", ContextWindow = 400000, AutoCompactTokenLimit = 350000 };
        codex.BalanceQuery = new BalanceQuery { Mode = BalanceQueryMode.None };
        config.RouteFor(ClientType.Claude)!.CurrentKeyId = "k2";
        config.ClientTakeover[ClientType.Claude] = new ClientTakeoverState
        {
            Enabled = true, ConfigPath = @"C:\Users\me\.claude\settings.json", BackupPath = @"D:\backup", LastWrittenHash = "abc",
        };
        config.Validate(false);

        var (loaded, migrated) = RoundTrip(config);
        Assert.False(migrated);
        Assert.Equal(config, loaded);
        Assert.Equal("k2", loaded.CurrentKeyOf(loaded.RouteFor(ClientType.Claude)!)!.Id);
        Assert.Equal("****ture", loaded.CurrentKeyOf(loaded.RouteFor(ClientType.Claude)!)!.MaskedKey);

        // Claude 只写角色与 1M，Codex 只写上下文窗口与压缩阈值。
        var saved = Canonical(config);
        var providers = saved.GetProperty("providers");
        var codexModels = providers[0].GetProperty("models").EnumerateObject().Select(property => property.Name);
        Assert.Equal(new[] { "model", "context_window", "auto_compact_token_limit" }, codexModels);
        var claudeModels = providers[1].GetProperty("models").EnumerateObject().Select(property => property.Name);
        Assert.Equal(new[] { "model", "context_1m", "opus", "sonnet", "haiku", "fable" }, claudeModels);
        Assert.Equal("x_api_key", providers[1].GetProperty("auth_mode").GetString());
        Assert.Equal("openai_billing", providers[1].GetProperty("balance_query").GetProperty("detected").GetString());
        Assert.Equal(JsonValueKind.Null, providers[1].GetProperty("keys")[0].GetProperty("model_override").ValueKind);
        Assert.True(saved.GetProperty("client_takeover").GetProperty("claude").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void ProvidersAreOrderedBySortIndexWithinTheirClient()
    {
        var (config, _) = Parse($$"""
            {
              "schema_version":{{ConfigDefaults.CurrentSchemaVersion}},
              "providers":[
                {"id":"c2","client_type":"codex","name":"second","base_url":"https://second.example","sort_index":1},
                {"id":"l1","client_type":"claude","name":"claude","base_url":"https://claude.example","sort_index":0},
                {"id":"c1","client_type":"codex","name":"first","base_url":"https://first.example","sort_index":0}
              ]
            }
            """);
        Assert.Equal(new[] { "first", "second" }, config.ProvidersFor(ClientType.Codex).Select(provider => provider.Name));
        var saved = Canonical(config).GetProperty("providers");
        Assert.Equal(
            new[] { ("claude", 0), ("first", 0), ("second", 1) },
            saved.EnumerateArray().Select(provider => (provider.GetProperty("name").GetString(), provider.GetProperty("sort_index").GetInt32())));
    }

    [Fact]
    public void Schema7RejectsInvalidProviderFields()
    {
        foreach (var (provider, message) in new[]
                 {
                     ("""{"client_type":"codex","name":"a","base_url":"https://a.example"}""", "缺少 id"),
                     ("""{"id":"p","client_type":"codex","name":"a","base_url":"https://a.example","auth_mode":"basic"}""", "auth_mode"),
                     ("""{"id":"p","client_type":"codex","name":"a","base_url":"https://a.example","balance_query":{"mode":"magic"}}""", "balance_query.mode"),
                     ("""{"id":"p","client_type":"codex","name":"a","base_url":"https://a.example","keys":[{"id":"k","name":"n"}]}""", "api_key"),
                     ("""{"id":"p","client_type":"codex","name":"a","base_url":"https://a.example","models":{"context_window":-1}}""", "context_window"),
                 })
        {
            var json = $$"""{"schema_version":{{ConfigDefaults.CurrentSchemaVersion}},"providers":[{{provider}}]}""";
            Assert.Contains(message, Assert.Throws<ConfigException>(() => Parse(json)).Message);
        }

        // 记住的余额接口认不出来时当作尚未识别，不报错。
        var (config, _) = Parse($$$"""
            {"schema_version":{{{ConfigDefaults.CurrentSchemaVersion}}},"providers":[
              {"id":"p","client_type":"codex","name":"a","base_url":"https://a.example","balance_query":{"mode":"auto","detected":"future"}}]}
            """);
        Assert.Null(config.Providers[0].BalanceQuery.Detected);
    }

    [Fact]
    public void CanonicalJsonUsesFixedKeyOrder()
    {
        using var document = ProxyConfigJson.ToCanonicalDocument(TestConfigs.BuiltinWithProviders());
        var topLevel = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(new[] { "schema_version", "selected_route_id", "providers", "routes", "client_takeover" }, topLevel);
        Assert.Equal(7, document.RootElement.GetProperty("schema_version").GetInt32());
        var providerKeys = document.RootElement.GetProperty("providers")[0].EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "id", "client_type", "name", "base_url", "auth_mode", "models", "website_url", "notes", "balance_query", "sort_index", "keys",
            },
            providerKeys);
        var routeKeys = document.RootElement.GetProperty("routes")[0].EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "id", "name", "client_type", "current_provider_id", "current_key_id", "local_token", "listen_port", "max_retries",
                "timeout_seconds", "generation_timeout_seconds", "total_timeout_seconds",
                "base_delay_seconds", "max_delay_seconds", "desired_running",
                "keepalive_enabled", "keepalive_idle_minutes", "keepalive_context_limit", "keepalive_reasoning_effort",
                "pass_through_compression",
            },
            routeKeys);
        var takeoverKeys = document.RootElement.GetProperty("client_takeover").EnumerateObject().Select(property => property.Name);
        Assert.Equal(new[] { "codex", "claude" }, takeoverKeys);
    }

    [Fact]
    public void ValidationMessagesMatchRustWording()
    {
        var duplicateName = TestConfigs.BuiltinWithProviders();
        duplicateName.Providers.Add(new ProviderEndpoint("ANYROUTER.TOP", "https://other.example") { Id = "dup", ClientType = ClientType.Codex });
        Assert.Equal("服务商名称重复：ANYROUTER.TOP", Assert.Throws<ConfigException>(() => duplicateName.Validate(false)).Message);

        var matchingAddress = TestConfigs.BuiltinWithProviders();
        matchingAddress.Providers.Add(new ProviderEndpoint("independent", matchingAddress.Providers[0].BaseUrl) { Id = "independent" });
        matchingAddress.Validate(false);

        var duplicatePort = ProxyConfig.Builtin();
        duplicatePort.Routes[1].ListenPort = 18080;
        Assert.Equal("本地端口重复：18080", Assert.Throws<ConfigException>(() => duplicatePort.Validate(false)).Message);

        var missingProvider = ProxyConfig.Builtin();
        missingProvider.Routes[1].CurrentProviderId = "nowhere";
        Assert.Equal("通道“Claude Code”的当前服务商不存在", Assert.Throws<ConfigException>(() => missingProvider.Validate(false)).Message);

        var badUrl = new ProviderEndpoint("x", "https://user:pw@example.com");
        Assert.Equal("服务商地址不能包含用户名或密码", Assert.Throws<ConfigException>(() => badUrl.Validate()).Message);

        var query = new ProviderEndpoint("x", "https://example.com/v1?x=1");
        Assert.Equal("服务商地址不能包含查询参数或片段", Assert.Throws<ConfigException>(() => query.Validate()).Message);

        var empty = new ProxyConfig();
        Assert.Equal("请先新增并选择服务商", Assert.Throws<ConfigException>(() => empty.Validate(true)).Message);
    }

    [Fact]
    public void Schema7ValidationEnforcesOneChannelPerClientAndMatchingProviders()
    {
        var twoCodex = ProxyConfig.Builtin();
        twoCodex.Routes[1].ClientType = ClientType.Codex;
        twoCodex.Routes[1].Name = "Codex 2";
        Assert.Equal("Codex 只能有一条通道", Assert.Throws<ConfigException>(() => twoCodex.Validate(false)).Message);

        var wrongClient = TestConfigs.BuiltinWithProviders();
        wrongClient.Routes[1].CurrentProviderId = "provider-codex";
        Assert.Equal("服务商“anyrouter.top”属于 Codex，不能用于 Claude Code",
            Assert.Throws<ConfigException>(() => wrongClient.Validate(false)).Message);

        var missingKey = TestConfigs.BuiltinWithProviders();
        missingKey.Routes[0].CurrentKeyId = "gone";
        Assert.Equal("通道“Codex”的当前 Key 不存在", Assert.Throws<ConfigException>(() => missingKey.Validate(false)).Message);

        var keyWithoutProvider = ProxyConfig.Builtin();
        keyWithoutProvider.Routes[0].CurrentKeyId = "k";
        Assert.Equal("通道“Codex”选了 Key 却没有选服务商", Assert.Throws<ConfigException>(() => keyWithoutProvider.Validate(false)).Message);

        var noId = ProxyConfig.Builtin();
        noId.Providers.Add(new ProviderEndpoint("x", "https://x.example"));
        Assert.Equal("服务商“x”缺少 ID", Assert.Throws<ConfigException>(() => noId.Validate(false)).Message);

        // 名称只在同一客户端内唯一。
        var sameNameOtherClient = TestConfigs.BuiltinWithProviders();
        sameNameOtherClient.Validate(false);

        foreach (var (key, message) in new[]
                 {
                     (new ProviderKey { Id = "", Name = "n", ApiKey = "sk" }, "有 Key 缺少 ID"),
                     (new ProviderKey { Id = "k", Name = "", ApiKey = "sk" }, "有 Key 未填写名称"),
                     (new ProviderKey { Id = "k", Name = "n", ApiKey = "" }, "的 Key“n”未填写密钥"),
                 })
        {
            var provider = new ProviderEndpoint("p", "https://p.example") { Id = "p", Keys = { key } };
            Assert.EndsWith(message, Assert.Throws<ConfigException>(() => provider.Validate()).Message);
        }

        var duplicateKeys = new ProviderEndpoint("p", "https://p.example")
        {
            Id = "p",
            Keys = { new ProviderKey { Id = "a", Name = "主号", ApiKey = "sk-1" }, new ProviderKey { Id = "b", Name = "主号", ApiKey = "sk-2" } },
        };
        Assert.Equal("服务商“p”的 Key 名称重复：主号", Assert.Throws<ConfigException>(() => duplicateKeys.Validate()).Message);
    }
}
