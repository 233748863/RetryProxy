using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Proxy;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Tests.Support;
using Xunit;
using Pipeline = RetryProxy.Core.Proxy.RetryProxy;

namespace RetryProxy.Tests;

public sealed class NetworkProxyTests
{
    private static NetworkProxySettings Custom(string url, string user = "", string password = "") =>
        new NetworkProxySettings { Mode = NetworkProxyMode.Custom, Address = url, Username = user, Password = password }.Normalize();

    [Theory]
    [InlineData(NetworkProxyMode.Inherit, NetworkProxyMode.Inherit, NetworkProxyMode.Inherit)]
    [InlineData(NetworkProxyMode.Custom, NetworkProxyMode.Inherit, NetworkProxyMode.Custom)]
    [InlineData(NetworkProxyMode.Custom, NetworkProxyMode.Direct, NetworkProxyMode.Direct)]
    [InlineData(NetworkProxyMode.Direct, NetworkProxyMode.Inherit, NetworkProxyMode.Direct)]
    [InlineData(NetworkProxyMode.Direct, NetworkProxyMode.Custom, NetworkProxyMode.Custom)]
    public void KeySettingsOverrideProviderAndInheritanceIsCopied(NetworkProxyMode providerMode, NetworkProxyMode keyMode, NetworkProxyMode expected)
    {
        var provider = Custom("http://provider-proxy.test:80"); provider.Mode = providerMode;
        var key = Custom("http://key-proxy.test:80"); key.Mode = keyMode;
        var effective = NetworkProxySettings.Effective(provider, key);
        Assert.Equal(expected, effective.Mode);
        if (expected == NetworkProxyMode.Custom) Assert.Equal(keyMode == NetworkProxyMode.Custom ? key.Address : provider.Address, effective.Address);
        provider.Address = key.Address = "changed";
        Assert.DoesNotContain("changed", effective.Address);
    }

