using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>
/// 对应 proxy.rs 与 request_logging.rs 中依赖保活探测/CLI 的用例，外加一个用 PowerShell 假 Codex CLI 的端到端用例。
/// 假 CLI 进程从测试进程继承环境，放在同一集合里避免与改环境变量的用例并行。
/// </summary>
[Collection("cli-environment")]
public class KeepAliveIntegrationTests
{
    private static ProxyConfig Config(double timeoutSeconds)
    {
        var config = Configs.Default();
        config.MaxRetries = 0;
        config.TimeoutSeconds = timeoutSeconds;
        config.TotalTimeoutSeconds = Math.Max(timeoutSeconds, 3.0);
        config.BaseDelaySeconds = 0.0;
        config.MaxDelaySeconds = 0.0;
        return config;
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public async Task HeadProbesDoNotCountAsRealRequestsOrInterruptPreparation()
    {
        await using var fixture = await LifecycleProxy.StartAsync(context => Upstream.Text(context, 401, string.Empty), Config(3.0));
        using var service = fixture.Proxy.KeepAlive.RegisterService(KeepAliveFlavor.Claude);
        Assert.True(fixture.Proxy.KeepAlive.RequestPreparation());
        using var probe = fixture.Proxy.KeepAlive.BeginDueProbe()!;
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Head, $"{fixture.Address}/api/hello");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(probe.Cancel.IsCancellationRequested);
        Assert.Equal(0UL, fixture.Proxy.KeepAlive.Snapshot().ActiveRequests);
        Assert.NotNull(probe.Complete(null, 10));
        probe.Dispose();
        Assert.IsType<PreparationResult.Ready>(fixture.Proxy.KeepAlive.TakePreparationResult());
    }

