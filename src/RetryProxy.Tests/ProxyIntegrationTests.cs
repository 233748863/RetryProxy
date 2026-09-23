using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Service;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>对应 proxy_integration.rs 中未被 ignore 的测试：走 ProxyService 的真实启停。</summary>
public class ProxyIntegrationTests
{
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string TempLogDirectory() => Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));

    private static ProxyConfig ServiceConfig(string upstream, int proxyPort, long maxRetries)
    {
        var config = Configs.Default();
        config.UpstreamBaseUrl = upstream;
        config.ListenPort = proxyPort;
        config.MaxRetries = maxRetries;
        config.BaseDelaySeconds = 0.0;
        config.MaxDelaySeconds = 0.0;
        return config;
    }

    [Theory]
    [InlineData(ClientType.Codex, "Authorization", "Bearer sk-upstream")]
    [InlineData(ClientType.Claude, "x-api-key", "sk-upstream")]
    public async Task TemporaryServiceOverridesOnlyItsOwnUpstreamAuthentication(ClientType clientType, string expectedHeader, string expectedValue)
    {
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Authorization, string ApiKey, string Path)>();
        await using var upstream = await FakeUpstream.StartAsync(async context =>
        {
            seen.Enqueue((context.Request.Headers.Authorization.ToString(), context.Request.Headers["x-api-key"].ToString(), context.Request.Path));
            await Upstream.Text(context, 200, "ok");
        });
        var directory = TempLogDirectory();
        using var logger = ProxyLogger.Silent(directory);
        var normalPort = FreePort();
        var temporaryPort = FreePort();
        var normal = new ProxyService(logger, "normal");
        var temporary = new ProxyService(logger, "prepare").WithUpstreamApiKey("sk-upstream", "local-key");
        var normalConfig = ServiceConfig(upstream.BaseUrl, normalPort, 0);
        normalConfig.ClientType = clientType;
        var temporaryConfig = ServiceConfig(upstream.BaseUrl, temporaryPort, 0);
        temporaryConfig.ClientType = clientType;
        normal.Start(normalConfig, TimeSpan.FromSeconds(5));
        temporary.Start(temporaryConfig, TimeSpan.FromSeconds(5));
        try
        {
            using var client = TestClient.Create();
            using var probe = await TestClient.Send(client, HttpMethod.Head, $"http://127.0.0.1:{temporaryPort}/api/hello");
            Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
            Assert.Empty(seen);
            using var rejected = await TestClient.Send(client, HttpMethod.Get, $"http://127.0.0.1:{temporaryPort}/v1/models");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Empty(seen);
            using var prepared = await TestClient.Send(client, HttpMethod.Get, $"http://127.0.0.1:{temporaryPort}/v1/models",
                headers: new System.Collections.Generic.Dictionary<string, string> { ["authorization"] = "Bearer local-key", ["x-api-key"] = "local-only" });
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
            using var ordinary = await TestClient.Send(client, HttpMethod.Get, $"http://127.0.0.1:{normalPort}/v1/models",
                headers: new System.Collections.Generic.Dictionary<string, string> { ["authorization"] = "Bearer client-key" });
            Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
            var requests = seen.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal("/v1/models", requests[0].Path);
            Assert.Equal(expectedValue, expectedHeader == "Authorization" ? requests[0].Authorization : requests[0].ApiKey);
            Assert.Equal(string.Empty, expectedHeader == "Authorization" ? requests[0].ApiKey : requests[0].Authorization);
            Assert.Equal("Bearer client-key", requests[1].Authorization);
            Assert.DoesNotContain("sk-upstream", LogFiles.ReadAll(directory));
        }
        finally
        {
            temporary.Stop(TimeSpan.FromSeconds(5));
            normal.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ServiceForwardsAndRetries()
    {
        var attempts = 0;
        await using var upstream = await FakeUpstream.StartAsync(async context =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
            {
                await Upstream.Text(context, 500, "retry");
            }
            else
            {
                await Upstream.Text(context, 200, $"attempt-{attempt}");
            }
        });
        var logDirectory = TempLogDirectory();
        using var logger = ProxyLogger.Silent(logDirectory);
        var proxyPort = FreePort();
        var service = new ProxyService(logger, "integration");
        service.Start(ServiceConfig(upstream.BaseUrl, proxyPort, 1), TimeSpan.FromSeconds(5));
        Assert.Equal(ServiceState.Running, service.State);
        using var client = TestClient.Create();
        var response = await client.GetAsync($"http://127.0.0.1:{proxyPort}/hello");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attempt-2", await response.Content.ReadAsStringAsync());
        using (var health = await TestClient.Health(client, $"http://127.0.0.1:{proxyPort}"))
        {
            Assert.Equal(1, health.RootElement.GetProperty("metrics").GetProperty("retry_count").GetInt32());
        }

        Assert.Equal(2, attempts);
        service.Stop(TimeSpan.FromSeconds(5));
        var logText = LogFiles.ReadAll(logDirectory);
        Assert.True(logText.Contains("第 1/2 次 GET /hello -> 上游 HTTP 500"), logText);
        Assert.True(logText.Contains("第 2/2 次 GET /hello -> 上游 HTTP 200"), logText);
        Assert.True(service.State is ServiceState.Stopped or ServiceState.Error);
    }

    [Fact]
    public async Task StreamingKeepsRequestActiveUntilBodyFinishes()
    {
        var release = new Notify();
        await using var upstream = await FakeUpstream.StartAsync(async context =>
        {
            await Upstream.Begin(context, 200, null);
            await Upstream.Chunk(context, "first");
            await release.Notified();
            await Upstream.Chunk(context, "second");
        });
        using var logger = ProxyLogger.Silent(TempLogDirectory());
        var proxyPort = FreePort();
        var service = new ProxyService(logger, "stream");
        service.Start(ServiceConfig(upstream.BaseUrl, proxyPort, 0), TimeSpan.FromSeconds(5));
        using var client = TestClient.Create();
        var address = $"http://127.0.0.1:{proxyPort}";
        var response = await TestClient.Send(client, HttpMethod.Get, $"{address}/stream");
        var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal("first", Encoding.UTF8.GetString((await TestClient.NextChunk(stream))!));
        using (var health = await TestClient.Health(client, address))
        {
            Assert.Equal(1, health.RootElement.GetProperty("metrics").GetProperty("active_requests").GetInt32());
        }

        release.NotifyOne();
        Assert.Equal("second", Encoding.UTF8.GetString((await TestClient.NextChunk(stream))!));
        Assert.Null(await TestClient.NextChunk(stream));
        await Task.Delay(30);
        using (var health = await TestClient.Health(client, address))
        {
            Assert.Equal(0, health.RootElement.GetProperty("metrics").GetProperty("active_requests").GetInt32());
            Assert.Equal(1, health.RootElement.GetProperty("metrics").GetProperty("successful_requests").GetInt32());
        }

        service.Stop(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnavailableUpstreamReturns502AfterRetry()
    {
        var upstreamPort = FreePort();
        using var logger = ProxyLogger.Silent(TempLogDirectory());
        var proxyPort = FreePort();
        var service = new ProxyService(logger, "failure");
        service.Start(ServiceConfig($"http://127.0.0.1:{upstreamPort}", proxyPort, 1), TimeSpan.FromSeconds(5));
        // Windows 对回环端口的拒绝连接会重试约 2 秒，两次尝试超过默认的 4 秒客户端超时。
        using var client = TestClient.Create(15);
        var response = await client.GetAsync($"http://127.0.0.1:{proxyPort}/failure");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using (var health = await TestClient.Health(client, $"http://127.0.0.1:{proxyPort}"))
        {
            Assert.Equal(1, health.RootElement.GetProperty("metrics").GetProperty("retry_count").GetInt32());
            Assert.Equal(1, health.RootElement.GetProperty("metrics").GetProperty("failed_requests").GetInt32());
        }

        service.Stop(TimeSpan.FromSeconds(5));
    }

    /// <summary>停用通道是硬停：不等在处理中的请求收尾，直接丢掉。</summary>
    [Fact]
    public async Task StoppingDiscardsInFlightRequests()
    {
        await using var upstream = await FakeUpstream.StartAsync(async context =>
        {
            await Upstream.Begin(context, 200, null);
            await Upstream.Chunk(context, "first");
            await Upstream.Pending(context);
        });
        var logDirectory = TempLogDirectory();
        using var logger = ProxyLogger.Silent(logDirectory);
        var proxyPort = FreePort();
        var service = new ProxyService(logger, "discard");
        service.Start(ServiceConfig(upstream.BaseUrl, proxyPort, 0), TimeSpan.FromSeconds(5));
        using var client = TestClient.Create();
        var address = $"http://127.0.0.1:{proxyPort}";
        var response = await TestClient.Send(client, HttpMethod.Get, $"{address}/stream");
        var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal("first", Encoding.UTF8.GetString((await TestClient.NextChunk(stream))!));
        using (var health = await TestClient.Health(client, address))
        {
            Assert.Equal(1, health.RootElement.GetProperty("metrics").GetProperty("active_requests").GetInt32());
        }

        var started = DateTime.UtcNow;
        service.Stop(TimeSpan.FromSeconds(10));
        var elapsed = DateTime.UtcNow - started;
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"停用没有立刻返回，耗时 {elapsed}");
        Assert.Equal(ServiceState.Stopped, service.State);

        // 在途请求的下游连接被切断：第二块永远不会到，流以错误或结束收场。
        using (var timeout = new CancellationTokenSource(3000))
        {
            try
            {
                var buffer = new byte[1024];
                var read = await stream.ReadAsync(buffer, timeout.Token);
                Assert.True(read == 0, $"停用后仍收到数据：{Encoding.UTF8.GetString(buffer, 0, read)}");
            }
            catch (Exception error) when (error is IOException or HttpRequestException or HttpIOException)
            {
            }
        }

        // 端口已经释放，可以立刻重新绑定。
        var rebind = new TcpListener(IPAddress.Loopback, proxyPort);
        rebind.Start();
        rebind.Stop();

        var logText = LogFiles.ReadAll(logDirectory);
        Assert.True(logText.Contains("代理服务已停止，丢弃 1 个处理中的请求"), $"日志里没有丢弃提示：{logText}");
    }

    /// <summary>下游客户端不再读响应时，代理往它写数据会被 TCP 缓冲区堵住，这种连接只能靠硬停丢掉。</summary>
    [Fact]
    public async Task StoppingCutsOffADownstreamClientThatStoppedReading()
    {
        var block = new byte[64 * 1024];
        Array.Fill(block, (byte)'x');
        await using var upstream = await FakeUpstream.StartAsync(async context =>
        {
            await Upstream.Begin(context, 200, null);
            for (var index = 0; index < 512; index++)
            {
                try
                {
                    await Upstream.Chunk(context, block);
                }
                catch (Exception)
                {
                    return;
                }
            }
        });
        using var logger = ProxyLogger.Silent(TempLogDirectory());
        var proxyPort = FreePort();
        var service = new ProxyService(logger, "stuck-downstream");
        service.Start(ServiceConfig(upstream.BaseUrl, proxyPort, 0), TimeSpan.FromSeconds(5));

        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, proxyPort);
        var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /big HTTP/1.1\r\nHost: 127.0.0.1:{proxyPort}\r\nConnection: close\r\n\r\n"));
        await stream.FlushAsync();
        socket.ReceiveTimeout = 5000;
        var head = new byte[1024];
        Assert.True(await stream.ReadAsync(head) > 0, "没有收到响应头");

        using var client = TestClient.Create();
        async Task<int> Active()
        {
            using var health = await TestClient.Health(client, $"http://127.0.0.1:{proxyPort}");
            return health.RootElement.GetProperty("metrics").GetProperty("active_requests").GetInt32();
        }

        var started = DateTime.UtcNow;
        while (await Active() == 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, await Active());
        await Task.Delay(400);

        started = DateTime.UtcNow;
        service.Stop(TimeSpan.FromSeconds(10));
        var elapsed = DateTime.UtcNow - started;
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"停用没有立刻返回，耗时 {elapsed}");
        Assert.Equal(ServiceState.Stopped, service.State);
    }

    [Fact]
    public void StateChangesAndChildComponentsShareOneUiNotifier()
    {
        using var logger = ProxyLogger.Silent(TempLogDirectory());
        var service = new ProxyService(logger, "alpha");
        var count = 0;
        service.SetUiNotifier(() => Interlocked.Increment(ref count));
        service.ForceStateForTest(ServiceState.Running);
        Assert.Equal(ServiceState.Running, service.State);
        Assert.Equal(1, count);
        service.SetStateIfNotErrorForTest(ServiceState.Stopped);
        Assert.Equal(2, count);
        service.SetErrorForTest("启动失败");
        Assert.Equal(ServiceState.Error, service.State);
        Assert.Equal(3, count);
        service.SetStateIfNotErrorForTest(ServiceState.Running);
        Assert.Equal(ServiceState.Error, service.State);
        var beforeMetrics = count;
        service.Metrics.RequestStarted("req-1", "POST", "/v1/responses");
        Assert.True(count > beforeMetrics);
        var beforeKeepAlive = count;
        service.KeepAlive.Configure(true, TimeSpan.FromSeconds(60));
        Assert.True(count > beforeKeepAlive);
    }
}
