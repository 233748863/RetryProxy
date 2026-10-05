using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Config;
using RetryProxy.Core.Diagnostics;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Storage;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

public class RequestDiagnosticsTests
{
    private const string Secret = "sk-diagnostic-secret";
    private const string LocalToken = "diagnostic-local-token";
    private const string InputMarker = "PRIVATE_INPUT_DIAGNOSTIC_MARKER";
    private const string OutputMarker = "PRIVATE_OUTPUT_DIAGNOSTIC_MARKER";

    private sealed class RecordedRequest(DiagnosticRequestInfo info) : IDiagnosticRequest
    {
        public DiagnosticRequestInfo Info { get; } = info;
        public ConcurrentQueue<DiagnosticEntry> Entries { get; } = new();
        public void Record(DiagnosticEntry entry) => Entries.Enqueue(entry);
        public bool Finished => Entries.Any(entry => entry.Kind == DiagnosticEventKind.Finished);
    }

    private sealed class Recorder : IRequestDiagnostics
    {
        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
        public IDiagnosticRequest Begin(DiagnosticRequestInfo request)
        {
            var result = new RecordedRequest(request);
            result.Record(new DiagnosticEntry(DiagnosticEventKind.Started, 0));
            Requests.Enqueue(result);
            return result;
        }
    }

    private sealed class BrokenRecorder(bool failAtBegin) : IRequestDiagnostics, IDiagnosticRequest
    {
        public IDiagnosticRequest Begin(DiagnosticRequestInfo request) => failAtBegin
            ? throw new IOException(Secret) : this;
        public void Record(DiagnosticEntry entry) => throw new IOException(Secret);
    }

    private static ChannelSnapshot Snapshot(string upstream, ProxyConfig config, string key = "a", string? secret = Secret) => new()
    {
        ClientType = config.ClientType,
        ProviderId = "provider-" + key,
        ProviderName = "供应商" + key,
        KeyId = "key-" + key,
        KeyName = "主号" + key,
        ApiKey = secret ?? string.Empty,
        LocalToken = LocalToken,
        UpstreamBaseUrl = upstream,
        MaxRetries = config.MaxRetries,
        TimeoutSeconds = config.TimeoutSeconds,
        GenerationTimeoutSeconds = config.GenerationTimeoutSeconds,
        TotalTimeoutSeconds = config.TotalTimeoutSeconds,
        BaseDelaySeconds = config.BaseDelaySeconds,
        MaxDelaySeconds = config.MaxDelaySeconds,
    };

    private static Task<LifecycleProxy> Start(Func<HttpContext, Task> upstream, ProxyConfig config, IRequestDiagnostics? diagnostics)
        => LifecycleProxy.StartAsync(upstream, config, proxy =>
        {
            proxy.WithRandomValue(() => 1);
            proxy.WithDiagnostics(diagnostics);
            proxy.UpdateSnapshot(Snapshot(config.UpstreamBaseUrl, config));
            return proxy;
        });

    private static ProxyConfig Configuration(int retries = 0)
    {
        var config = Configs.Default();
        config.MaxRetries = retries;
        config.TimeoutSeconds = 2;
        config.GenerationTimeoutSeconds = 0.25;
        config.TotalTimeoutSeconds = 5;
        config.BaseDelaySeconds = 0;
        config.MaxDelaySeconds = 0;
        return config;
    }

    private static async Task<RecordedRequest> Finished(Recorder recorder)
    {
        await TestClock.WaitUntil(() => recorder.Requests.Count == 1 && recorder.Requests.Single().Finished);
        return Assert.Single(recorder.Requests);
    }

