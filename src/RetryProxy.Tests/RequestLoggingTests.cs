using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Workspace;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>对应 request_logging.rs 中未被 ignore 的测试。</summary>
public class RequestLoggingTests
{
    private static ProxyConfig LoggingConfig(double timeoutSeconds, long maxRetries)
    {
        var config = Configs.Default();
        config.MaxRetries = maxRetries;
        config.BaseDelaySeconds = 0.0;
        config.MaxDelaySeconds = 0.0;
        config.TimeoutSeconds = timeoutSeconds;
        return config;
    }

    private static async Task<List<string>> CompletedLogs(LifecycleProxy proxy)
    {
        await TestClock.WaitUntil(() => proxy.Metrics.Snapshot().ActiveRequests == 0, 2.0, "请求结束后活动计数未归零");
        return proxy.DrainLogs();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentPreparationRetriesFailedResponsesUntilDeadline(bool streamError)
    {
        var watchdog = new KeepAliveWatchdog(false, TimeSpan.FromMinutes(5));
        using var registration = watchdog.RegisterService(KeepAliveFlavor.Codex);
        Assert.True(watchdog.RequestPreparation());
        var config = LoggingConfig(5.0, 0);
        config.TotalTimeoutSeconds = 0.5;
        config.BaseDelaySeconds = 0.05;
        config.MaxDelaySeconds = 0.05;
        await using var fixture = await LifecycleProxy.StartAsync(
            context => streamError
                ? Upstream.Text(context, 500, "data: {\"type\":\"error\",\"error\":{\"code\":\"get_channel_failed\"}}\n\n", "text/event-stream")
                : Upstream.Text(context, 500, "upstream unavailable", "text/plain"),
            config,
            proxy => proxy.WithRouteLogger(proxy.Logger.Base.Preparation("准备 1 · Codex"))
                .WithKeepAliveWatchdog(watchdog)
                .AsPreparationProxy("sk-upstream", "sk-local"));
        var marker = Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        InternalSessions.Register(marker, cancellation);
        try
        {
            using var client = TestClient.Create();
            using var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses",
                "{\"model\":\"gpt-test\",\"input\":\"hello\"}", headers: new Dictionary<string, string>
                {
                    ["authorization"] = "Bearer sk-local",
                    ["x-retry-keepalive"] = marker,
                });
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.NotNull(await TestClient.TryReadAll(response));
            var lines = await CompletedLogs(fixture);
            Assert.Contains(lines, line => line.Contains("WARNING [一键准备][准备 1 · Codex][准备][请求 ")
                && line.Contains("POST /v1/responses -> 上游 HTTP 500") && line.Contains("未交给客户端"));
            Assert.Contains(lines, line => line.Contains("HTTP 504"));
            if (streamError)
            {
                Assert.Contains(lines, line => line.Contains("get_channel_failed"));
            }
        }
        finally
        {
            InternalSessions.Unregister(marker);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationRequestKeepsItsActivityWhenTheTaskChangesBeforeTheResponse(bool markerInBody)
    {
        var watchdog = new KeepAliveWatchdog(false, TimeSpan.FromMinutes(5));
        using var registration = watchdog.RegisterService(KeepAliveFlavor.Codex);
        Assert.True(watchdog.RequestPreparation());
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = LoggingConfig(5.0, 0);
        config.TotalTimeoutSeconds = 0.5;
        config.BaseDelaySeconds = 0.05;
        config.MaxDelaySeconds = 0.05;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Upstream.Text(context, 500, "upstream unavailable");
        }, config, proxy => proxy
            .WithRouteLogger(proxy.Logger.Base.Preparation("准备 2 · Codex"))
            .WithKeepAliveWatchdog(watchdog)
            .AsPreparationProxy("sk-upstream", "sk-local"));
        var marker = Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        InternalSessions.Register(marker, cancellation);
        try
        {
            using var client = TestClient.Create();
            var headers = new Dictionary<string, string> { ["authorization"] = "Bearer sk-local" };
            var body = "{\"model\":\"gpt-test\",\"input\":\"hello\"}";
            if (markerInBody)
            {
                body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    model = "gpt-test",
                    input = "hello",
                    client_metadata = new Dictionary<string, string>
                    {
                        ["x-codex-turn-metadata"] = System.Text.Json.JsonSerializer.Serialize(new { retry_proxy_keepalive = marker }),
                    },
                });
            }
            else
            {
                headers["x-retry-keepalive"] = marker;
            }
            var pending = TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", body, headers: headers);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            watchdog.CancelPreparation();
            watchdog.Configure(true, TimeSpan.FromMinutes(5));
            release.TrySetResult();
            using var response = await pending;
            await response.Content.ReadAsStringAsync();
            var first = await CompletedLogs(fixture);
            Assert.Contains(first, line => line.Contains("[一键准备][准备 2 · Codex][准备][请求 "));
            Assert.DoesNotContain(first, line => line.Contains("[独立保活]"));

