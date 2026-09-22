using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RetryProxy.Core.Config;
using RetryProxy.Core.Metrics;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>对应 request_lifecycle.rs（第一批：生成门与阶段）。</summary>
public class RequestLifecycleTests
{
    [Fact]
    public async Task GenerationTimeoutRetriesDespiteHeartbeatsWithoutLeakingTheOldAttempt()
    {
        var hits = 0;
        var heartbeats = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            var attempt = Interlocked.Increment(ref hits) - 1;
            if (attempt > 0)
            {
                await Upstream.EventStream(context, Configs.GeneratedStream, new Dictionary<string, string> { ["x-request-id"] = "new-attempt" });
                return;
            }

            await Upstream.Begin(context, 200, "text/event-stream", new Dictionary<string, string> { ["x-request-id"] = "old-attempt" });
            await Upstream.Chunk(context, "data: {\"type\":\"response.created\",\"response\":{\"id\":\"old-attempt\",\"output\":[]}}\n\n");
            while (!context.RequestAborted.IsCancellationRequested)
            {
                Interlocked.Increment(ref heartbeats);
                try
                {
                    await Upstream.Chunk(context, "event: keepalive\ndata: {\"type\":\"keepalive\"}\n\n");
                    await Task.Delay(20, context.RequestAborted);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }, Configs.Generation());
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"stream\":true}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("new-attempt", string.Join(",", response.Headers.GetValues("x-request-id")));
        Assert.Equal(Configs.GeneratedStream, await response.Content.ReadAsStringAsync());
        var logs = await fixture.CompletedLogs();
        Assert.Equal(2, hits);
        Assert.True(heartbeats >= 2);
        Assert.Contains("等待生成达到 0.25 秒", logs);
        Assert.Contains("最后事件 keepalive", logs);
        Assert.Contains("old-attempt", logs);
        var snapshot = fixture.Metrics.Snapshot();
        Assert.Equal(1UL, snapshot.RetryCount);
        Assert.Equal(1UL, snapshot.SuccessfulRequests);
        Assert.Equal(0UL, snapshot.FailedRequests);
    }

    [Fact]
    public async Task RequestPhasesFollowAttemptsAndBodyDeliveryWithoutCountdowns()
    {
        var hits = 0;
        var firstHeaders = new Notify();
        var secondHeaders = new Notify();
        var finishBody = new Notify();
        var config = Configs.Retrying(3.0);
        config.MaxRetries = 1;
        config.BaseDelaySeconds = 0.4;
        config.MaxDelaySeconds = 0.4;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            var attempt = Interlocked.Increment(ref hits) - 1;
            if (attempt == 0)
            {
                await firstHeaders.Notified();
                await Upstream.Text(context, 503, "busy");
                return;
            }

            await secondHeaders.Notified();
            await Upstream.Begin(context, 200, "text/plain");
            await Upstream.Chunk(context, "first");
            await finishBody.Notified();
            await Upstream.Chunk(context, "last");
        }, config);
        using var client = TestClient.Create();
        var requestTask = TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/phases?api_key=private-query");
        await TestClock.WaitUntil(() => Volatile.Read(ref hits) == 1);
        var snapshot = fixture.Metrics.Snapshot();
        Assert.Single(snapshot.Requests);
        var requestId = snapshot.Requests[0].RequestId;
        Assert.Equal(RequestPhase.WaitingResponse, snapshot.Requests[0].Phase);
        Assert.Equal(1UL, snapshot.Requests[0].Attempt);
        using (var health = await TestClient.Health(client, fixture.Address))
        {
            var first = health.RootElement.GetProperty("metrics").GetProperty("requests")[0];
            Assert.Equal(requestId, first.GetProperty("request_id").GetString());
            Assert.Equal("/phases", first.GetProperty("path").GetString());
            Assert.Equal("waiting_response", first.GetProperty("phase").GetString());
            var text = health.RootElement.GetRawText();
            Assert.DoesNotContain("private-query", text);
            Assert.DoesNotContain("countdown", text);
        }

        firstHeaders.NotifyOne();
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().Requests is [{ Phase: RequestPhase.WaitingRetry }]);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().Requests[0].Attempt);
        await TestClock.WaitUntil(() => Volatile.Read(ref hits) == 2);
        snapshot = fixture.Metrics.Snapshot();
        Assert.Equal(requestId, snapshot.Requests[0].RequestId);
        Assert.Equal(RequestPhase.WaitingResponse, snapshot.Requests[0].Phase);
        Assert.Equal(2UL, snapshot.Requests[0].Attempt);
        Assert.Equal(1UL, snapshot.RetryCount);
        secondHeaders.NotifyOne();
        var response = await requestTask;
        var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal("first", Encoding.UTF8.GetString((await TestClient.NextChunk(stream))!));
        snapshot = fixture.Metrics.Snapshot();
        Assert.Equal(1UL, snapshot.ActiveRequests);
        Assert.Equal(RequestPhase.ReceivingResponse, snapshot.Requests[0].Phase);
        Assert.Equal(2UL, snapshot.Requests[0].Attempt);
        finishBody.NotifyOne();
        Assert.Equal("last", Encoding.UTF8.GetString((await TestClient.NextChunk(stream))!));
        Assert.Null(await TestClient.NextChunk(stream));
        await fixture.CompletedLogs();
        snapshot = fixture.Metrics.Snapshot();
        Assert.Equal(1UL, snapshot.TotalRequests);
        Assert.Equal(1UL, snapshot.SuccessfulRequests);
        Assert.Equal(0UL, snapshot.FailedRequests);
    }

    [Fact]
    public async Task TotalDeadlineStopsRetryAfterWaitAndReturns504()
    {
        var hits = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            context.Response.Headers["retry-after"] = "60";
            await Upstream.Text(context, 503, "busy");
        }, Configs.Retrying(0.3));
        using var client = TestClient.Create();
        var startedAt = DateTime.UtcNow;
        var response = await client.GetAsync(fixture.Address);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("proxy_timeout", error.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(2));
        var logs = await fixture.CompletedLogs();
        Assert.Equal(1, hits);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
        Assert.Contains("总等待达到 0.3 秒", logs);
        Assert.Contains("HTTP 504", logs);
    }

    [Fact]
    public async Task DisconnectBeforeResponseHeadersStopsTheRequest()
    {
        var hits = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await Upstream.Pending(context);
        }, Configs.Retrying(30.0));
        var socket = await fixture.SocketRequest("/pending");
        await TestClock.WaitUntil(() => Volatile.Read(ref hits) == 1);
        socket.Dispose();
        var logs = await fixture.CompletedLogs();
        Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
        Assert.Equal(1, hits);
        Assert.Contains("客户端在响应转发前断开", logs);
        Assert.Contains("不再重试", logs);
    }
}