    [Theory]
    [InlineData(200, "application/json", "", true, 200)]
    [InlineData(200, "application/json", "{}", true, 200)]
    [InlineData(201, "application/json", "{}", false, 201)]
    [InlineData(400, "application/json", "{\"error\":{\"type\":\"invalid_request_error\"}}", false, 400)]
    [InlineData(200, "application/json", "{\"error\":{\"code\":\"overloaded_error\"}}", false, 200)]
    [InlineData(200, "application/json", "{\"output\":[{\"type\":\"function_call\",\"name\":\"test\",\"arguments\":\"{}\"}],\"status\":\"completed\"}", true, 200)]
    [InlineData(200, "text/event-stream", Configs.GeneratedStream, true, 200)]
    [InlineData(200, "text/event-stream", "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n", true, 200)]
    [InlineData(200, "text/event-stream", "", false, 502)]
    public async Task ObservingRequestsPreservesExistingStatusBytesAndSuccessRules(int status, string contentType, string body, bool success, int deliveredStatus)
    {
        // 先跑空诊断，再跑同样请求的有诊断版本；显式锁定空 JSON 也能成功的原规则。
        foreach (var enabled in new[] { false, true })
        {
            var recorder = new Recorder();
            var hits = 0;
            await using var fixture = await Start(context =>
            {
                Interlocked.Increment(ref hits);
                return Upstream.Text(context, status, body, contentType);
            }, Configuration(), enabled ? recorder : null);
            using var client = TestClient.Create();
            var requestBody = JsonSerializer.Serialize(new { model = "gpt-test", input = InputMarker, stream = contentType == "text/event-stream" });
            using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", requestBody);
            var returned = await response.Content.ReadAsStringAsync();
            Assert.Equal(deliveredStatus, (int)response.StatusCode);
            if (status == deliveredStatus) Assert.Equal(body, returned);
            await fixture.CompletedLogs();
            Assert.Equal(1, hits);
            var metrics = fixture.Metrics.Snapshot();
            Assert.Equal(success ? 1UL : 0UL, metrics.SuccessfulRequests);
            Assert.Equal(success ? 0UL : 1UL, metrics.FailedRequests);
            Assert.Equal(0UL, metrics.RetryCount);
            if (!enabled) { Assert.Empty(recorder.Requests); continue; }
            var recorded = await Finished(recorder);
            Assert.Equal(32, recorded.Info.RequestId.Length);
            Assert.Single(recorded.Entries, entry => entry.Kind == DiagnosticEventKind.SendStarted);
            Assert.Single(recorded.Entries, entry => entry.Kind == DiagnosticEventKind.SendFinished);
            var result = Assert.Single(recorded.Entries, entry => entry.Kind == DiagnosticEventKind.Outcome);
            Assert.Equal(success ? DiagnosticOutcome.Success : DiagnosticOutcome.Failure, result.Outcome);
            Assert.Single(recorded.Entries, entry => entry.Kind == DiagnosticEventKind.Finished);
            Assert.Equal(DiagnosticDelivery.Complete,
                Assert.Single(recorded.Entries, entry => entry.Kind == DiagnosticEventKind.Delivery).Delivery);
        }
    }