            using var next = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", body, headers: headers);
            await next.Content.ReadAsStringAsync();
            var second = Assert.Single(await CompletedLogs(fixture));
            Assert.Contains("[一键准备][准备 2 · Codex][独立保活][请求 ", second);
            Assert.Equal(0UL, fixture.Metrics.Snapshot().TotalRequests);
        }
        finally
        {
            release.TrySetResult();
            InternalSessions.Unregister(marker);
        }
    }

    [Fact]
    public async Task ConcurrentChannelRequestsAndKeepAliveHaveSeparateSources()
    {
        await using var fixture = await LifecycleProxy.StartAsync(
            context => Upstream.Text(context, 200, "{}"), LoggingConfig(5.0, 0),
            proxy => proxy.WithRouteLogger(proxy.Logger.Base.Route("alpha")));
        var marker = Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        InternalSessions.Register(marker, cancellation);
        try
        {
            using var client = TestClient.Create();
            var ordinary = TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{}");
            var background = TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{}",
                headers: new Dictionary<string, string> { ["x-retry-keepalive"] = marker });
            using var first = await ordinary;
            using var second = await background;
            await first.Content.ReadAsStringAsync();
            await second.Content.ReadAsStringAsync();
            var logs = await CompletedLogs(fixture);
            Assert.Equal(2, logs.Count);
            Assert.Single(logs, line => LogLine.Matches(line, LogLevelFilter.All, string.Empty, "alpha", LogSource.ChannelProxy));
            Assert.Single(logs, line => LogLine.Matches(line, LogLevelFilter.All, string.Empty, "alpha", LogSource.ChannelKeepAlive));
            Assert.All(logs, line => Assert.DoesNotContain("[保活-", line));
            Assert.Equal(1UL, fixture.Metrics.Snapshot().TotalRequests);
        }
        finally
        {
            InternalSessions.Unregister(marker);
        }
    }

    [Fact]
    public async Task NonStreamingCompletionLogsUsageWithoutChangingTraffic()
    {
        const string responseBody = "{\"model\":\"gpt-test\",\"output\":[{\"content\":[{\"text\":\"private-output\"}]}],\"usage\":{\"input_tokens\":4096,\"output_tokens\":32,\"input_tokens_details\":{\"cached_tokens\":512},\"output_tokens_details\":{\"reasoning_tokens\":8}}}";
        const string requestBody = "{\"model\":\"request-alias\",\"input\":\"private-prompt\",\"stream\":false}";
        string? seenTarget = null;
        string? seenAuthorization = null;
        string? seenEncoding = null;
        string? seenBody = null;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            seenTarget = context.Request.Path + context.Request.QueryString;
            seenAuthorization = context.Request.Headers.Authorization.ToString();
            seenEncoding = context.Request.Headers.AcceptEncoding.ToString();
            seenBody = await Upstream.ReadBody(context);
            await Upstream.Bytes(context, 200, Encoding.UTF8.GetBytes(responseBody), "application/json", new Dictionary<string, string> { ["x-request-id"] = "upstream-request" });
        }, LoggingConfig(5.0, 0));
        using var client = TestClient.Create();
        var response = await TestClient.Send(
            client,
            HttpMethod.Post,
            $"{fixture.Address}/v1/responses?key=private-query",
            requestBody,
            "application/json",
            new Dictionary<string, string> { ["authorization"] = "Bearer private-key", ["accept-encoding"] = "gzip, br" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream-request", string.Join(",", response.Headers.GetValues("x-request-id")));
        Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        Assert.Equal("/v1/responses?key=private-query", seenTarget);
        Assert.Equal("Bearer private-key", seenAuthorization);
        Assert.Equal("identity", seenEncoding);
        Assert.Equal(requestBody, seenBody);
        var logs = await CompletedLogs(fixture);
        Assert.True(logs.Count == 1, string.Join("\n", logs));
        var line = logs[0];
        Assert.Contains("] POST /v1/responses -> 上游 HTTP 200", line);
        Assert.DoesNotContain("第 1/", line);
        foreach (var expected in new[] { "HTTP 200", "模型 gpt-test", "输入 4096", "输出 32", "命中 512", "推理 8", "首字", "耗时" })
        {
            Assert.True(line.Contains(expected), $"缺少 {expected}: {line}");
        }

        foreach (var privateValue in new[] { "private-output", "private-prompt", "private-query", "private-key" })
        {
            Assert.False(line.Contains(privateValue), "日志泄露了请求内容");
        }

        Assert.Equal(1UL, fixture.Metrics.Snapshot().SuccessfulRequests);
    }

    [Fact]
    public async Task StreamingCompletionFinishesBeforeTransportEof()
    {
        const string payload =
            ": ping\r\n\r\n"
            + "event: response.created\r\ndata: {\"type\":\"response.created\",\"response\":{\"model\":\"gpt-stream\"}}\r\n\r\n"
            + "event: response.output_text.delta\r\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"完成\"}\r\n\r\n"
            + "event: response.completed\r\ndata: {\"type\":\"response.completed\",\"response\":{\"model\":\"gpt-stream\",\"usage\":{\"input_tokens\":120,\"output_tokens\":9}}}\r\n\r\n";
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            await Upstream.Begin(context, 200, "text/event-stream");
            var bytes = Encoding.UTF8.GetBytes(payload);
            for (var offset = 0; offset < bytes.Length; offset += 7)
            {
                await Upstream.Chunk(context, bytes[offset..Math.Min(offset + 7, bytes.Length)]);
            }

            await Upstream.Pending(context);
        }, LoggingConfig(5.0, 0));
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/v1/responses");
        var received = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(payload, received);
        var logs = await CompletedLogs(fixture);
        Assert.True(logs.Count == 1, string.Join("\n", logs));
        Assert.Contains("HTTP 200", logs[0]);
        Assert.Contains("输入 120", logs[0]);
        Assert.Contains("输出 9", logs[0]);
        Assert.DoesNotContain("第 1/", logs[0]);
        Assert.DoesNotContain("WARNING", logs[0]);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().SuccessfulRequests);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().FailedRequests);
    }

    [Fact]
    public async Task ActiveRequestsSuppressProbesAndDisconnectsDoNotReplaceTemplates()
    {
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref requests);
            await Upstream.Begin(context, 200, "text/event-stream");
            await Upstream.Chunk(context, "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-new\",\"usage\":{\"input_tokens\":44,\"output_tokens\":0}}}\n\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"partial\"}}\n\n");
            await Upstream.Pending(context);
        }, LoggingConfig(5.0, 0));
        fixture.Proxy.KeepAlive.Configure(true, TimeSpan.FromSeconds(1));
        var oldTemplate = new KeepAliveTemplate("POST", "/v1/messages", new HeaderList(), Encoding.UTF8.GetBytes("{\"model\":\"claude-old\",\"messages\":[]}"));
        fixture.Proxy.KeepAlive.Remember(oldTemplate);
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/messages", "{\"model\":\"claude-new\",\"stream\":true,\"messages\":[]}");
        var stream = await response.Content.ReadAsStreamAsync();
        Assert.NotNull(await TestClient.NextChunk(stream));
        Assert.Equal(1UL, fixture.Metrics.Snapshot().ActiveRequests);
        Assert.True(oldTemplate.Body.Span.SequenceEqual(fixture.Proxy.KeepAlive.Template()!.Body.Span));
        await Task.Delay(1100);
        await fixture.Proxy.SendKeepAliveProbeAsync(oldTemplate);
        Assert.Equal(1, requests);
        response.Dispose();
        var logs = await CompletedLogs(fixture);
        Assert.True(logs.Count == 1, string.Join("\n", logs));
        Assert.Contains("WARNING", logs[0]);
        Assert.Contains("客户端断开", logs[0]);
        Assert.Contains("输入 44", logs[0]);
        Assert.DoesNotContain("第 ", logs[0]);
        Assert.Contains("不再重试（已进入响应转发阶段）", logs[0]);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().SuccessfulRequests);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
        Assert.True(fixture.Proxy.KeepAlive.IdleFor() < TimeSpan.FromMilliseconds(200));
        Assert.True(oldTemplate.Body.Span.SequenceEqual(fixture.Proxy.KeepAlive.Template()!.Body.Span));
    }

    [Fact]
    public async Task StreamingTimeoutResetsWhileContentContinuesToArrive()
    {
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            await Upstream.Begin(context, 200, "text/event-stream");
            for (var index = 0; index < 6; index++)
            {
                await Upstream.Chunk(context, "data: {\"type\":\"response.output_text.delta\",\"delta\":\"x\"}\n\n");
                await Task.Delay(100);
            }

            await Upstream.Chunk(context, "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":6}}}\n\n");
        }, LoggingConfig(0.4, 0));
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"model\":\"gpt-stream\",\"input\":\"hi\",\"stream\":true}");
        var payload = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(6, payload.Split("response.output_text.delta").Length - 1);
        var logs = await CompletedLogs(fixture);
        Assert.True(logs.Count == 1, string.Join("\n", logs));
        Assert.DoesNotContain("WARNING", logs[0]);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().SuccessfulRequests);
    }

    [Fact]
    public async Task ProtocolErrorsAndPrematureEofRemainFailures()
    {
        foreach (var payload in new[]
                 {
                     "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"private-error\"}}}\n\n",
                     "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n",
                 })
        {
            await using var fixture = await LifecycleProxy.StartAsync(context => Upstream.EventStream(context, payload), LoggingConfig(5.0, 0));
            using var client = TestClient.Create();
            if (payload.Contains("response.failed"))
            {
                var response = await TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/v1/responses");
                Assert.Equal(payload, await response.Content.ReadAsStringAsync());
            }
            else
            {
                // 上游提前 EOF 时代理会中止连接；RST 偶尔先于响应头到达，Send 本身抛错同样视为“正文不完整”。
                HttpResponseMessage? response = null;
                try
                {
                    response = await TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/v1/responses");
                }
                catch (HttpRequestException)
                {
                }

                if (response is not null)
                {
                    Assert.Null(await TestClient.TryReadAll(response));
                }
            }

            var logs = await CompletedLogs(fixture);
            Assert.True(logs.Count == 1, string.Join("\n", logs));
            Assert.Contains("WARNING", logs[0]);
            Assert.Contains("HTTP 200", logs[0]);
            Assert.DoesNotContain("private-error", logs[0]);
            Assert.Equal(0UL, fixture.Metrics.Snapshot().SuccessfulRequests);
            Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
        }
    }

    [Fact]
    public async Task StructuredFailureDiagnosticsPreserveResponseBytesAndHideMessages()
    {
        foreach (var (contentType, payload) in new[]
                 {
                     ("text/event-stream", "event: error\ndata: {\"type\":\"error\",\"code\":\"server_error\",\"param\":\"input[0].content\",\"message\":\"private-error Bearer private-api-secret\"}\n\n"),
                     ("application/json", "{\"error\":{\"code\":\"server_error\",\"param\":\"input[0].content\",\"message\":\"private-error Bearer private-api-secret\"}}"),
                 })
        {
            await using var fixture = await LifecycleProxy.StartAsync(
                context => Upstream.Bytes(context, 200, Encoding.UTF8.GetBytes(payload), contentType, new Dictionary<string, string> { ["x-oneapi-request-id"] = "upstream-request-123" }),
                LoggingConfig(5.0, 0));
            using var client = TestClient.Create();
            var response = await TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/v1/responses");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("upstream-request-123", string.Join(",", response.Headers.GetValues("x-oneapi-request-id")));
            Assert.Equal(payload, await response.Content.ReadAsStringAsync());
            var logs = await CompletedLogs(fixture);
            Assert.True(logs.Count == 1, string.Join("\n", logs));
            var line = logs[0];
            Assert.Contains("WARNING", line);
            Assert.Contains("原因：上游服务内部错误", line);
            Assert.Contains("上游错误码 server_error", line);
            Assert.Contains("错误参数 input[0].content", line);
            Assert.Contains("上游请求 ID upstream-request-123", line);
            Assert.DoesNotContain("输入 未获取", line);
            Assert.DoesNotContain("private-error", line);
            Assert.DoesNotContain("private-api-secret", line);
            Assert.Equal(0UL, fixture.Metrics.Snapshot().SuccessfulRequests);
            Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
        }
    }

    [Fact]
    public async Task RateLimitAfterHttp200IsExplainedWithoutRetryingOrChangingTheBody()
    {
        const string payload =
            "data: {\"type\":\"response.created\",\"response\":{\"model\":\"gpt-6-astra\",\"usage\":null}}\n\n"
            + "data: {\"type\":\"error\",\"error\":{\"code\":\"rate_limit_exceeded\",\"type\":\"too_many_requests\",\"message\":\"private-message Bearer private-key\"}}\n\n";
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
        {
            Interlocked.Increment(ref requests);
            return Upstream.Bytes(context, 200, Encoding.UTF8.GetBytes(payload), "text/event-stream", new Dictionary<string, string> { ["x-oneapi-request-id"] = "rate-limit-request-123" });
        }, LoggingConfig(5.0, 2));
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses?key=private-query", "{\"model\":\"gpt-6-astra\",\"stream\":true,\"input\":\"private-prompt\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("rate-limit-request-123", string.Join(",", response.Headers.GetValues("x-oneapi-request-id")));
        Assert.Equal(payload, await response.Content.ReadAsStringAsync());
        var logs = await CompletedLogs(fixture);
        Assert.True(logs.Count == 1, string.Join("\n", logs));
        var line = logs[0];
        foreach (var expected in new[]
                 {
                     "WARNING", "上游 HTTP 200", "原因：上游请求超限", "不再重试（已进入响应转发阶段）", "上游错误码 rate_limit_exceeded",
                     "上游错误类型 too_many_requests", "上游请求 ID rate-limit-request-123", "生成内容：未读取到", "最后事件 error",
                     "首字：无",
                 })
        {
            Assert.True(line.Contains(expected), $"缺少 {expected}: {line}");
        }

        Assert.DoesNotContain("输入 未获取", line);
        Assert.DoesNotContain("private", line);
        Assert.Equal(1, requests);
        var metrics = fixture.Metrics.Snapshot();
        Assert.Equal(0UL, metrics.RetryCount);
        Assert.Equal(1UL, metrics.FailedRequests);
        Assert.Equal(0UL, metrics.SuccessfulRequests);
    }

    [Fact]
    public async Task InterruptedStreamsKeepProgressAndRequestIdsWithoutClaimingRateLimits()
    {
        foreach (var (breakConnection, expected) in new[] { (true, "上游回复到一半，连接就断了"), (false, "上游回复到一半就没了动静，已等到超时") })
        {
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                await Upstream.Begin(context, 200, "text/event-stream", new Dictionary<string, string> { ["x-request-id"] = "broken-stream-request-123" });
                await Upstream.Chunk(context, "data: {\"type\":\"response.created\"}\n\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"private-answer\"}\n\n");
                if (breakConnection)
                {
                    await Task.Delay(100);
                    Upstream.Reset(context);
                }
                else
                {
                    await Task.Delay(1000);
                    await Upstream.Chunk(context, "data: {\"type\":\"response.completed\"}\n\n");
                }
            }, LoggingConfig(0.5, 2));
            using var client = TestClient.Create();
            var response = await TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/v1/responses?key=private-query");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(await TestClient.TryReadAll(response));
            var logs = await CompletedLogs(fixture);
            Assert.True(logs.Count == 1, string.Join("\n", logs));
            var line = logs[0];
            foreach (var part in new[]
                     {
                         expected, "链路：直连", "生成内容：已读取到", "最后事件 response.output_text.delta",
                         "上游请求 ID broken-stream-request-123", "不再重试（已进入响应转发阶段）",
                     })
            {
                Assert.True(line.Contains(part), $"缺少 {part}: {line}");
            }

            Assert.DoesNotContain("上游请求超限", line);
            Assert.DoesNotContain("private", line);
            Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
            Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
        }
    }

    [Fact]
    public async Task ResponseTimeoutsLogTheRetryDecisionWithoutLeakingTheRequestUrl()
    {
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            try
            {
                await Task.Delay(1000, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Upstream.Text(context, 200, "unused");
        }, LoggingConfig(0.2, 1));
        using var client = TestClient.Create();
        var response = await client.GetAsync($"{fixture.Address}/v1/responses?key=private-query");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        await response.Content.ReadAsByteArrayAsync();
        var logs = await CompletedLogs(fixture);
        var failures = logs.FindAll(line => line.Contains("Timeout"));
        Assert.True(failures.Count == 2, string.Join("\n", logs));
        Assert.Contains("第 1 次", failures[0]);
        Assert.Contains("0.0 秒后重试", failures[0]);
        Assert.Contains("第 2 次", failures[1]);
        Assert.Contains("已达到重试上限", failures[1]);
        foreach (var line in logs)
        {
            Assert.DoesNotContain("private-query", line);
        }

        foreach (var line in failures)
        {
            Assert.Contains("上游状态码：无", line);
            Assert.Contains("上游一直没回复，已等到超时", line);
            Assert.Contains("链路：直连", line);
            Assert.Contains("（技术细节：Timeout", line);
        }

        Assert.Equal(1UL, fixture.Metrics.Snapshot().RetryCount);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
    }

    [Fact]
    public async Task ModelListRequestsDoNotReplaceTheSessionKeepAliveTemplate()
    {
        await using var fixture = await LifecycleProxy.StartAsync(
            context => Upstream.Text(context, 200, "{\"model\":\"gpt-session\",\"usage\":{\"input_tokens\":8,\"output_tokens\":2}}"),
            LoggingConfig(5.0, 0));
        fixture.Proxy.KeepAlive.Configure(true, TimeSpan.FromSeconds(1));
        using var client = TestClient.Create();
        var first = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"model\":\"gpt-session\",\"input\":\"hi\"}");
        await first.Content.ReadAsStringAsync();
        await CompletedLogs(fixture);
        var original = fixture.Proxy.KeepAlive.Template();
        Assert.NotNull(original);
        await client.GetStringAsync($"{fixture.Address}/v1/models");
        await CompletedLogs(fixture);
        var current = fixture.Proxy.KeepAlive.Template();
        Assert.NotNull(current);
        Assert.Equal("/v1/responses", current.PathAndQuery);
        Assert.True(original.Body.Span.SequenceEqual(current.Body.Span));
    }

    private const string ChannelBusyError = "{\"error\":{\"code\":\"get_channel_failed\",\"type\":\"new_api_error\",\"message\":\"private-message\"}}";

    [Fact]
    public async Task ChannelRetriesExplainUpstreamErrorCodesLikePreparation()
    {
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context => Interlocked.Increment(ref requests) == 1
            ? Upstream.Bytes(context, 500, Encoding.UTF8.GetBytes(ChannelBusyError), "application/json", new Dictionary<string, string> { ["x-oneapi-request-id"] = "busy-request-123" })
            : Upstream.Json(context, 200, "{\"model\":\"gpt-6-astra\",\"usage\":{\"input_tokens\":8,\"output_tokens\":2}}"),
            LoggingConfig(5.0, 2));
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"model\":\"gpt-6-astra\",\"input\":\"private-prompt\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();
        var logs = await CompletedLogs(fixture);

        // 暂存的错误正文按上游错误码写明原因，并带上与一键准备相同的诊断字段；错误原文不进日志。
        // 每次重试只写一行：状态、诊断字段和等待时间在同一行。
        var attempt = Assert.Single(logs, line => line.Contains("第 1 次 POST /v1/responses"));
        foreach (var expected in new[]
                 {
                     "WARNING", "-> 上游 HTTP 500（当前需求量高，模型负载已达上限），上游错误码 get_channel_failed，上游错误类型 new_api_error",
                     "上游请求 ID busy-request-123", "模型 gpt-6-astra", "秒后重试",
                 })
        {
            Assert.True(attempt.Contains(expected), $"缺少 {expected}: {attempt}");
        }

        Assert.DoesNotContain("输入 未获取", attempt);
        Assert.DoesNotContain("首字", attempt);
        Assert.Contains(logs, line => line.Contains("INFO") && line.Contains("上游 HTTP 200（重试 1 次后成功）"));
        Assert.Equal(2, logs.Count);
        Assert.DoesNotContain(logs, line => line.Contains("private"));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task RepeatedRetriesForTheSameReasonAreSummarized()
    {
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context => Interlocked.Increment(ref requests) <= 25
            ? Upstream.Text(context, 500, "busy")
            : Upstream.Json(context, 200, "{\"model\":\"gpt-6-astra\",\"usage\":{\"input_tokens\":8,\"output_tokens\":2}}"),
            LoggingConfig(5.0, 30));
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"model\":\"gpt-6-astra\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();
        var logs = await CompletedLogs(fixture);

        // 25 次同样的 500：第 1 次完整一行，第 20 次一条进度，最后一行成功并写明重试次数。
        Assert.True(logs.Count == 3, string.Join("\n", logs));
        Assert.True(logs[0].Contains("第 1 次 POST /v1/responses -> 上游 HTTP 500（上游服务内部错误），模型 gpt-6-astra，0.0 秒后重试"), logs[0]);
        Assert.True(logs[1].Contains("已重试 20 次，仍是上游 HTTP 500（上游服务内部错误）"), logs[1]);
        Assert.True(logs[2].Contains("POST /v1/responses -> 上游 HTTP 200（重试 25 次后成功）"), logs[2]);
        Assert.Equal(25UL, fixture.Metrics.Snapshot().RetryCount);
    }

    [Fact]
    public async Task RetryExhaustionNamesTheReasonOfTheReturnedResponse()
    {
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return Upstream.Json(context, 500, ChannelBusyError);
            }

            // 之后的尝试连不上上游，只能把第一次暂存的完整 500 交给客户端。
            Upstream.Reset(context);
            return Task.CompletedTask;
        }, LoggingConfig(5.0, 1));
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"model\":\"gpt-test\",\"input\":\"hi\"}");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ChannelBusyError, await response.Content.ReadAsStringAsync());
        var logs = await CompletedLogs(fixture);
        Assert.Contains(logs, line => line.Contains("重试耗尽，返回客户端最后一次完整上游响应 HTTP 500（当前需求量高，模型负载已达上限）"));
    }
}
