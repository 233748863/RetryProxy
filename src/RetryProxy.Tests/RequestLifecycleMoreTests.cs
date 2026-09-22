using System;
using System.Collections.Generic;
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
using RetryProxy.Core.Metrics;
using RetryProxy.Tests.Support;
using Xunit;
using Pipeline = RetryProxy.Core.Proxy.RetryProxy;

namespace RetryProxy.Tests;

/// <summary>对应 request_lifecycle.rs（第二批：生成门细节、暂存上限、总等待与断连）。</summary>
public class RequestLifecycleMoreTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task GenerationRetriesEmptyEofAndNetworkFailureBeforeContent()
    {
        foreach (var (prefix, disconnect) in new[]
                 {
                     ("", false),
                     ("data: {\"type\":\"keepalive\"}\n\n", false),
                     ("data: {\"type\":\"response.created\",\"response\":{\"output\":[]}}\n\n", false),
                     ("data: {\"type\":\"keepalive\"}\n\n", true),
                 })
        {
            var hits = 0;
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                var attempt = Interlocked.Increment(ref hits) - 1;
                if (attempt > 0)
                {
                    await Upstream.EventStream(context, Configs.GeneratedStream);
                    return;
                }

                await Upstream.Begin(context, 200, "text/event-stream");
                if (prefix.Length > 0)
                {
                    await Upstream.Chunk(context, prefix);
                }

                if (disconnect)
                {
                    await Task.Delay(20);
                    Upstream.Reset(context);
                }
            }, Configs.Generation());
            using var client = TestClient.Create();
            var response = await client.GetAsync($"{fixture.Address}/v1/responses");
            Assert.True(HttpStatusCode.OK == response.StatusCode, $"{prefix}/{disconnect}");
            Assert.Equal(Configs.GeneratedStream, await response.Content.ReadAsStringAsync());
            await fixture.CompletedLogs();
            Assert.Equal(2, hits);
            Assert.Equal(1UL, fixture.Metrics.Snapshot().RetryCount);
        }
    }

    [Fact]
    public async Task GenerationWaitingPreservesFragmentedPrefixAndShowsTheActualPhase()
    {
        var contentReady = new Notify();
        var finishReady = new Notify();
        const string prefix = ": heartbeat\r\n\r\ndata: {\"type\":\"response.created\",\"response\":{\"output\":[]}}\r\n\r\n";
        const string content = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"answer\"}\n\n";
        const string terminal = "data: {\"type\":\"response.completed\"}\n\n";
        var config = Configs.Generation();
        config.GenerationTimeoutSeconds = 2.0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            await Upstream.Begin(context, 200, "text/event-stream");
            foreach (var b in Utf8(prefix))
            {
                await Upstream.Chunk(context, new[] { b });
                await Task.Yield();
            }

            await contentReady.Notified();
            await Upstream.Chunk(context, content);
            await finishReady.Notified();
            await Upstream.Chunk(context, terminal);
        }, config);
        using var client = TestClient.Create();
        var requestTask = TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/v1/responses");
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().Requests is [{ Phase: RequestPhase.WaitingGeneration }]);
        await Task.Delay(150);
        Assert.False(requestTask.IsCompleted, "空消息过早向客户端提交了响应");
        using (var health = await TestClient.Health(client, fixture.Address))
        {
            Assert.Equal("waiting_generation", health.RootElement.GetProperty("metrics").GetProperty("requests")[0].GetProperty("phase").GetString());
            Assert.DoesNotContain("countdown", health.RootElement.GetRawText());
        }

        contentReady.NotifyOne();
        var response = await requestTask;
        var stream = await response.Content.ReadAsStreamAsync();
        var received = new MemoryStream();
        while (received.Length < prefix.Length + content.Length)
        {
            var chunk = await TestClient.NextChunk(stream);
            Assert.NotNull(chunk);
            received.Write(chunk);
        }

        Assert.Equal(RequestPhase.ReceivingResponse, fixture.Metrics.Snapshot().Requests[0].Phase);
        finishReady.NotifyOne();
        while (await TestClient.NextChunk(stream) is { } more)
        {
            received.Write(more);
        }

        Assert.Equal(prefix + content + terminal, Encoding.UTF8.GetString(received.ToArray()));
        await fixture.CompletedLogs();
        Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
    }

    [Fact]
    public async Task GenerationExhaustionReturnsFailureInsteadOfAnEmptySuccess()
    {
        foreach (var continuous in new[] { false, true })
        {
            var hits = 0;
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                Interlocked.Increment(ref hits);
                await Upstream.Begin(context, 200, "text/event-stream");
                while (true)
                {
                    try
                    {
                        await Upstream.Chunk(context, "data: {\"type\":\"keepalive\"}\n\n");
                        if (!continuous)
                        {
                            break;
                        }

                        await Task.Delay(20, context.RequestAborted);
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            }, Configs.Generation());
            using var client = TestClient.Create();
            var response = await client.GetAsync($"{fixture.Address}/v1/responses");
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("upstream_unavailable", error.RootElement.GetProperty("error").GetProperty("type").GetString());
            var logs = await fixture.CompletedLogs();
            Assert.Equal(2, hits);
            var snapshot = fixture.Metrics.Snapshot();
            Assert.Equal(0UL, snapshot.SuccessfulRequests);
            Assert.Equal(1UL, snapshot.FailedRequests);
            Assert.Equal(1UL, snapshot.RetryCount);
            Assert.Contains("已达到重试上限", logs);
        }
    }

    [Fact]
    public async Task GenerationWaitObeysTheTotalDeadlineWithoutReplaying()
    {
        var hits = 0;
        var config = Configs.Generation();
        config.TotalTimeoutSeconds = 0.3;
        config.GenerationTimeoutSeconds = 2.0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await Upstream.Begin(context, 200, "text/event-stream");
            while (!context.RequestAborted.IsCancellationRequested)
            {
                try
                {
                    await Upstream.Chunk(context, "data: {\"type\":\"keepalive\"}\n\n");
                    await Task.Delay(20, context.RequestAborted);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }, config);
        using var client = TestClient.Create();
        var response = await client.GetAsync($"{fixture.Address}/v1/responses");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("proxy_timeout", error.RootElement.GetProperty("error").GetProperty("type").GetString());
        await fixture.CompletedLogs();
        Assert.Equal(1, hits);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
    }

    [Fact]
    public async Task GenerationWaitIsCancelledWhenTheClientLeaves()
    {
        var hits = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await Upstream.Begin(context, 200, "text/event-stream");
            while (!context.RequestAborted.IsCancellationRequested)
            {
                try
                {
                    await Upstream.Chunk(context, ": heartbeat\n\n");
                    await Task.Delay(20, context.RequestAborted);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }, Configs.Generation());
        var socket = await fixture.SocketRequest("/v1/responses");
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().Requests is [{ Phase: RequestPhase.WaitingGeneration }]);
        socket.Dispose();
        var logs = await fixture.CompletedLogs();
        await Task.Delay(350);
        Assert.Equal(1, hits);
        Assert.Contains("客户端在响应转发前断开", logs);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
    }

    [Fact]
    public async Task GenerationNeverReplaysToolCallsReasoningOrUnknownEvents()
    {
        foreach (var (path, payload) in new[]
                 {
                     ("/v1/responses", "data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"name\":\"run\",\"arguments\":\"\"}}\n\n"),
                     ("/v1/responses", "data: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"thinking\"}\n\n"),
                     ("/v1/messages", "data: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"name\":\"run\",\"input\":{}}}\n\n"),
                     ("/v1/chat/completions", "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"function\":{\"name\":\"run\",\"arguments\":\"\"}}]}}]}\n\n"),
                     ("/v1/responses", "data: {\"type\":\"future.event\",\"payload\":\"opaque\"}\n\n"),
                 })
        {
            var hits = 0;
            var finish = new Notify();
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                Interlocked.Increment(ref hits);
                await Upstream.Begin(context, 200, "text/event-stream");
                await Upstream.Chunk(context, ": heartbeat\n\n");
                await Upstream.Chunk(context, payload);
                await finish.Notified();
            }, Configs.Generation());
            using var client = TestClient.Create();
            var response = await TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}{path}");
            Assert.True(HttpStatusCode.OK == response.StatusCode, payload);
            var stream = await response.Content.ReadAsStreamAsync();
            var received = new MemoryStream();
            while (received.Length < ": heartbeat\n\n".Length + payload.Length)
            {
                var chunk = await TestClient.NextChunk(stream);
                Assert.NotNull(chunk);
                received.Write(chunk);
            }

            Assert.Equal($": heartbeat\n\n{payload}", Encoding.UTF8.GetString(received.ToArray()));
            finish.NotifyOne();
            Assert.Null(await ReadRest(stream));
            var logs = await fixture.CompletedLogs();
            Assert.True(1 == hits, payload);
            Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
            Assert.Contains("不再重试", logs);
        }
    }

    /// <summary>读到流结束；连接被代理切断返回 null，正常结束返回读到的字节。</summary>
    private static async Task<byte[]?> ReadRest(Stream stream)
    {
        var buffer = new MemoryStream();
        try
        {
            await stream.CopyToAsync(buffer);
            return buffer.ToArray();
        }
        catch (Exception error) when (error is IOException or HttpRequestException or HttpIOException or SocketException)
        {
            return null;
        }
    }

    [Fact]
    public async Task GenerationPassesEmptyCompletionAndErrorEventsWithoutRetrying()
    {
        foreach (var payload in new[]
                 {
                     "data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n",
                     "data: [DONE]\n\n",
                     "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"server_error\"}}}\n\n",
                     "data: {\"type\":\"response.incomplete\",\"response\":{\"output\":[]}}\n\n",
                     "data: {\"type\":\"error\",\"error\":{\"message\":\"failed\"}}\n\n",
                 })
        {
            var hits = 0;
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                Interlocked.Increment(ref hits);
                await Upstream.EventStream(context, payload);
            }, Configs.Generation());
            using var client = TestClient.Create();
            var response = await client.GetAsync($"{fixture.Address}/v1/responses");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(payload, await response.Content.ReadAsStringAsync());
            await fixture.CompletedLogs();
            Assert.Equal(1, hits);
            Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
        }
    }

    [Fact]
    public async Task GenerationPrefixLimitPreservesAllBytesAndTheRetryBoundary()
    {
        foreach (var overflow in new[] { false, true })
        {
            var prefix = new byte[Pipeline.MaxGenerationPrefixBytes + (overflow ? 1 : 0)];
            Array.Fill(prefix, (byte)' ');
            prefix[0] = (byte)':';
            prefix[^2] = (byte)'\n';
            prefix[^1] = (byte)'\n';
            if (overflow)
            {
                prefix = Combine(prefix, Utf8(Configs.GeneratedStream));
            }

            var hits = 0;
            var config = Configs.Generation();
            config.GenerationTimeoutSeconds = 2.0;
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                var attempt = Interlocked.Increment(ref hits) - 1;
                await Upstream.Bytes(context, 200, attempt == 0 ? prefix : Utf8(Configs.GeneratedStream), "text/event-stream");
            }, config);
            using var client = TestClient.Create();
            var response = await client.GetAsync($"{fixture.Address}/v1/responses");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var received = await response.Content.ReadAsByteArrayAsync();
            var logs = await fixture.CompletedLogs();
            if (overflow)
            {
                Assert.Equal(prefix, received);
                Assert.Equal(1, hits);
                Assert.Contains("1048576 字节暂存上限", logs);
            }
            else
            {
                Assert.Equal(Configs.GeneratedStream, Encoding.UTF8.GetString(received));
                Assert.Equal(2, hits);
            }
        }
    }

    private static byte[] Combine(byte[] left, byte[] right)
    {
        var result = new byte[left.Length + right.Length];
        left.CopyTo(result, 0);
        right.CopyTo(result, left.Length);
        return result;
    }

    [Fact]
    public async Task ResponseTimingIncludesRequestBodyUpload()
    {
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            await Upstream.EventStream(context, "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"model\":\"fixture\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1},\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"answer\"}]}]}}\n\n");
        }, Configs.Retrying(3.0));
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, fixture.Port);
        var stream = socket.GetStream();
        var body = "{\"model\":\"fixture\",\"stream\":true}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /v1/responses HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
        await stream.FlushAsync();
        await Task.Delay(200);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(body));
        await stream.FlushAsync();
        var received = new MemoryStream();
        using (var timeout = new CancellationTokenSource(3000))
        {
            try
            {
                await stream.CopyToAsync(received, timeout.Token);
            }
            catch (Exception)
            {
            }
        }

        var logs = await fixture.CompletedLogs();
        string? completion = null;
        foreach (var line in logs.Split('\n'))
        {
            if (line.Contains("上游 HTTP 200"))
            {
                completion = line;
            }
        }

        Assert.NotNull(completion);
        foreach (var field in new[] { "首字 ", "耗时 " })
        {
            var after = completion[(completion.IndexOf(field, StringComparison.Ordinal) + field.Length)..];
            var seconds = double.Parse(after.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(seconds >= 0.15, completion);
        }
    }

    private static async Task OversizedErrorIsForwardedInFull(bool knownLength)
    {
        var hits = 0;
        var releasePadding = new Notify();
        var finishBody = new Notify();
        var prefix = Utf8("event: error\ndata: {\"type\":\"error\",\"error\":{\"code\":\"overloaded\"}}\n\n");
        var padding = new byte[Pipeline.MaxRetryResponseBodyBytes];
        Array.Fill(padding, (byte)'x');
        var tail = Utf8("\nCOMPLETE-ERROR-TAIL\n");
        var expected = Combine(Combine(prefix, padding), tail);
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await Upstream.Begin(context, 500, "text/event-stream", new Dictionary<string, string> { ["x-request-id"] = "large-error-fixture" }, knownLength ? expected.Length : null);
            await Upstream.Chunk(context, prefix);
            if (knownLength)
            {
                await releasePadding.Notified();
            }

            await Upstream.Chunk(context, padding);
            await finishBody.Notified();
            await Upstream.Chunk(context, tail);
        }, Configs.Retrying(3.0));
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/large-error");
        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal("large-error-fixture", string.Join(",", response.Headers.GetValues("x-request-id")));
        var stream = await response.Content.ReadAsStreamAsync();
        var actual = new MemoryStream();
        var first = await TestClient.NextChunk(stream);
        Assert.NotNull(first);
        Assert.NotEmpty(first);
        actual.Write(first);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().ActiveRequests);
        Assert.Equal(RequestPhase.ReceivingResponse, fixture.Metrics.Snapshot().Requests[0].Phase);
        releasePadding.NotifyOne();
        finishBody.NotifyOne();
        await stream.CopyToAsync(actual);
        Assert.Equal(expected, actual.ToArray());
        Assert.Equal(1, hits);
        var logs = await fixture.CompletedLogs();
        Assert.Contains("1048576 字节暂存上限", logs);
        Assert.DoesNotContain("重试耗尽", logs);
        var snapshot = fixture.Metrics.Snapshot();
        Assert.Equal(1UL, snapshot.TotalRequests);
        Assert.Equal(0UL, snapshot.RetryCount);
        Assert.Equal(1UL, snapshot.FailedRequests);
        Assert.Equal(0UL, snapshot.SuccessfulRequests);
    }

    [Fact]
    public Task OversizedChunkedErrorsForwardThePrefixAndTailWithoutRetrying() => OversizedErrorIsForwardedInFull(false);

    [Fact]
    public Task OversizedKnownLengthErrorsBypassBufferingWithoutTruncation() => OversizedErrorIsForwardedInFull(true);

    [Fact]
    public async Task ErrorsAtTheBufferLimitStillRetryNormally()
    {
        var hits = 0;
        var limit = new byte[Pipeline.MaxRetryResponseBodyBytes];
        Array.Fill(limit, (byte)'x');
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            var attempt = Interlocked.Increment(ref hits) - 1;
            if (attempt == 0)
            {
                await Upstream.Bytes(context, 503, limit);
            }
            else
            {
                await Upstream.Text(context, 200, "recovered");
            }
        }, Configs.Retrying(3.0));
        using var client = TestClient.Create();
        var response = await client.GetAsync($"{fixture.Address}/boundary");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("recovered", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, hits);
        var logs = await fixture.CompletedLogs();
        Assert.DoesNotContain("暂存上限", logs);
        var snapshot = fixture.Metrics.Snapshot();
        Assert.Equal(1UL, snapshot.RetryCount);
        Assert.Equal(1UL, snapshot.SuccessfulRequests);
        Assert.Equal(0UL, snapshot.FailedRequests);
    }

    [Fact]
    public async Task SuccessfulResponsesAreNotLimitedByTheRetryBufferLimit()
    {
        var expected = new byte[Pipeline.MaxRetryResponseBodyBytes + 100];
        Array.Fill(expected, (byte)'x');
        await using var fixture = await LifecycleProxy.StartAsync(context => Upstream.Bytes(context, 200, expected), Configs.Retrying(3.0));
        using var client = TestClient.Create();
        var response = await client.GetAsync($"{fixture.Address}/large-success");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        var logs = await fixture.CompletedLogs();
        Assert.DoesNotContain("暂存上限", logs);
        var snapshot = fixture.Metrics.Snapshot();
        Assert.Equal(1UL, snapshot.SuccessfulRequests);
        Assert.Equal(0UL, snapshot.RetryCount);
        Assert.Equal(0UL, snapshot.FailedRequests);
    }

    [Fact]
    public async Task OversizedErrorsReleaseRequestStateOnDeadlineAndClientDisconnect()
    {
        foreach (var closeClient in new[] { false, true })
        {
            var hits = 0;
            var oversized = new byte[Pipeline.MaxRetryResponseBodyBytes + 1];
            Array.Fill(oversized, (byte)'x');
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                Interlocked.Increment(ref hits);
                await Upstream.Begin(context, 503, null);
                await Upstream.Chunk(context, oversized);
                await Upstream.Pending(context);
            }, Configs.Retrying(closeClient ? 3.0 : 0.7));
            using var client = TestClient.Create();
            var response = await TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/unfinished-error");
            Assert.Equal(503, (int)response.StatusCode);
            var stream = await response.Content.ReadAsStreamAsync();
            Assert.NotNull(await TestClient.NextChunk(stream));
            Assert.Equal(1UL, fixture.Metrics.Snapshot().ActiveRequests);
            if (closeClient)
            {
                response.Dispose();
            }
            else
            {
                Assert.Null(await ReadRest(stream));
            }

            var logs = await fixture.CompletedLogs();
            if (!closeClient)
            {
                Assert.Contains("总等待达到", logs);
            }

            Assert.Equal(1, hits);
            var snapshot = fixture.Metrics.Snapshot();
            Assert.Equal(1UL, snapshot.TotalRequests);
            Assert.Equal(1UL, snapshot.FailedRequests);
            Assert.Equal(0UL, snapshot.RetryCount);
        }
    }

    [Fact]
    public async Task TotalDeadlineCoversAnUnfinishedRequestBody()
    {
        var hits = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await Upstream.Text(context, 200, "unexpected");
        }, Configs.Retrying(0.2));
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, fixture.Port);
        var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /v1/responses HTTP/1.1\r\nHost: localhost\r\nContent-Length: 100\r\n\r\n{"));
        await stream.FlushAsync();
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(2000);
        var received = await stream.ReadAsync(buffer, timeout.Token);
        Assert.StartsWith("HTTP/1.1 504", Encoding.ASCII.GetString(buffer, 0, received));
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().FailedRequests == 1);
        var logs = await fixture.CompletedLogs();
        Assert.Equal(1UL, fixture.Metrics.Snapshot().TotalRequests);
        Assert.Equal(0, hits);
        Assert.Contains("总等待达到 0.2 秒", logs);
    }

    [Fact]
    public async Task DisconnectDuringRequestBodyRecordsOneFailure()
    {
        await using var fixture = await LifecycleProxy.StartAsync(context => Upstream.Text(context, 200, "unexpected"), Configs.Retrying(3.0));
        var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, fixture.Port);
        var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /v1/responses HTTP/1.1\r\nHost: localhost\r\nContent-Length: 100\r\n\r\n{"));
        await stream.FlushAsync();
        await Task.Delay(50);
        socket.Dispose();
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().FailedRequests == 1);
        await fixture.CompletedLogs();
        Assert.Equal(1UL, fixture.Metrics.Snapshot().TotalRequests);
    }

    [Fact]
    public async Task DisconnectDuringRetryWaitPreventsTheNextAttempt()
    {
        var hits = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            context.Response.Headers["retry-after"] = "1";
            await Upstream.Text(context, 503, "busy");
        }, Configs.Retrying(30.0));
        var socket = await fixture.SocketRequest("/retry");
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().RetryCount == 1);
        socket.Dispose();
        var logs = await fixture.CompletedLogs();
        await Task.Delay(1200);
        Assert.Equal(1, hits);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
        Assert.Contains("不再重试", logs);
    }

    [Fact]
    public async Task NewRequestsAndDirectProviderTrafficDoNotWaitForOldRequests()
    {
        var hits = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            if (context.Request.Path == "/pending")
            {
                await Upstream.Pending(context);
                return;
            }

            await Upstream.Text(context, 200, "new response");
        }, Configs.Retrying(30.0));
        var socket = await fixture.SocketRequest("/pending");
        await TestClock.WaitUntil(() => Volatile.Read(ref hits) == 1);
        using var client = TestClient.Create();
        Assert.Equal("new response", await client.GetStringAsync($"{fixture.Address}/new"));
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().ActiveRequests == 1);
        await using var direct = await LifecycleProxy.StartAsync(context => Upstream.Text(context, 200, "other provider"), Configs.Retrying(30.0));
        Assert.Equal("other provider", await client.GetStringAsync(direct.UpstreamAddress));
        Assert.Equal(0UL, direct.Metrics.Snapshot().TotalRequests);
        Assert.Equal(2UL, fixture.Metrics.Snapshot().TotalRequests);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().ActiveRequests);
        socket.Dispose();
        await fixture.CompletedLogs();
    }

    [Fact]
    public async Task TotalDeadlineCoversHeadersFirstChunkAndRequestUpload()
    {
        foreach (var firstChunkPending in new[] { false, true })
        {
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                if (context.Request.Path == "/fast")
                {
                    await Upstream.Text(context, 200, "ready");
                    return;
                }

                if (firstChunkPending)
                {
                    await Upstream.Begin(context, 200, "text/event-stream");
                }

                await Upstream.Pending(context);
            }, Configs.Retrying(0.2));
            using var client = TestClient.Create();
            var response = await client.GetAsync(fixture.Address);
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            await fixture.CompletedLogs();
            Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
            Assert.Equal("ready", await client.GetStringAsync($"{fixture.Address}/fast"));
            await fixture.CompletedLogs();
            Assert.Equal(1UL, fixture.Metrics.Snapshot().SuccessfulRequests);
            Assert.False(fixture.Cancel.IsCancellationRequested);
        }

        await using var upload = await LifecycleProxy.StartAsync(context => Upstream.Text(context, 200, "unused"), Configs.Retrying(0.2));
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, upload.Port);
        var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /v1/responses HTTP/1.1\r\nHost: localhost\r\nContent-Length: 1000\r\nConnection: close\r\n\r\n{"));
        await stream.FlushAsync();
        var bytes = new MemoryStream();
        using (var timeout = new CancellationTokenSource(3000))
        {
            try
            {
                await stream.CopyToAsync(bytes, timeout.Token);
            }
            catch (Exception)
            {
            }
        }

        Assert.StartsWith("HTTP/1.1 504", Encoding.ASCII.GetString(bytes.ToArray()));
        await upload.CompletedLogs();
    }

    [Fact]
    public async Task ExtremeRetryAfterIsBoundedWithoutDurationOverflow()
    {
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            context.Response.Headers["retry-after"] = "18446744073709551616";
            await Upstream.Text(context, 503, "busy");
        }, Configs.Retrying(0.15));
        using var client = TestClient.Create();
        var response = await client.GetAsync(fixture.Address);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await fixture.CompletedLogs();
        Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
    }

    [Fact]
    public async Task TotalDeadlineIsSharedByAllAttempts()
    {
        var hits = 0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await Upstream.Text(context, 500, "retry");
        }, Configs.Retrying(0.4));
        using var client = TestClient.Create();
        var startedAt = DateTime.UtcNow;
        var response = await client.GetAsync(fixture.Address);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await fixture.CompletedLogs();
        Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(2));
        var attempts = Volatile.Read(ref hits);
        Assert.True(attempts >= 2 && attempts < 101, $"attempts={attempts}");
        await Task.Delay(150);
        Assert.Equal(attempts, Volatile.Read(ref hits));
    }

    [Fact]
    public async Task StreamingDeadlineEndsPartialAnswersWithoutReplayingThem()
    {
        var hits = 0;
        var config = Configs.Retrying(0.35);
        config.TimeoutSeconds = 0.15;
        config.GenerationTimeoutSeconds = 0.08;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await Upstream.Begin(context, 200, "text/event-stream");
            await Upstream.Chunk(context, "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n");
            while (!context.RequestAborted.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(20, context.RequestAborted);
                    await Upstream.Chunk(context, ": heartbeat\n\n");
                }
                catch (Exception)
                {
                    break;
                }
            }
        }, config);
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"stream\":true}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync();
        var first = await TestClient.NextChunk(stream);
        Assert.Contains("partial", Encoding.UTF8.GetString(first!));
        using (var timeout = new CancellationTokenSource(2000))
        {
            var failed = false;
            try
            {
                while (true)
                {
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, timeout.Token);
                    if (read == 0)
                    {
                        Assert.Fail("未完成的流不能被当成正常结束");
                    }
                }
            }
            catch (Exception error) when (error is IOException or HttpRequestException or HttpIOException)
            {
                failed = true;
            }

            Assert.True(failed);
        }

        var logs = await fixture.CompletedLogs();
        Assert.Equal(1, hits);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().SuccessfulRequests);
        Assert.Contains("总等待达到 0.35 秒", logs);
        Assert.Contains("不再重试", logs);
    }

    [Fact]
    public async Task TotalDeadlineReleasesAClientThatStopsReading()
    {
        var block = new byte[64 * 1024];
        Array.Fill(block, (byte)'x');
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            await Upstream.Begin(context, 200, null);
            while (!context.RequestAborted.IsCancellationRequested)
            {
                try
                {
                    await Upstream.Chunk(context, block);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }, Configs.Retrying(0.4));
        using var socket = new TcpClient();
        socket.ReceiveBufferSize = 4096;
        await socket.ConnectAsync(IPAddress.Loopback, fixture.Port);
        var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /large HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        await stream.FlushAsync();
        var first = new byte[1024];
        Assert.True(await stream.ReadAsync(first) > 0);
        var logs = await fixture.CompletedLogs();
        Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
        Assert.Contains("总等待达到 0.4 秒", logs);
    }

    [Fact]
    public async Task ExhaustedRetriesForwardTheEntireLastErrorResponse()
    {
        var hits = 0;
        const string finalPayload = "event: error\ndata: {\"type\":\"error\",\"error\":{\"code\":\"server_error\"}}\n\n" + "data: {\"diagnostic\":\"last response tail\"}\n\n";
        var config = Configs.Retrying(2.0);
        config.MaxRetries = 2;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            var attempt = Interlocked.Increment(ref hits);
            if (attempt < 3)
            {
                await Upstream.Text(context, 500, $"earlier failure {attempt}");
                return;
            }

            await Upstream.Begin(context, 500, "text/event-stream", new Dictionary<string, string> { ["x-request-id"] = "last-attempt" });
            await Upstream.Chunk(context, "event: error\ndata: {\"type\":\"error\",\"error\":{\"code\":\"server_error\"}}\n\n");
            await Task.Delay(50);
            await Upstream.Chunk(context, "data: {\"diagnostic\":\"last response tail\"}\n\n");
        }, config);
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"stream\":true}");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("last-attempt", string.Join(",", response.Headers.GetValues("x-request-id")));
        Assert.Equal(finalPayload, await response.Content.ReadAsStringAsync());
        await fixture.CompletedLogs();
        Assert.Equal(3, hits);
        var snapshot = fixture.Metrics.Snapshot();
        Assert.Equal(2UL, snapshot.RetryCount);
        Assert.Equal(1UL, snapshot.FailedRequests);
        Assert.Equal(0UL, snapshot.SuccessfulRequests);
    }

    [Fact]
    public async Task TerminalEventsFinishWithoutWaitingForUpstreamDisconnect()
    {
        foreach (var eventType in new[] { "error", "response.failed", "response.incomplete", "response.completed" })
        {
            var payload = $"event: {eventType}\ndata: {{\"type\":\"{eventType}\"}}\n\n";
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                await Upstream.Begin(context, 200, "text/event-stream");
                await Upstream.Chunk(context, payload);
                await Upstream.Pending(context);
            }, Configs.Retrying(3.0));
            using var client = TestClient.Create();
            var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"stream\":true}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(payload, body);
            await fixture.CompletedLogs();
            var snapshot = fixture.Metrics.Snapshot();
            var successful = eventType == "response.completed" ? 1UL : 0UL;
            Assert.Equal(successful, snapshot.SuccessfulRequests);
            Assert.Equal(1 - successful, snapshot.FailedRequests);
            Assert.Equal(0UL, snapshot.RetryCount);
        }
    }

    [Fact]
    public async Task SuccessfulHttpWithoutATerminalEventAbortsTheResponse()
    {
        foreach (var knownLength in new[] { false, true })
        {
            var payload = Utf8("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n");
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                await Upstream.Begin(context, 200, "text/event-stream", null, knownLength ? payload.Length : null);
                await Upstream.Chunk(context, payload);
                await Task.Delay(50);
            }, Configs.Retrying(2.0));
            using var client = TestClient.Create();
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
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(await TestClient.TryReadAll(response) is null, "缺少完成事件不能正常结束");
            }

            var logs = await fixture.CompletedLogs();
            Assert.Contains("未收到完成事件", logs);
            Assert.Equal(1UL, fixture.Metrics.Snapshot().FailedRequests);
            Assert.Equal(0UL, fixture.Metrics.Snapshot().SuccessfulRequests);
            Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
        }
    }

    [Fact]
    public async Task ClaudePartialToolResponseIsDeliveredAndRequiresMessageStop()
    {
        const string prefix =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_local_test\",\"model\":\"claude-test\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":32,\"output_tokens\":4}}}\n\n"
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"本地测试🧪\"}}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"synthetic-signature\"}}\n\n"
            + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"已收到部分回复\"}}\n\n"
            + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n"
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tool_local_test\",\"name\":\"local_test\",\"input\":{}}}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"value\\\":\"}}\n\n";
        const string tail =
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"1}\"}}\n\n"
            + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":2}\n\n"
            + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":80}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        foreach (var complete in new[] { false, true })
        {
            var release = new Notify();
            var hits = 0;
            await using var fixture = await LifecycleProxy.StartAsync(async context =>
            {
                Interlocked.Increment(ref hits);
                await Upstream.Begin(context, 200, "text/event-stream", new Dictionary<string, string> { ["x-request-id"] = "claude-local-stream" });
                foreach (var fragment in Fragments(Utf8(prefix), 7))
                {
                    await Upstream.Chunk(context, fragment);
                    await Task.Yield();
                }

                await release.Notified();
                if (complete)
                {
                    foreach (var fragment in Fragments(Utf8(tail), 7))
                    {
                        await Upstream.Chunk(context, fragment);
                        await Task.Yield();
                    }

                    await Upstream.Pending(context);
                }
                else
                {
                    Upstream.Reset(context);
                }
            }, Configs.Retrying(5.0));
            using var client = TestClient.Create();
            var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/messages", "{\"model\":\"claude-test\",\"stream\":true}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var stream = await response.Content.ReadAsStreamAsync();
            var received = new MemoryStream();
            var prefixBytes = Utf8(prefix);
            using (var timeout = new CancellationTokenSource(2000))
            {
                while (received.Length < prefixBytes.Length)
                {
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, timeout.Token);
                    Assert.True(read > 0, "完整回复结束前，客户端应已收到上游发来的全部片段");
                    received.Write(buffer, 0, read);
                }
            }

            Assert.Equal(prefixBytes, received.ToArray());
            Assert.Equal(0UL, fixture.Metrics.Snapshot().SuccessfulRequests);
            Assert.Equal(1UL, fixture.Metrics.Snapshot().ActiveRequests);
            release.NotifyOne();
            var failed = false;
            using (var timeout = new CancellationTokenSource(2000))
            {
                try
                {
                    while (true)
                    {
                        var buffer = new byte[4096];
                        var read = await stream.ReadAsync(buffer, timeout.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        received.Write(buffer, 0, read);
                    }
                }
                catch (Exception error) when (error is IOException or HttpRequestException or HttpIOException)
                {
                    failed = true;
                }
            }

            response.Dispose();
            Assert.Equal(!complete, failed);
            var expected = complete ? prefix + tail : prefix;
            Assert.Equal(Utf8(expected), received.ToArray());
            var logs = await fixture.CompletedLogs();
            var metrics = fixture.Metrics.Snapshot();
            Assert.Equal(0UL, metrics.RetryCount);
            Assert.Equal(complete ? 1UL : 0UL, metrics.SuccessfulRequests);
            Assert.Equal(complete ? 0UL : 1UL, metrics.FailedRequests);
            Assert.Equal(1, hits);
            if (!complete)
            {
                foreach (var expectedText in new[] { "读取上游响应失败", "最后事件 content_block_delta", "上游请求 ID claude-local-stream", "不再重试（已进入响应转发阶段）" })
                {
                    Assert.Contains(expectedText, logs);
                }
            }
        }
    }

    private static IEnumerable<byte[]> Fragments(byte[] bytes, int size)
    {
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            yield return bytes[offset..Math.Min(offset + size, bytes.Length)];
        }
    }
}