    [Fact]
    public async Task HttpRetriesKeepAllAttemptsWithoutChangingCountsOrLoggingThrottle()
    {
        var recorder = new Recorder();
        var hits = 0;
        var config = Configuration(2);
        config.BaseDelaySeconds = config.MaxDelaySeconds = 0.01;
        await using var fixture = await Start(context => Interlocked.Increment(ref hits) < 3
            ? Upstream.Json(context, 429, "{\"error\":{\"code\":\"rate_limit_exceeded\"}}")
            : Upstream.Json(context, 200, "{}"), config, recorder);
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{\"model\":\"gpt-test\"}");
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        var record = await Finished(recorder);
        var entries = record.Entries.ToArray();
        Assert.Equal(3, hits);
        Assert.Equal(new ulong?[] { 1, 2, 3 }, entries.Where(e => e.Kind == DiagnosticEventKind.SendStarted).Select(e => e.SendNumber));
        Assert.Equal(new int?[] { 429, 429, 200 }, entries.Where(e => e.Kind == DiagnosticEventKind.SendFinished).Select(e => e.StatusCode));
        Assert.Equal(new ulong?[] { 1, 2 }, entries.Where(e => e.Kind == DiagnosticEventKind.RetryWaiting).Select(e => e.RetryCount));
        Assert.Equal(2, entries.Count(e => e.Kind == DiagnosticEventKind.RetryWaitFinished));
        Assert.Equal(2UL, fixture.Metrics.Snapshot().RetryCount);
        var logs = await fixture.CompletedLogs();
        Assert.Equal(1, logs.Split('\n').Count(line => line.Contains("第 1 次", StringComparison.Ordinal)));
        Assert.DoesNotContain("第 2 次", logs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationFailuresHaveTheirOwnAttemptAndWaitingPhase(bool rateLimited)
    {
        var hits = 0;
        var recorder = new Recorder();
        var first = rateLimited
            ? "data: {\"type\":\"error\",\"error\":{\"code\":\"rate_limit_exceeded\"}}\n\n"
            : "data: {\"type\":\"response.created\",\"response\":{\"output\":[]}}\n\n";
        await using var fixture = await Start(context => Upstream.EventStream(context,
            Interlocked.Increment(ref hits) == 1 ? first : Configs.GeneratedStream), Configuration(1), recorder);
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{\"stream\":true}");
        Assert.Equal(Configs.GeneratedStream, await response.Content.ReadAsStringAsync());
        var record = await Finished(recorder);
        Assert.Equal(2, record.Entries.Count(e => e.Kind == DiagnosticEventKind.WaitingGeneration));
        Assert.Equal(2, record.Entries.Count(e => e.Kind == DiagnosticEventKind.SendStarted));
        Assert.Equal("等待生成重试", Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.RetryWaiting).Reason);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().RetryCount);
    }