    [Fact]
    public async Task RealRequestInterruptsBodyMarkedKeepAliveWithoutWaiting()
    {
        var reached = new Notify();
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            if (context.Request.Path == "/pending")
            {
                reached.NotifyOne();
                await Upstream.Pending(context);
                return;
            }

            var body = await Upstream.ReadBody(context);
            await Upstream.Text(context, 200, body);
        }, Config(3.0));
        using var service = fixture.Proxy.KeepAlive.RegisterService(KeepAliveFlavor.Codex);
        Assert.True(fixture.Proxy.KeepAlive.RequestPreparation());
        using var probe = fixture.Proxy.KeepAlive.BeginDueProbe()!;
        var marked = new JsonObject
        {
            ["client_metadata"] = new JsonObject
            {
                ["x-codex-turn-metadata"] = new JsonObject { ["retry_proxy_keepalive"] = probe.SessionId }.ToJsonString(),
            },
        }.ToJsonString();
        using var backgroundClient = TestClient.Create(10);
        var background = TestClient.Send(backgroundClient, HttpMethod.Post, $"{fixture.Address}/pending", marked);
        using (var wait = new CancellationTokenSource(2000))
        {
            await reached.Notified().WaitAsync(wait.Token);
        }

        const string original = "{ \"client_metadata\": {\"x-codex-turn-metadata\":\"{\\\"retry_proxy_keepalive\\\":\\\"unknown\\\"}\"}, \"input\": \"real request\" }";
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", original).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original, await response.Content.ReadAsStringAsync());
        Assert.True(probe.Cancel.IsCancellationRequested);
        try
        {
            var interrupted = await background.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.NotEqual(HttpStatusCode.OK, interrupted.StatusCode);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException)
        {
        }

        probe.Interrupt("real request");
        probe.Dispose();
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().ActiveRequests == 0);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().TotalRequests);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().FailedRequests);
        Assert.Equal(1UL, fixture.Proxy.KeepAlive.Snapshot().Totals.Interrupted);
        Assert.Equal(0UL, fixture.Proxy.KeepAlive.Snapshot().Totals.Completed);
    }

    [Fact]
    public async Task PreparationLogsCliLaunchFailureAndStaysPendingUntilCancelled()
    {
        var directory = TempDirectory();
        foreach (var (path, client) in new[] { ("/v1/responses", "Codex"), ("/v1/messages", "Claude") })
        {
            var watchdog = KeepAliveWatchdog.WithCliCommand(false, TimeSpan.FromSeconds(1), new CliCommand(Path.Combine(directory, "missing-client.exe")));
            await using var fixture = await LifecycleProxy.StartAsync(context => Upstream.Text(context, 404, string.Empty), Config(1.0), proxy => proxy.WithKeepAliveWatchdog(watchdog));
            var template = new KeepAliveTemplate("POST", path, new HeaderList(), Encoding.UTF8.GetBytes("{\"model\":\"recent-model\"}"));
            watchdog.Remember(template);
            Assert.True(watchdog.RequestPreparation());
            await fixture.Proxy.SendKeepAliveProbeAsync(template);

            var snapshot = watchdog.Snapshot();
            Assert.True(snapshot.Preparing, client);
            Assert.False(snapshot.Probing);
            Assert.Equal(1UL, snapshot.PreparationAttempts);
            Assert.NotNull(snapshot.PreparationRetryAfter);
            Assert.NotNull(snapshot.PreparationLastError);
            Assert.Equal(1UL, snapshot.Totals.Failed);
            Assert.Equal(0, snapshot.Turns);
            Assert.Null(watchdog.TakePreparationResult());
            Assert.Equal(0UL, fixture.Metrics.Snapshot().TotalRequests);
            var logs = fixture.DrainLogs();
            Assert.True(
                logs.Exists(line => line.Contains(client) && line.Contains("响应未完成") && line.Contains("随机等待 1.500～2.500 秒后继续重试") && line.Contains("终止准备")),
                string.Join("\n", logs));

            Assert.True(watchdog.CancelPreparation());
            Assert.IsType<PreparationResult.Cancelled>(watchdog.TakePreparationResult());
            await fixture.Proxy.SendDueKeepAliveProbeAsync();
            snapshot = watchdog.Snapshot();
            Assert.False(snapshot.Preparing);
            Assert.Null(snapshot.PreparationRetryAfter);
            Assert.Equal(1UL, snapshot.PreparationAttempts);
            Assert.Equal(1UL, snapshot.Totals.Failed);
            Assert.Null(watchdog.TakePreparationResult());
            Assert.False(watchdog.CancelPreparation());
        }

        Directory.Delete(directory, recursive: true);
    }

    /// <summary>
    /// 假 Codex CLI（PowerShell 脚本）：走完整的 app-server 握手，回答时经本通道转发一次带保活标记的请求，
    /// 验证 .ps1 包装、Job Object、内部请求识别、标记剥离与「完整回复」日志。
    /// </summary>
    private const string FakeCodexCli = @"
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$ErrorActionPreference = 'Stop'
$toolConfig = @{ node_repl = @{ command = 'fixture-tool'; env = @{ API_KEY = 'fixture-private' } } }
$line = [Console]::In.ReadLine()
while ($null -ne $line) {
    $message = $line | ConvertFrom-Json
    $id = $message.id
    switch ($message.method) {
        'initialize' { [Console]::WriteLine((@{ id = $id; result = @{} } | ConvertTo-Json -Depth 10 -Compress)) }
        'initialized' { }
        'config/read' { [Console]::WriteLine((@{ id = $id; result = @{ config = @{ mcp_servers = $toolConfig } } } | ConvertTo-Json -Depth 10 -Compress)) }
        'thread/start' {
            $overrides = $message.params.config
            $valid = $overrides.mcp_servers.node_repl.enabled -eq $false -and $message.params.ephemeral -eq $true -and $message.params.approvalPolicy -eq 'never'
            if ($valid) {
                [Console]::WriteLine((@{ id = $id; result = @{ thread = @{ id = 'fake-thread' }; model = 'fake-cli-model' } } | ConvertTo-Json -Depth 10 -Compress))
            } else {
                [Console]::WriteLine((@{ id = $id; error = @{ code = -32600; message = 'failed to load bootstrap configuration: invalid transport in mcp_servers' } } | ConvertTo-Json -Depth 10 -Compress))
            }
        }
        'turn/start' {
            $marker = $message.params.responsesapiClientMetadata.retry_proxy_keepalive
            if (-not $marker) { throw 'Missing keepalive marker' }
            $metadata = @{ retry_proxy_keepalive = $marker; thread_id = 'fake-thread' } | ConvertTo-Json -Compress
            $body = @{
                model = 'fake-cli-model'; store = $false; stream = $true
                input = @(@{ role = 'user'; content = @(@{ type = 'input_text'; text = $message.params.input[0].text }) })
                client_metadata = @{ 'x-codex-turn-metadata' = $metadata; fixture_field = 'preserved' }
            } | ConvertTo-Json -Depth 10 -Compress
            $response = Invoke-WebRequest -UseBasicParsing $env:RETRY_PROXY_FAKE_PROXY_URL -Method Post -ContentType 'application/json; charset=utf-8' -Headers @{ Authorization = 'Bearer local-validation-token' } -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 8
            if (-not $response.Content.Contains('response.completed')) { throw 'Incomplete local response' }
            [Console]::WriteLine((@{ id = $id; result = @{ turn = @{ id = 'fake-turn'; status = 'inProgress'; items = @() } } } | ConvertTo-Json -Depth 10 -Compress))
            [Console]::WriteLine((@{ method = 'item/agentMessage/delta'; params = @{ threadId = 'fake-thread'; delta = 'Java' } } | ConvertTo-Json -Depth 10 -Compress))
            [Console]::WriteLine((@{ method = 'thread/tokenUsage/updated'; params = @{ threadId = 'fake-thread'; tokenUsage = @{ last = @{ inputTokens = 40; outputTokens = 12; cachedInputTokens = 0; reasoningOutputTokens = 0 } } } } | ConvertTo-Json -Depth 10 -Compress))
            [Console]::WriteLine((@{ method = 'turn/completed'; params = @{ threadId = 'fake-thread'; turn = @{ status = 'completed'; items = @(@{ type = 'agentMessage'; text = 'Java CLI 验证回答' }) } } } | ConvertTo-Json -Depth 10 -Compress))
        }
    }
    [Console]::Out.Flush()
    $line = [Console]::In.ReadLine()
}
";

    [Fact]
    public async Task FakeCodexCliCompletesAPreparationThroughTheChannel()
    {
        var directory = TempDirectory();
        var script = Path.Combine(directory, "codex.ps1");
        File.WriteAllText(script, FakeCodexCli, new UTF8Encoding(true));
        var command = new CliCommand("powershell.exe");
        command.Arguments.AddRange(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script });
        var watchdog = KeepAliveWatchdog.WithCliCommand(false, TimeSpan.FromSeconds(60), command);
        var upstreamRequests = new List<(string Body, bool HasMarkerHeader)>();
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            var body = await Upstream.ReadBody(context);
            lock (upstreamRequests)
            {
                upstreamRequests.Add((body, context.Request.Headers.ContainsKey("x-retry-keepalive")));
            }

            await Upstream.EventStream(context, "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"client-format-model\",\"status\":\"completed\",\"usage\":{\"input_tokens\":40,\"output_tokens\":12},\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"这是完整的 Java 验证回答。\"}]}]}}\n\n");
        }, Config(20.0), proxy => proxy.WithKeepAliveWatchdog(watchdog));
        command.Environment.Add(new KeyValuePair<string, string>("RETRY_PROXY_FAKE_PROXY_URL", $"{fixture.Address}/v1/responses"));
        using var service = watchdog.RegisterService(KeepAliveFlavor.Codex);
        Assert.True(watchdog.RequestPreparation());
        await fixture.Proxy.SendDueKeepAliveProbeAsync();

        var snapshot = watchdog.Snapshot();
        Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
        Assert.Equal(1, snapshot.Turns);
        Assert.Equal("fake-cli-model", snapshot.Model);
        Assert.Equal(52UL, snapshot.ContextTokens);
        Assert.Equal(new KeepAliveTotals(1, 0, 0), snapshot.Totals);
        Assert.False(snapshot.Preparing);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().TotalRequests);

        (string Body, bool HasMarkerHeader) forwarded;
        lock (upstreamRequests)
        {
            forwarded = Assert.Single(upstreamRequests);
        }

        Assert.False(forwarded.HasMarkerHeader);
        using var document = JsonDocument.Parse(forwarded.Body);
        var metadata = document.RootElement.GetProperty("client_metadata");
        Assert.Equal("preserved", metadata.GetProperty("fixture_field").GetString());
        Assert.Equal("{\"thread_id\":\"fake-thread\"}", metadata.GetProperty("x-codex-turn-metadata").GetString());
        var logs = await fixture.CompletedLogs();
        Assert.True(logs.Contains("后台准备 [会话 ") && logs.Contains("Codex CLI，沿用本机客户端配置，问题："), logs);
        Assert.DoesNotContain("自动保活 [会话 ", logs);
        Assert.True(logs.Contains("完整回复") && logs.Contains("当前会话 52/50000 token") && logs.Contains("回答：Java CLI 验证回答") && logs.Contains("自动保活已关闭"), logs);
        Assert.DoesNotContain("local-validation-token", logs);

        // 再问一轮复用同一会话（同一个 CLI 进程），轮次递增。
        Assert.True(watchdog.RequestPreparation());
        await fixture.Proxy.SendDueKeepAliveProbeAsync();
        Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
        Assert.Equal(2, watchdog.Snapshot().Turns);
        watchdog.Configure(true, TimeSpan.FromMinutes(5));
        watchdog.MakeDueForTest();
        await fixture.Proxy.SendDueKeepAliveProbeAsync();
        Assert.Equal(3, watchdog.Snapshot().Turns);
        Assert.Null(watchdog.TakePreparationResult());
        Assert.Contains("自动保活 [会话 ", await fixture.CompletedLogs());
        service.Dispose();
        Assert.Null(watchdog.Snapshot().SessionId);
        Directory.Delete(directory, recursive: true);
    }
}

[CollectionDefinition("cli-environment", DisableParallelization = true)]
public class CliEnvironmentCollection
{
}
