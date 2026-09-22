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
        foreach (var expected in new[] { "HTTP 200", "模型 gpt-test", "输入 4096", "输出 32", "缓存命中 512", "推理 8", "首字", "耗时" })
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
        Assert.Contains("第 1/1 次", logs[0]);
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
            var response = await TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/v1/responses");
            if (payload.Contains("response.failed"))
            {
                Assert.Equal(payload, await response.Content.ReadAsStringAsync());
            }
            else
            {
                Assert.Null(await TestClient.TryReadAll(response));
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
            Assert.Contains("输入 未获取 / 输出 未获取", line);
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
                     "WARNING", "第 1/3 次", "上游 HTTP 200", "原因：上游请求超限", "不再重试（已进入响应转发阶段）", "上游错误码 rate_limit_exceeded",
                     "上游错误类型 too_many_requests", "上游请求 ID rate-limit-request-123", "生成内容：未读取到", "最后事件 error",
                     "输入 未获取 / 输出 未获取 token（未读取到用量统计）", "首字：无",
                 })
        {
            Assert.True(line.Contains(expected), $"缺少 {expected}: {line}");
        }

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
        foreach (var (breakConnection, expected) in new[] { (true, "读取上游响应失败（ClientError）"), (false, "读取上游响应超时（Timeout）") })
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
                         expected, "第 1/3 次", "链路：直连", "生成内容：已读取到", "最后事件 response.output_text.delta",
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
        Assert.Contains("第 1/2 次", failures[0]);
        Assert.Contains("将在 0.000 秒后重试", failures[0]);
        Assert.Contains("第 2/2 次", failures[1]);
        Assert.Contains("已达到重试上限", failures[1]);
        foreach (var line in logs)
        {
            Assert.DoesNotContain("private-query", line);
        }

        foreach (var line in failures)
        {
            Assert.Contains("上游状态码：无", line);
            Assert.Contains("收到上游响应前超时（Timeout）", line);
            Assert.Contains("链路：直连", line);
            Assert.Contains("底层原因：", line);
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
}
