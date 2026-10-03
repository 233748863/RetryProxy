using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Metrics;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

public class ModelListRequestTests
{
    private const string Models = "{\"data\":[{\"id\":\"test-model\"}]}";

    private static void AssertNoUsage(ProxyMetrics metrics)
    {
        var snapshot = metrics.Snapshot();
        Assert.Equal(0UL, snapshot.TotalRequests);
        Assert.Equal(0UL, snapshot.ActiveRequests);
        Assert.Equal(0UL, snapshot.SuccessfulRequests);
        Assert.Equal(0UL, snapshot.FailedRequests);
        Assert.Equal(0UL, snapshot.RetryCount);
        Assert.Equal(0UL, snapshot.Cache.MeasuredRequests);
        Assert.Equal(0UL, snapshot.Cache.UnmeasuredRequests);
        Assert.Empty(snapshot.Requests);
    }

    [Theory]
    [InlineData("/models", "/v1/models")]
    [InlineData("/v1/models", "/v1/models")]
    [InlineData("/v1/models/?client_version=test", "/v1/models/?client_version=test")]
    [InlineData("/api/anthropic/v1/models", "/v1/api/anthropic/v1/models")]
    public async Task ModelQueriesDoNotBlockKeepAliveOrRestartItsIdleWindow(string path, string upstreamPath)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Assert.Equal(upstreamPath, context.Request.Path + context.Request.QueryString);
            reached.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            await Upstream.Json(context, 200, Models);
        }, Configs.Default());
        var watchdog = fixture.Proxy.KeepAlive;
        watchdog.Configure(true, TimeSpan.FromMinutes(5));
        using var service = watchdog.RegisterService(KeepAliveFlavor.Codex);
        watchdog.MakeDueForTest();
        using var client = TestClient.Create();
        var pending = client.GetAsync(fixture.Address + path);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            AssertNoUsage(fixture.Metrics);
            Assert.Equal(0UL, watchdog.Snapshot().ActiveRequests);
            Assert.True(watchdog.IdleFor() >= watchdog.Idle);
            using (var probe = watchdog.BeginDueProbe())
            {
                Assert.NotNull(probe);
                Assert.NotNull(probe.Complete("test-model", 10));
            }
            watchdog.MakeDueForTest();
            release.TrySetResult();
            using var response = await pending;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Models, await response.Content.ReadAsStringAsync());
            AssertNoUsage(fixture.Metrics);
            Assert.True(watchdog.IdleFor() >= watchdog.Idle);
            using var next = watchdog.BeginDueProbe();
            Assert.NotNull(next);
            Assert.NotNull(next.Complete("test-model", 20));
            Assert.Contains($"GET {path.Split('?')[0]} -> HTTP 200", await fixture.CompletedLogs());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelQueriesPreserveAnInflightProbeButConversationRequestsStillInterruptIt(bool preparing)
    {
        await using var fixture = await LifecycleProxy.StartAsync(
            context => Upstream.Json(context, 200, Models), Configs.Default());
        var watchdog = fixture.Proxy.KeepAlive;
        watchdog.Configure(!preparing, TimeSpan.FromMinutes(5));
        using var service = watchdog.RegisterService(KeepAliveFlavor.Codex);
        if (preparing) Assert.True(watchdog.RequestPreparation());
        else watchdog.MakeDueForTest();
        using var probe = watchdog.BeginDueProbe();
        Assert.NotNull(probe);
        using var client = TestClient.Create();
        Assert.Equal(Models, await client.GetStringAsync($"{fixture.Address}/v1/models"));
        Assert.False(probe.Cancel.IsCancellationRequested);
        Assert.Equal(probe.SessionId, watchdog.Snapshot().SessionId);
        Assert.True(watchdog.Snapshot().Probing);
        AssertNoUsage(fixture.Metrics);

        using var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"input\":\"hi\"}");
        await response.Content.ReadAsStringAsync();
        await fixture.CompletedLogs();
        Assert.True(probe.Cancel.IsCancellationRequested);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().TotalRequests);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().SuccessfulRequests);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    public async Task ModelQueryRetriesAndFailuresDoNotEnterUsage(int finalStatus)
    {
        var hits = 0;
        var config = Configs.Default();
        config.MaxRetries = 1;
        config.BaseDelaySeconds = 0;
        config.MaxDelaySeconds = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
            Upstream.Json(context, Interlocked.Increment(ref hits) == 1 ? 500 : finalStatus, Models), config);
        using var client = TestClient.Create();
        using var response = await client.GetAsync($"{fixture.Address}/v1/models");
        Assert.Equal(finalStatus, (int)response.StatusCode);
        Assert.Equal(Models, await response.Content.ReadAsStringAsync());
        Assert.Equal(2, hits);
        AssertNoUsage(fixture.Metrics);
        Assert.Contains("GET /v1/models", await fixture.CompletedLogs());
    }
}