    [Fact]
    public async Task AuthenticationCompatibilityIsASendButNotACountedRetry()
    {
        var recorder = new Recorder();
        var hits = 0;
        var config = Configuration();
        config.ClientType = ClientType.Claude;
        await using var fixture = await Start(context => Interlocked.Increment(ref hits) == 1
            ? Upstream.Json(context, 401, "{}") : Upstream.Json(context, 200, "{}"), config, recorder);
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/messages", "{\"model\":\"claude-test\"}");
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        var record = await Finished(recorder);
        Assert.Equal(2, hits);
        Assert.Equal(2, record.Entries.Count(e => e.Kind == DiagnosticEventKind.SendStarted));
        Assert.Equal("认证格式兼容重发", Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.CompatibilityResend).Reason);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
        Assert.All(record.Entries, entry => Assert.True(entry.RetryCount is null or 0));
    }

    [Fact]
    public async Task CacheCompatibilityIsRecordedWithinTheSameOuterAttempt()
    {
        var recorder = new Recorder();
        var bodies = new ConcurrentQueue<string>();
        await using var fixture = await Start(async context =>
        {
            var body = await Upstream.ReadBody(context);
            bodies.Enqueue(body);
            await Upstream.Json(context, bodies.Count == 1 ? 400 : 200, bodies.Count == 1
                ? "{\"error\":{\"message\":\"Unknown parameter: prompt_cache_key\",\"param\":\"prompt_cache_key\",\"code\":\"unknown_parameter\"}}" : "{}");
        }, Configuration(), recorder);
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{\"model\":\"gpt-test\",\"input\":\"hi\"}",
            headers: new Dictionary<string, string> { ["thread_id"] = "diagnostic-thread" });
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        var record = await Finished(recorder);
        Assert.Contains("prompt_cache_key", bodies.First());
        Assert.DoesNotContain("prompt_cache_key", bodies.Last());
        var sends = record.Entries.Where(e => e.Kind == DiagnosticEventKind.SendStarted).ToArray();
        Assert.Equal(2, sends.Length);
        Assert.All(sends, send => Assert.Equal(1UL, send.AttemptNumber));
        Assert.Equal("缓存标识兼容重发", Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.CompatibilityResend).Reason);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualSwitchRecordsActualTargetsAndInterruptedWait(bool duringBackoff)
    {
        var recorder = new Recorder();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = Configuration(2);
        config.TotalTimeoutSeconds = 15;
        config.BaseDelaySeconds = config.MaxDelaySeconds = duringBackoff ? 5 : 0;
        await using var other = await FakeUpstream.StartAsync(context => Upstream.Json(context, 200, "{}"));
        await using var fixture = await Start(async context =>
        {
            reached.TrySetResult();
            if (duringBackoff) await Upstream.Json(context, 429, "{}");
            else await Upstream.Pending(context);
        }, config, recorder);
        using var client = TestClient.Create(10);
        var pending = TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{}");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (duringBackoff) await TestClock.WaitUntil(() => recorder.Requests.Single().Entries.Any(e => e.Kind == DiagnosticEventKind.RetryWaiting));
        Assert.Equal(1, fixture.Proxy.UpdateSnapshot(Snapshot(other.BaseUrl, config, "b", "sk-other-secret")));
        using var response = await pending;
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        var record = await Finished(recorder);
        var sends = record.Entries.Where(e => e.Kind == DiagnosticEventKind.SendStarted).ToArray();
        Assert.Equal(new[] { "provider-a", "provider-b" }, sends.Select(e => e.Target!.ProviderId));
        Assert.Equal("手动切换后重发", sends[1].Reason);
        Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.Switched);
        Assert.Equal(duringBackoff ? 1UL : 0UL, fixture.Metrics.Snapshot().RetryCount);
        if (duringBackoff)
            Assert.Equal("等待被中断", Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.RetryWaitFinished).Reason);
    }

    [Theory]
    [InlineData("GET", "/models")]
    [InlineData("GET", "/v1/models/?version=secret-query")]
    [InlineData("GET", "/prefix/v1/models")]
    [InlineData("HEAD", "/api/hello")]
    [InlineData("GET", "/_retry/health")]
    public async Task AuxiliaryRequestsAreNotRecorded(string method, string path)
    {
        var recorder = new Recorder();
        await using var fixture = await Start(context => Upstream.Json(context, 200, "{}"), Configuration(), recorder);
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, new HttpMethod(method), fixture.Address + path);
        await response.Content.ReadAsStringAsync();
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().ActiveRequests == 0);
        Assert.Empty(recorder.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredBackgroundRequestIsNotRecorded(bool bodyMarker)
    {
        var recorder = new Recorder();
        var marker = Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        InternalSessions.Register(marker, cancellation);
        try
        {
            await using var fixture = await Start(context => Upstream.Json(context, 200, "{}"), Configuration(), recorder);
            using var client = TestClient.Create();
            var body = bodyMarker ? JsonSerializer.Serialize(new
            {
                client_metadata = new Dictionary<string, string>
                {
                    ["x-codex-turn-metadata"] = JsonSerializer.Serialize(new { retry_proxy_keepalive = marker }),
                },
            }) : "{}";
            var headers = bodyMarker ? null : new Dictionary<string, string> { ["x-retry-keepalive"] = marker };
            using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", body, headers: headers);
            Assert.Equal("{}", await response.Content.ReadAsStringAsync());
            Assert.Empty(recorder.Requests);
        }
        finally { InternalSessions.Unregister(marker); }
    }

    [Fact]
    public async Task PreparationProxyDoesNotCaptureEvenAnUnmarkedRequest()
    {
        var recorder = new Recorder();
        await using var fixture = await Start(context => Upstream.Json(context, 200, "{}"), Configuration(), recorder);
        fixture.Proxy.AsPreparationProxy(Secret, LocalToken);
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{}",
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + LocalToken });
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task MissingKeyIsLocalFailureWithoutAnInventedSend()
    {
        var recorder = new Recorder();
        var config = Configuration();
        var hits = 0;
        await using var fixture = await Start(context => { Interlocked.Increment(ref hits); return Upstream.Json(context, 200, "{}"); }, config, recorder);
        fixture.Proxy.UpdateSnapshot(Snapshot(fixture.UpstreamAddress, config, secret: null));
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{}");
        await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var record = await Finished(recorder);
        Assert.Equal(0, hits);
        Assert.DoesNotContain(record.Entries, e => e.Kind == DiagnosticEventKind.SendStarted);
        Assert.Equal(403, Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.Outcome).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiagnosticExceptionsNeverChangeTheResponse(bool failAtBegin)
    {
        await using var fixture = await Start(context => Upstream.Json(context, 200, "{}"), Configuration(), new BrokenRecorder(failAtBegin));
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        var logs = await fixture.CompletedLogs();
        Assert.Equal(1UL, fixture.Metrics.Snapshot().SuccessfulRequests);
        Assert.DoesNotContain(Secret, logs);
    }

    [Fact]
    public async Task DiagnosisDoesNotRetainBodiesCredentialsOrQueryStrings()
    {
        var recorder = new Recorder();
        var output = "{\"error\":{\"code\":\"invalid_request_error\",\"message\":\"" + OutputMarker + "\"}}";
        await using var fixture = await Start(context => Upstream.Json(context, 400, output), Configuration(), recorder);
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post,
            fixture.Address + "/private-path-token/v1/responses?api_key=PRIVATE_QUERY_TOKEN",
            "{\"model\":\"gpt-test\",\"input\":\"" + InputMarker + "\"}",
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer sk-original-client-key" });
        Assert.Equal(output, await response.Content.ReadAsStringAsync());
        var record = await Finished(recorder);
        var all = JsonSerializer.Serialize(record.Info) + JsonSerializer.Serialize(record.Entries.ToArray());
        foreach (var secret in new[] { Secret, LocalToken, InputMarker, OutputMarker, "PRIVATE_QUERY_TOKEN", "private-path-token", "sk-original-client-key" })
            Assert.DoesNotContain(secret, all);
        Assert.Equal("/responses", record.Info.Endpoint);
        Assert.Contains("invalid_request_error", all);
    }

    [Fact]
    public async Task OpaqueCredentialsEchoedInDiagnosticFieldsAreRemoved()
    {
        const string opaque = "opaque-credential-for-diagnostic-test";
        var recorder = new Recorder();
        var config = Configuration();
        var output = "{\"error\":{\"code\":\"" + opaque + "\"}}";
        await using var fixture = await Start(context =>
        {
            context.Response.Headers["x-request-id"] = "request-" + opaque + "-echo";
            return Upstream.Json(context, 400, output);
        }, config, recorder);
        fixture.Proxy.UpdateSnapshot(Snapshot(fixture.UpstreamAddress, config, secret: opaque));
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{}");
        Assert.Equal(output, await response.Content.ReadAsStringAsync());
        var record = await Finished(recorder);
        Assert.DoesNotContain(opaque, JsonSerializer.Serialize(record.Entries.ToArray()));
        Assert.Equal(DiagnosticOutcome.Failure, Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.Outcome).Outcome);
    }

    private sealed class ReadFailureStream : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            => Task.FromException(new IOException("PRIVATE_BODY_READ_FAILURE"));
    }

    private sealed class WriteFailureStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("PRIVATE_DELIVERY_FAILURE"));
    }

    private static DefaultHttpContext Context(LifecycleProxy fixture, Stream requestBody, Stream responseBody)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Host = new HostString("127.0.0.1", fixture.Port);
        context.Request.Path = "/v1/responses";
        context.Request.ContentType = "application/json";
        context.Request.Body = requestBody;
        context.Response.Body = responseBody;
        return context;
    }

    [Fact]
    public async Task BodyReadFailureIsRecordedWithoutAnUpstreamSendOrRawException()
    {
        var recorder = new Recorder();
        var hits = 0;
        await using var fixture = await Start(context => { Interlocked.Increment(ref hits); return Upstream.Json(context, 200, "{}"); }, Configuration(), recorder);
        using var requestBody = new ReadFailureStream();
        using var responseBody = new MemoryStream();
        var context = Context(fixture, requestBody, responseBody);
        await fixture.Proxy.HandleAsync(context);
        var record = await Finished(recorder);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(0, hits);
        Assert.DoesNotContain(record.Entries, e => e.Kind == DiagnosticEventKind.SendStarted);
        Assert.Equal(DiagnosticOutcome.Failure, Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.Outcome).Outcome);
        Assert.DoesNotContain("PRIVATE_BODY_READ_FAILURE", JsonSerializer.Serialize(record.Entries.ToArray()));
    }

    [Fact]
    public async Task FailedDownstreamDeliveryDoesNotOverwriteAnAlreadyRecordedSuccess()
    {
        var recorder = new Recorder();
        await using var fixture = await Start(context => Upstream.Json(context, 200, "{}"), Configuration(), recorder);
        using var requestBody = new MemoryStream("{}"u8.ToArray());
        using var responseBody = new WriteFailureStream();
        var context = Context(fixture, requestBody, responseBody);
        await fixture.Proxy.HandleAsync(context);
        var record = await Finished(recorder);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().SuccessfulRequests);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().FailedRequests);
        Assert.Equal(DiagnosticOutcome.Success, Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.Outcome).Outcome);
        Assert.Equal(DiagnosticDelivery.Interrupted, Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.Delivery).Delivery);
        Assert.DoesNotContain("PRIVATE_DELIVERY_FAILURE", JsonSerializer.Serialize(record.Entries.ToArray()));
    }

    [Fact]
    public async Task RealPipelineRecordsRestoreAcrossStoreInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-proxy-diagnostic-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            string requestId;
            DateOnly date;
            using (var data = new DataDatabase(directory))
            using (var store = new DiagnosticStore(data))
            {
                var hits = 0;
                await using var fixture = await Start(context => Upstream.Json(context, Interlocked.Increment(ref hits) == 1 ? 429 : 200, "{}"), Configuration(1), store);
                using var client = TestClient.Create();
                using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{\"model\":\"gpt-test\"}");
                Assert.Equal("{}", await response.Content.ReadAsStringAsync());
                await fixture.CompletedLogs();
                date = store.Today;
                DiagnosticSummary? summary = null;
                for (var index = 0; index < 100; index++)
                {
                    summary = (await store.QueryAsync(new DiagnosticQuery(date, ClientType.Codex))).Items.SingleOrDefault();
                    if (summary?.Finished == true) break;
                    await Task.Delay(10);
                }
                Assert.NotNull(summary);
                Assert.True(summary.Finished);
                Assert.False(summary.Incomplete);
                Assert.Equal(DiagnosticOutcome.Success, summary.Outcome);
                Assert.Equal(2UL, summary.SendCount);
                Assert.Equal(1UL, summary.RetryCount);
                requestId = summary.Request.RequestId;
            }
            using var restoredData = new DataDatabase(directory);
            using var restored = new DiagnosticStore(restoredData);
            var details = await restored.ReadDetailAsync(date, requestId);
            Assert.NotNull(details.Summary);
            Assert.Equal(DiagnosticOutcome.Success, details.Summary.Outcome);
            Assert.True(details.Summary.PreviousSession);
            Assert.False(details.Summary.Incomplete);
            Assert.Equal(2, details.Events.Count(e => e.Entry.Kind == DiagnosticEventKind.SendStarted));
            Assert.Single(details.Events, e => e.Entry.Kind == DiagnosticEventKind.RetryWaiting);
            Assert.DoesNotContain(Secret, JsonSerializer.Serialize(details));
            Assert.DoesNotContain(LocalToken, JsonSerializer.Serialize(details));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TotalDeadlineKeepsUpstreamStatusSeparateFromLocalResponseStatus()
    {
        var recorder = new Recorder();
        var config = Configuration();
        config.TotalTimeoutSeconds = 0.2;
        config.GenerationTimeoutSeconds = 2;
        await using var fixture = await Start(async context =>
        {
            await Upstream.Begin(context, 200, "text/event-stream");
            await Upstream.Chunk(context, "data: {\"type\":\"response.created\",\"response\":{\"output\":[]}}\n\n");
            await Upstream.Pending(context);
        }, config, recorder);
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{\"stream\":true}");
        await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var record = await Finished(recorder);
        Assert.Equal(200, Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.SendFinished).StatusCode);
        Assert.Equal(504, Assert.Single(record.Entries, e => e.Kind == DiagnosticEventKind.Outcome).StatusCode);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
    }
}
