using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.Metrics;
using RetryProxy.Tests.Support;
using Xunit;
using Pipeline = RetryProxy.Core.Proxy.RetryProxy;

namespace RetryProxy.Tests;

/// <summary>HTTP 200 事件流：转发前限流可以重试，转发后的内容不得重放。</summary>
public class PreForwardRateLimitTests
{
    private const string Created =
        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"discarded-attempt\",\"output\":[]}}\n\n";
    private const string RateLimit =
        "data: {\"type\":\"error\",\"error\":{\"code\":\"rate_limit_exceeded\"}}\n\n";
    private const string Text =
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"already-delivered\"}\n\n";

    private static ProxyConfig Config(long retries = 2)
    {
        var config = Configs.Generation();
        config.MaxRetries = retries;
        config.TimeoutSeconds = 5.0;
        config.GenerationTimeoutSeconds = 5.0;
        config.TotalTimeoutSeconds = 10.0;
        return config;
    }

    private static Task<HttpResponseMessage> Request(HttpClient client, LifecycleProxy fixture) =>
        TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses",
            "{\"model\":\"gpt-test\",\"stream\":true,\"input\":\"hello\"}");

    private static async Task<string> AssertOutcome(LifecycleProxy fixture, ulong retries, bool? success = null)
    {
        var logs = await fixture.CompletedLogs();
        var snapshot = fixture.Metrics.Snapshot();
        Assert.Equal(1UL, snapshot.TotalRequests);
        Assert.Equal(0UL, snapshot.ActiveRequests);
        Assert.Equal(retries, snapshot.RetryCount);
        Assert.Equal(1UL, snapshot.SuccessfulRequests + snapshot.FailedRequests);
        if (success is { } completed)
        {
            Assert.Equal(completed ? 1UL : 0UL, snapshot.SuccessfulRequests);
            Assert.Equal(completed ? 0UL : 1UL, snapshot.FailedRequests);
        }

        return logs;
    }

    [Theory]
    [InlineData("error", "code", "rate_limit_exceeded")]
    [InlineData("error", "code", "rate_limit_error")]
    [InlineData("error", "code", "too_many_requests")]
    [InlineData("error", "type", "rate_limit_exceeded")]
    [InlineData("error", "type", "rate_limit_error")]
    [InlineData("error", "type", "too_many_requests")]
    [InlineData("response.failed", "code", "rate_limit_exceeded")]
    [InlineData("response.failed", "type", "rate_limit_error")]
    public async Task RateLimitBeforeForwardingOnlyDeliversTheSuccessfulAttempt(
        string eventType, string field, string value)
    {
        var error = new Dictionary<string, string> { [field] = value, ["message"] = "private-error" };
        var json = eventType == "error"
            ? JsonSerializer.Serialize(new { type = eventType, error })
            : JsonSerializer.Serialize(new { type = eventType, response = new { error } });
        var firstBody = Created + "data: " + json + "\n\n";
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
        {
            var first = Interlocked.Increment(ref requests) == 1;
            var headers = new Dictionary<string, string> { ["x-request-id"] = first ? "discarded" : "success" };
            if (first)
            {
                headers["x-discarded-only"] = "must-not-leak";
                // HTTP 200 的事件错误仍使用普通退避，不采用 HTTP 429/503 的 Retry-After 规则。
                headers["retry-after"] = "86400";
            }

            return Upstream.EventStream(context, first ? firstBody : Configs.GeneratedStream, headers);
        }, Config());
        using var client = TestClient.Create();
        using var response = await Request(client, fixture);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", Assert.Single(response.Headers.GetValues("x-request-id")));
        Assert.False(response.Headers.Contains("x-discarded-only"));
        Assert.False(response.Headers.Contains("retry-after"));
        Assert.Equal(Encoding.UTF8.GetBytes(Configs.GeneratedStream),
            await response.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var logs = await AssertOutcome(fixture, 1, true);
        Assert.Contains("上游请求超限，未转发", logs);
        Assert.Contains("上游 ID discarded", logs);
        Assert.DoesNotContain("响应未完成", logs);
        Assert.DoesNotContain("private-error", logs);
        Assert.Equal(2, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task CompressedRateLimitRetriesAndPreservesTheSuccessfulCompressedBytes()
    {
        static byte[] Compress(string body) =>
            CompressionPassThroughTests.CompressSegments("gzip", new[] { body })
                .SelectMany(part => part).ToArray();
        var failed = Compress(Created + RateLimit);
        var expected = Compress(Configs.GeneratedStream);
        var requests = 0;
        var config = Config();
        config.PassThroughCompression = true;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
            Upstream.Bytes(context, 200, Interlocked.Increment(ref requests) == 1 ? failed : expected,
                "text/event-stream", new Dictionary<string, string> { ["content-encoding"] = "gzip" }), config);
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses",
            "{\"model\":\"gpt-test\",\"stream\":true}",
            headers: new Dictionary<string, string> { ["accept-encoding"] = "gzip" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await AssertOutcome(fixture, 1, true);
        Assert.Equal(2, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task FragmentedRateLimitRetriesWithoutWaitingForTransportEof()
    {
        var bytes = Encoding.UTF8.GetBytes(Created + RateLimit);
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref requests) > 1)
            {
                await Upstream.EventStream(context, Configs.GeneratedStream);
                return;
            }

            await Upstream.Begin(context, 200, "text/event-stream");
            for (var offset = 0; offset < bytes.Length; offset += 7)
            {
                await Upstream.Chunk(context, bytes[offset..Math.Min(offset + 7, bytes.Length)]);
                await Task.Yield();
            }

            // 上游不关闭连接，重试必须由已收到的限流事件触发。
            await Upstream.Pending(context);
        }, Config());
        using var client = TestClient.Create();
        using var response = await Request(client, fixture);
        Assert.Equal(Configs.GeneratedStream,
            await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await AssertOutcome(fixture, 1, true);
        Assert.Equal(2, Volatile.Read(ref requests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnterminatedRateLimitRetriesWhenGenerationWaitingEnds(bool keepStreamOpen)
    {
        var config = Config();
        config.GenerationTimeoutSeconds = 0.2;
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref requests) > 1)
            {
                await Upstream.EventStream(context, Configs.GeneratedStream);
                return;
            }

            await Upstream.Begin(context, 200, "text/event-stream");
            await Upstream.Chunk(context, Created + RateLimit.TrimEnd('\n'));
            if (keepStreamOpen)
            {
                await Upstream.Pending(context);
            }
        }, config);
        using var client = TestClient.Create();
        using var response = await Request(client, fixture);
        Assert.Equal(Configs.GeneratedStream,
            await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var logs = await AssertOutcome(fixture, 1, true);
        Assert.Contains("上游请求超限，未转发", logs);
        Assert.Contains("错误码 rate_limit_exceeded", logs);
        Assert.DoesNotContain("存在未识别的消息", logs);
        Assert.Equal(2, Volatile.Read(ref requests));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ExhaustedRetriesReturnTheFinalErrorBytesAndHeaders(int maxRetries)
    {
        static string Body(int attempt) => Created + $"id: attempt-{attempt}\r\n" + RateLimit;
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
        {
            var attempt = Interlocked.Increment(ref requests);
            return Upstream.EventStream(context, Body(attempt),
                new Dictionary<string, string> { ["x-request-id"] = $"attempt-{attempt}" });
        }, Config(maxRetries));
        using var client = TestClient.Create();
        using var response = await Request(client, fixture);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"attempt-{maxRetries + 1}", Assert.Single(response.Headers.GetValues("x-request-id")));
        Assert.Equal(Encoding.UTF8.GetBytes(Body(maxRetries + 1)),
            await response.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await AssertOutcome(fixture, (ulong)maxRetries, false);
        Assert.Equal(maxRetries + 1, Volatile.Read(ref requests));
    }

    [Theory]
    [InlineData(Text)]
    [InlineData("data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"name\":\"run\",\"arguments\":\"\"}}\n\n")]
    [InlineData("data: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"thinking\"}\n\n")]
    [InlineData("data: {\"type\":\"future.event\",\"payload\":\"opaque\"}\n\n")]
    public async Task NonWaitingEventBeforeRateLimitInOneWritePreventsRetry(string firstEvent)
    {
        var body = Created + firstEvent + RateLimit;
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
        {
            Interlocked.Increment(ref requests);
            // 一次写入包含先出现的非等待事件和末尾限流，顺序不能被整块错误统计覆盖。
            return Upstream.EventStream(context, body);
        }, Config());
        using var client = TestClient.Create();
        using var response = await Request(client, fixture);
        Assert.Equal(body, await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await AssertOutcome(fixture, 0, false);
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task RateLimitAfterTheClientReceivesContentDoesNotReplayTheRequest()
    {
        var releaseError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = Encoding.UTF8.GetBytes(Created + Text);
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref requests);
            await Upstream.Begin(context, 200, "text/event-stream");
            await Upstream.Chunk(context, prefix);
            await releaseError.Task.WaitAsync(TimeSpan.FromSeconds(5), context.RequestAborted);
            await Upstream.Chunk(context, RateLimit);
        }, Config());
        try
        {
            using var client = TestClient.Create();
            using var response = await Request(client, fixture);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stream = await response.Content.ReadAsStreamAsync();
            var delivered = new byte[prefix.Length];
            await stream.ReadExactlyAsync(delivered, timeout.Token);
            Assert.Equal(prefix, delivered);
            Assert.Equal(RequestPhase.ReceivingResponse, Assert.Single(fixture.Metrics.Snapshot().Requests).Phase);
            // 客户端实际读到全部前缀后，才允许上游发送限流错误。
            releaseError.TrySetResult();
            using var received = new MemoryStream();
            received.Write(delivered);
            await stream.CopyToAsync(received, timeout.Token);
            Assert.Equal(Encoding.UTF8.GetBytes(Created + Text + RateLimit), received.ToArray());
            await AssertOutcome(fixture, 0, false);
            Assert.Equal(1, Volatile.Read(ref requests));
        }
        finally
        {
            releaseError.TrySetResult();
        }
    }

    [Theory]
    [InlineData("data: {\"type\":\"error\",\"error\":{\"code\":\"server_error\"}}\n\n")]
    [InlineData("data: {\"type\":\"error\",\"error\":{\"code\":\"insufficient_quota\",\"message\":\"rate_limit_exceeded\"}}\n\n")]
    [InlineData("data: {\"type\":\"error\",\"error\":{\"code\":\"insufficient_quota\",\"type\":\"rate_limit_error\"}}\n\n")]
    [InlineData("data: {\"type\":\"error\",\"error\":{\"code\":429}}\n\n")]
    [InlineData("data: {\"type\":\"error\",\"error\":\n\n")]
    [InlineData("event: ping\ndata: {\"type\":\"error\",\"error\":{\"code\":\"rate_limit_exceeded\"}}\n\n")]
    public async Task NonRateLimitOrMalformedFirstEventIsNotRetried(string firstEvent)
    {
        var body = firstEvent + RateLimit;
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
        {
            Interlocked.Increment(ref requests);
            return Upstream.EventStream(context, body);
        }, Config());
        using var client = TestClient.Create();
        using var response = await Request(client, fixture);
        Assert.Equal(body, await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await AssertOutcome(fixture, 0, false);
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Theory]
    [InlineData("/v1/responses", "application/json", "{\"error\":{\"code\":\"rate_limit_exceeded\"}}")]
    [InlineData("/status", "text/event-stream", RateLimit)]
    public async Task JsonAndNonApiResponsesDoNotGainEventStreamRetries(string path, string contentType, string body)
    {
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
        {
            Interlocked.Increment(ref requests);
            return Upstream.Text(context, 200, body, contentType);
        }, Config());
        using var client = TestClient.Create();
        // 不带 model，保证 /status 不会被既有规则识别成模型请求。
        using var response = await TestClient.Send(client, HttpMethod.Get, fixture.Address + path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await AssertOutcome(fixture, 0);
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task OversizedWaitingPrefixForwardsEveryByteWithoutRetryingTheLaterRateLimit()
    {
        var prefix = new byte[Pipeline.MaxGenerationPrefixBytes + 1];
        Array.Fill(prefix, (byte)' ');
        prefix[0] = (byte)':';
        prefix[^2] = prefix[^1] = (byte)'\n';
        var expected = prefix.Concat(Encoding.UTF8.GetBytes(RateLimit)).ToArray();
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
        {
            Interlocked.Increment(ref requests);
            return Upstream.Bytes(context, 200, expected, "text/event-stream");
        }, Config());
        using var client = TestClient.Create();
        using var response = await Request(client, fixture);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var logs = await AssertOutcome(fixture, 0, false);
        Assert.Contains("1048576 字节", logs);
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("client")]
    [InlineData("proxy")]
    public async Task RetryBackoffObeysTheTotalDeadlineAndCancellation(string stopMode)
    {
        var config = Config();
        config.TotalTimeoutSeconds = stopMode == "deadline" ? 3.0 : 10.0;
        config.BaseDelaySeconds = config.MaxDelaySeconds = 30.0;
        var requests = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
        {
            Interlocked.Increment(ref requests);
            return Upstream.EventStream(context, Created + RateLimit);
        }, config);
        using var client = TestClient.Create(timeoutSeconds: 8.0);
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Address + "/v1/responses");
        var pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        // 等到真实退避阶段再取消，避免把“首次请求还未发出”误当成取消重试。
        await TestClock.WaitUntil(
            () => fixture.Metrics.Snapshot().Requests is [{ Phase: RequestPhase.WaitingRetry }], 2.0);
        if (stopMode == "client")
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            if (stopMode == "proxy")
            {
                fixture.CancelProxy();
            }

            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(stopMode == "deadline" ? HttpStatusCode.GatewayTimeout : (HttpStatusCode)499,
                response.StatusCode);
            var body = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (stopMode == "deadline")
            {
                using var json = JsonDocument.Parse(body);
                Assert.Equal("proxy_timeout", json.RootElement.GetProperty("error").GetProperty("type").GetString());
            }
            else
            {
                Assert.Empty(body);
            }
        }

        await AssertOutcome(fixture, 1, false);
        Assert.Equal(1, Volatile.Read(ref requests));
    }
}