    [Fact]
    public void OnlyInheritanceReadsSystemRulesAndCustomAlsoAppliesToLoopback()
    {
        var fallback = new RecordingResolver(new Uri("http://system-proxy.test:7897"));
        var target = new Uri("http://127.0.0.1:8000");
        Assert.Null(new NetworkProxyResolver(new() { Mode = NetworkProxyMode.Direct }, fallback).Resolve(target));
        Assert.Equal(0, fallback.Calls);
        var custom = new NetworkProxyResolver(Custom("127.0.0.1:7898"), fallback).Resolve(target)!;
        Assert.False(custom.FromSystem);
        Assert.Equal(7898, custom.ProxyUrl.Port);
        Assert.Equal(0, fallback.Calls);
        Assert.True(new NetworkProxyResolver(new(), fallback).Resolve(target)!.FromSystem);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public void ProxyCredentialsAreEscapedAndPassedSeparatelyToDotNet()
    {
        var settings = Custom("socks5h://[::1]:1080", "name@realm", "p@ss:%/#?");
        settings.Validate();
        var uri = settings.ProxyUri();
        var proxy = NetworkProxyResolver.WebProxyFor(uri);
        Assert.Equal("socks5", proxy.Address!.Scheme);
        Assert.Empty(proxy.Address.UserInfo);
        Assert.Equal(1080, proxy.Address.Port);
        var credentials = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal(settings.Username, credentials.UserName);
        Assert.Equal(settings.Password, credentials.Password);
        Assert.DoesNotContain("name", settings.ToString());
        Assert.DoesNotContain("p@ss", settings.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://proxy.example:80")]
    [InlineData("socks4://proxy.example:1080")]
    [InlineData("socks5://proxy.example")]
    [InlineData("http://proxy.example:0")]
    [InlineData("http://proxy.example:70000")]
    [InlineData("http://proxy.example/path")]
    [InlineData("http://proxy.example?query=value")]
    [InlineData("http://proxy.example#fragment")]
    [InlineData("http://user:private-password@proxy.example")]
    public void BadProxyAddressesAreRejectedWithoutDisclosingInput(string address)
    {
        var error = Assert.Throws<ConfigException>(() => Custom(address).Validate());
        Assert.DoesNotContain("private-password", error.Message);
        Assert.DoesNotContain("proxy.example", error.Message);
    }

    [Fact]
    public void CredentialLimitsUseUtf8AndInactiveChoicesDoNotSaveOldPasswords()
    {
        Assert.Throws<ConfigException>(() => Custom("http://proxy.test", "user", new string('中', 86)).Validate());
        Assert.Throws<ConfigException>(() => Custom("http://proxy.test", "", "password").Validate());
        Assert.Throws<ConfigException>(() => Custom("http://proxy.test", "user:name", "password").Validate());
        var settings = Custom("http://proxy.test", "user", "password"); settings.Mode = NetworkProxyMode.Direct;
        var normalized = settings.Normalize();
        Assert.Equal(NetworkProxyMode.Direct, normalized.Mode);
        Assert.Empty(normalized.Address); Assert.Empty(normalized.Username); Assert.Empty(normalized.Password);
    }

    private static ProxyConfig Config(string upstream)
    {
        var config = ProxyConfig.Builtin();
        config.Providers.Add(new ProviderEndpoint
        {
            Id = "p", Name = "provider", BaseUrl = upstream, ClientType = ClientType.Codex, Models = new() { Model = "test" },
            Keys = new() { new() { Id = "k", Name = "key", ApiKey = "sk-fixture" } },
        });
        var route = config.RouteFor(ClientType.Codex)!;
        route.CurrentProviderId = "p"; route.CurrentKeyId = "k";
        route.MaxRetries = 0; route.KeepaliveEnabled = false;
        return config.Normalize();
    }

    [Fact]
    public void ConfigAndPreparationRoundTripAndCopyBothLevels()
    {
        var config = Config("http://upstream.test");
        config.Providers[0].NetworkProxy = Custom("http://provider-proxy.test:8080", "user", "password");
        config.Providers[0].Keys[0].NetworkProxy = Custom("socks5://key-proxy.test:1080", "key-user", "key-password");
        var saved = ProxyConfigJson.Parse(ProxyConfigJson.ToCanonicalJson(config)).Config;
        Assert.Equal(config, saved);
        var snapshot = config.SnapshotFor(config.RouteFor(ClientType.Codex)!.Id);
        var target = PreparationCatalog.Resolve(config, ClientType.Codex, null);
        var copy = config.Clone();
        config.Providers[0].Keys[0].NetworkProxy.Password = "changed";
        Assert.NotEqual(copy, config);
        Assert.Equal("key-password", snapshot.NetworkProxy.Password);
        Assert.Equal("key-password", snapshot.WithKey("x", "y", Core.Cli.ClaudeAuthMode.Bearer).NetworkProxy.Password);
        Assert.Equal("key-password", target.NetworkProxy.Password);
        var json = JsonNode.Parse(ProxyConfigJson.ToCanonicalJson(config))!;
        json["providers"]![0]!.AsObject().Remove("network_proxy");
        json["providers"]![0]!["keys"]![0]!.AsObject().Remove("network_proxy");
        var legacy = ProxyConfigJson.Parse(json.ToJsonString()).Config;
        Assert.Equal(NetworkProxyMode.Inherit, legacy.Providers[0].NetworkProxy.Mode);
        Assert.Equal(NetworkProxyMode.Inherit, legacy.Providers[0].Keys[0].NetworkProxy.Mode);
    }

    [Fact]
    public void FailedSaveDoesNotChangeEitherNetworkSetting()
    {
        using var logger = ProxyLogger.Silent(Path.Combine(Path.GetTempPath(), "network-save-" + Guid.NewGuid().ToString("N")));
        using var app = new ProxyWorkspace(logger, Config("http://upstream.test"), _ => throw new IOException("fixture"));
        var draft = app.Config.Providers[0].Clone();
        draft.NetworkProxy = Custom("http://provider-proxy.test:80");
        draft.Keys[0].NetworkProxy = new() { Mode = NetworkProxyMode.Direct };
        Assert.NotNull(app.SaveProvider(draft, false));
        Assert.Equal(NetworkProxyMode.Inherit, app.Config.Providers[0].NetworkProxy.Mode);
        Assert.Equal(NetworkProxyMode.Inherit, app.Config.Providers[0].Keys[0].NetworkProxy.Mode);
    }

    [Fact]
    public void PoolSeparatesCredentialsAndProtocolsAndKeepsLeasedResponsesAlive()
    {
        var handlers = new List<TrackingHandler>();
        var pool = new UpstreamClientPool((_, _) => { var handler = new TrackingHandler(); handlers.Add(handler); return new HttpClient(handler); });
        var a = pool.Rent(new Uri("http://user:first@proxy.test:80"), true);
        using (var same = pool.Rent(new Uri("http://user:first@proxy.test:80"), true)) Assert.Same(a.Client, same.Client);
        using (var other = pool.Rent(new Uri("http://user:second@proxy.test:80"), true)) Assert.NotSame(a.Client, other.Client);
        using (var plain = pool.Rent(new Uri("http://user:first@proxy.test:80"), false)) Assert.NotSame(a.Client, plain.Client);
        for (var i = 0; i < 40; i++) using (pool.Rent(new Uri($"http://proxy.test:{10000 + i}"), false)) { }
        Assert.Equal(UpstreamClientPool.MaxIdleClients + 1, handlers.Count(handler => !handler.Disposed));
        Assert.False(handlers[0].Disposed);
        pool.Dispose();
        Assert.False(handlers[0].Disposed);
        Assert.Throws<ObjectDisposedException>(() => pool.Rent(null, false));
        a.Dispose(); a.Dispose(); pool.Dispose();
        Assert.All(handlers, handler => Assert.True(handler.Disposed));
    }

    [Fact]
    public void ResponseSourceOwnsItsConnectionLease()
    {
        var releases = 0;
        var lease = new UpstreamClientPool.Lease(new HttpClient(), () => releases++);
        var source = new HttpChunkSource(new HttpResponseMessage(), new MemoryStream(), TimeSpan.FromSeconds(5), null, lease);
        Assert.Equal(0, releases);
        source.Dispose(); source.Dispose();
        Assert.Equal(1, releases);
        lease.Client.Dispose();
    }

    [Fact]
    public async Task ManagedKeysUseSystemProviderCustomAndDirectConnectionsIndependently()
    {
        var peers = new List<string>();
        await using var direct = await FakeUpstream.StartAsync(context => { peers.Add("direct"); return Upstream.Json(context, 200, "{}"); });
        await using var system = await FakeUpstream.StartAsync(context => { peers.Add("system"); return Upstream.Json(context, 200, "{}"); });
        await using var providerProxy = await FakeUpstream.StartAsync(context => { peers.Add("provider"); return Upstream.Json(context, 200, "{}"); });
        await using var keyProxy = await FakeUpstream.StartAsync(context => { peers.Add("key"); return Upstream.Json(context, 200, "{}"); });
        var config = Config(direct.BaseUrl); var route = config.RouteFor(ClientType.Codex)!;
        using var logger = ProxyLogger.Silent(Path.Combine(Path.GetTempPath(), "network-real-" + Guid.NewGuid().ToString("N")));
        using var metrics = new ProxyMetrics();
        using var pipeline = new Pipeline(config.RuntimeConfigFor(route.Id), logger, metrics, CancellationToken.None, new RecordingResolver(new Uri(system.BaseUrl)));
        await using var host = await ProxyHost.StartAsync(pipeline, 0);
        using var client = TestClient.Create();
        async Task Send()
        {
            pipeline.UpdateSnapshot(config.SnapshotFor(route.Id));
            using var response = await TestClient.Send(client, HttpMethod.Post, $"http://127.0.0.1:{host.Port}/v1/responses", "{\"model\":\"test\"}");
            Assert.True(response.IsSuccessStatusCode); await response.Content.ReadAsStringAsync();
        }
        await Send();
        config.Providers[0].NetworkProxy = Custom(providerProxy.BaseUrl); await Send();
        config.Providers[0].Keys[0].NetworkProxy = Custom(keyProxy.BaseUrl); await Send();
        config.Providers[0].Keys[0].NetworkProxy = new() { Mode = NetworkProxyMode.Direct }; await Send();
        config.Providers[0].Keys[0].NetworkProxy = new(); await Send();
        Assert.Equal(new[] { "system", "provider", "key", "direct", "provider" }, peers);
    }

    [Fact]
    public async Task ModelQueriesHonorKeyOverridesIncludingDirect()
    {
        await using var direct = await FakeUpstream.StartAsync(context => Upstream.Json(context, 200, "{\"data\":[{\"id\":\"direct\"}]}"));
        await using var proxy = await FakeUpstream.StartAsync(context => Upstream.Json(context, 200, "{\"data\":[{\"id\":\"proxy\"}]}"));
        var provider = new ProviderEndpoint("models", direct.BaseUrl) { NetworkProxy = Custom(proxy.BaseUrl) };
        Assert.Equal(new[] { "proxy" }, await ProviderModelFetcher.FetchAsync(provider, "sk-test", ClientType.Codex, CancellationToken.None));
        Assert.Equal(new[] { "direct" }, await ProviderModelFetcher.FetchAsync(provider, "sk-test", ClientType.Codex, CancellationToken.None,
            keyNetworkProxy: new() { Mode = NetworkProxyMode.Direct }));
        provider.NetworkProxy = new() { Mode = NetworkProxyMode.Direct };
        Assert.Equal(new[] { "proxy" }, await ProviderModelFetcher.FetchAsync(provider, "sk-test", ClientType.Codex, CancellationToken.None, keyNetworkProxy: Custom(proxy.BaseUrl)));
    }

    [Fact]
    public async Task DeliveredStreamKeepsItsProxyWhileTheNextKeyUsesAnother()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxyA = await FakeUpstream.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"first\"}\n\n"));
            await context.Response.Body.FlushAsync();
            await finish.Task.WaitAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"last\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n"));
        });
        await using var proxyB = await FakeUpstream.StartAsync(context => Upstream.Json(context, 200, "{\"peer\":\"b\"}"));
        var config = Config("http://provider.invalid"); var route = config.RouteFor(ClientType.Codex)!;
        using var logger = ProxyLogger.Silent(Path.Combine(Path.GetTempPath(), "network-stream-" + Guid.NewGuid().ToString("N")));
        using var metrics = new ProxyMetrics();
        using var pipeline = new Pipeline(config.RuntimeConfigFor(route.Id), logger, metrics, CancellationToken.None);
        config.Providers[0].NetworkProxy = Custom(proxyA.BaseUrl);
        pipeline.UpdateSnapshot(config.SnapshotFor(route.Id));
        await using var host = await ProxyHost.StartAsync(pipeline, 0);
        using var http = TestClient.Create(10);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{host.Port}/v1/responses")
        { Content = new StringContent("{\"model\":\"test\",\"stream\":true}", System.Text.Encoding.UTF8, "application/json") };
        using var streamed = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await streamed.Content.ReadAsStreamAsync());
        Assert.Contains("first", await reader.ReadLineAsync());
        config.Providers[0].Keys.Add(new() { Id = "b", Name = "b", ApiKey = "sk-b", NetworkProxy = Custom(proxyB.BaseUrl) });
        route.CurrentKeyId = "b";
        pipeline.UpdateSnapshot(config.SnapshotFor(route.Id));
        using var next = await TestClient.Send(http, HttpMethod.Post, $"http://127.0.0.1:{host.Port}/v1/responses", "{}");
        Assert.Contains("\"b\"", await next.Content.ReadAsStringAsync());
        finish.TrySetResult();
        Assert.Contains("last", await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PreparationAndItsModelQueryShareTheResolvedKeyConnection()
    {
        await using var proxy = await FakeUpstream.StartAsync(context =>
        {
            Assert.Equal("Bearer sk-fixture", context.Request.Headers.Authorization.ToString());
            return Upstream.Json(context, 200, context.Request.Method == "GET" ? "{\"data\":[{\"id\":\"prepared\"}]}" : "{\"peer\":\"prepared\"}");
        });
        var config = Config("http://provider.invalid");
        config.Providers[0].NetworkProxy = new() { Mode = NetworkProxyMode.Direct };
        config.Providers[0].Keys[0].NetworkProxy = Custom(proxy.BaseUrl);
        using var logger = ProxyLogger.Silent(Path.Combine(Path.GetTempPath(), "network-prep-" + Guid.NewGuid().ToString("N")));
        var preparations = new PreparationWorkspace(logger, targetResolver: (client, key) => PreparationCatalog.Resolve(config, client, key));
        var query = preparations.ResolveModelQuery(new() { ClientType = ClientType.Codex, Mode = PrepareMode.LocalProvider });
        var provider = new ProviderEndpoint("query", query.Credential.BaseUrl) { NetworkProxy = query.NetworkProxy };
        Assert.Equal(new[] { "prepared" }, await ProviderModelFetcher.FetchAsync(provider, query.Credential.ApiKey, ClientType.Codex, CancellationToken.None));
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var runtime = config.RuntimeConfigFor(config.RouteFor(ClientType.Codex)!.Id); runtime.ListenPort = port;
        var service = new ProxyService(logger, "network-preparation").AsPreparationProxy(query.Credential.ApiKey, "local-access");
        try
        {
            service.Start(runtime, TimeSpan.FromSeconds(5), ChannelSnapshot.FromRuntime(runtime, networkProxy: query.NetworkProxy));
            using var client = TestClient.Create(10);
            using var response = await TestClient.Send(client, HttpMethod.Post, runtime.LocalUrl + "/v1/responses", "{}",
                headers: new Dictionary<string, string> { ["Authorization"] = "Bearer local-access" });
            Assert.Contains("prepared", await response.Content.ReadAsStringAsync());
            Assert.True(response.IsSuccessStatusCode);
        }
        finally { service.Stop(TimeSpan.FromSeconds(5)); preparations.Shutdown(); }
    }

    private sealed class RecordingResolver(Uri proxy) : IProxyResolver
    {
        public int Calls;
        public ProxyDecision Resolve(Uri target) { Calls++; return new(proxy, true); }
    }
    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage());
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
