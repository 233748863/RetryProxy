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
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Proxy;
using RetryProxy.Core.Service;
using RetryProxy.Tests.Support;
using Xunit;
using Pipeline = RetryProxy.Core.Proxy.RetryProxy;

namespace RetryProxy.Tests;

/// <summary>对应 proxy.rs 里的单元测试。</summary>
public class ProxyUnitTests
{
    private static Pipeline Proxy(double baseDelay, double maxDelay, Func<double>? random = null)
    {
        var config = Configs.Default();
        config.ListenPort = 18080;
        config.BaseDelaySeconds = baseDelay;
        config.MaxDelaySeconds = maxDelay;
        var directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        var logger = ProxyLogger.Silent(directory);
        return new Pipeline(config, logger, new ProxyMetrics(), CancellationToken.None).WithRandomValue(random ?? (() => 0.0));
    }

    [Fact]
    public void InternalBodyMetadataCleanupPreservesOtherClientFields()
    {
        foreach (var other in new[] { "{}", "{\"other\":\"preserved\"}" })
        {
            var turn = JsonSerializer.Serialize("{\"retry_proxy_keepalive\":\"marker\"}");
            var metadata = other == "{}" ? $"{{\"x-codex-turn-metadata\":{turn}}}" : $"{{\"other\":\"preserved\",\"x-codex-turn-metadata\":{turn}}}";
            var body = $"{{\"input\":\"question\",\"client_metadata\":{metadata}}}";
            using var stripped = JsonDocument.Parse(HeaderRules.StripInternalRequestMetadata(Encoding.UTF8.GetBytes(body)));
            Assert.Equal("question", stripped.RootElement.GetProperty("input").GetString());
            if (other == "{}")
            {
                Assert.False(stripped.RootElement.TryGetProperty("client_metadata", out _));
            }
            else
            {
                Assert.Equal(other, stripped.RootElement.GetProperty("client_metadata").GetRawText());
            }
        }

        foreach (var body in new[] { "{ \"input\": \"unchanged\" }", "not JSON", "{\"client_metadata\":{\"x-codex-turn-metadata\":\"invalid\"}}" })
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            Assert.Equal(body, Encoding.UTF8.GetString(HeaderRules.StripInternalRequestMetadata(bytes).Span));
        }
    }

    [Fact]
    public void NetworkDiagnosticsOnlyRecordStructuredSystemErrors()
    {
        foreach (var (code, expected) in new[]
                 {
                     (SocketError.ConnectionRefused, "连接被拒绝"),
                     (SocketError.ConnectionReset, "连接被重置"),
                     (SocketError.AccessDenied, "系统拒绝网络访问"),
                     (SocketError.TimedOut, "网络操作超时"),
                 })
        {
            var error = new InvalidOperationException("private-outer-error", new SocketException((int)code));
            var fields = NetworkErrorLabel.CauseFields(error);
            Assert.True(fields.Contains(expected), fields);
            foreach (var privateText in new[] { "private", "Bearer", "password", "example.test" })
            {
                Assert.False(fields.Contains(privateText), fields);
            }
        }

        var eof = new HttpRequestException("private-query Bearer private-key https://user:password@example.test", new HttpIOException(HttpRequestError.ResponseEnded, "private"));
        var eofFields = NetworkErrorLabel.CauseFields(eof);
        Assert.Contains("连接提前结束", eofFields);
        Assert.DoesNotContain("private", eofFields);
        Assert.Equal("，底层原因：未提供可识别的系统错误", NetworkErrorLabel.CauseFields(new InvalidOperationException("plain")));
    }

    [Fact]
    public void NetworkDiagnosticsPreserveWindowsSocketErrorCodes()
    {
        var fields = NetworkErrorLabel.CauseFields(new SocketException(10061));
        Assert.True(fields.Contains("连接被拒绝"), fields);
        Assert.True(fields.Contains("系统错误码 10061"), fields);
    }

    [Fact]
    public async Task ProxyConnectionFailuresHaveSafeLabelsWithoutExposingTheTarget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var buffer = new byte[1024];
            var headers = new MemoryStream();
            while (true)
            {
                var received = await stream.ReadAsync(buffer);
                if (received == 0)
                {
                    break;
                }

                headers.Write(buffer, 0, received);
                if (Encoding.ASCII.GetString(headers.ToArray()).Contains("\r\n\r\n"))
                {
                    break;
                }

                Assert.True(headers.Length < 8192);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            await stream.FlushAsync();
        });
        using var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
        })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        Exception? failure = null;
        try
        {
            await client.GetAsync("https://private-target.invalid/?key=private-key");
        }
        catch (Exception error)
        {
            failure = error;
        }

        await server;
        Assert.NotNull(failure);
        var upstream = UpstreamException.From(failure);
        Assert.True(upstream.IsConnect, failure.ToString());
        var label = NetworkErrorLabel.Describe(upstream, true, NetworkPhase.AwaitingResponse);
        Assert.True(label.Contains("建立上游连接失败（ConnectError）"), label);
        Assert.True(label.Contains("链路：系统代理"), label);
        Assert.False(label.Contains("private"), label);
        Assert.False(label.Contains("https://"), label);
    }

    [Fact]
    public void CompletedLogsOnlyHideTheFirstHttp200AttemptCounter()
    {
        foreach (var (attempt, total, status, expectedPrefix) in new (ulong, ulong, int, string)[]
                 {
                     (1, 101, 200, "[request-1] POST /v1/responses -> 上游 HTTP 200"),
                     (1, 1, 200, "[request-1] POST /v1/responses -> 上游 HTTP 200"),
                     (2, 101, 200, "[request-1] 第 2/101 次 POST /v1/responses -> 上游 HTTP 200"),
                     (101, 101, 200, "[request-1] 第 101/101 次 POST /v1/responses -> 上游 HTTP 200"),
                     (1, 101, 503, "[request-1] 第 1/101 次 POST /v1/responses -> 上游 HTTP 503"),
                 })
        {
            var line = LogText.FormatCompletedAttempt("request-1", attempt, total, "POST", "/v1/responses", status, 0.25, 1.5, "，模型 gpt-test，输入 8 / 输出 2 token");
            Assert.True(line.StartsWith(expectedPrefix, StringComparison.Ordinal), line);
            Assert.Contains("模型 gpt-test，输入 8 / 输出 2 token", line);
            Assert.Contains("首字 0.25 秒", line);
            Assert.Contains("耗时 1.50 秒", line);
        }
    }

    [Fact]
    public void RetryBackoffReachesCapAndHandlesExtremeAttemptCounts()
    {
        var proxy = Proxy(0.5, 4.0);
        Assert.Equal(0.0, proxy.RetryDelay(0, null, null));
        Assert.Equal(0.5, proxy.RetryDelay(1, null, null));
        Assert.Equal(1.5, proxy.RetryDelay(2, null, null));
        Assert.Equal(3.5, proxy.RetryDelay(3, null, null));
        Assert.Equal(3.5, proxy.RetryDelay(4, null, null));
        Assert.Equal(3.5, proxy.RetryDelay(ulong.MaxValue, null, null));
    }

    [Fact]
    public void ZeroRetryDelaysHandleExtremeAttemptCounts()
    {
        foreach (var random in new[] { 0.0, 0.5, 1.0 })
        {
            foreach (var max in new[] { 0.0, 4.0 })
            {
                var proxy = Proxy(0.0, max, () => random);
                Assert.Equal(0.0, proxy.RetryDelay(ulong.MaxValue, null, null));
            }
        }
    }

    [Fact]
    public void RetryJitterIsSymmetricAndVariesAtTheDelayCap()
    {
        foreach (var (random, expected, capped) in new[] { (0.0, 1.5, 3.5), (0.123, 1.623, 3.5615), (0.5, 2.0, 3.75), (0.75, 2.25, 3.875), (1.0, 2.5, 4.0) })
        {
            var proxy = Proxy(0.5, 4.0, () => random);
            Assert.True(Math.Abs(proxy.RetryDelay(2, null, null) - expected) < 1e-10);
            Assert.True(Math.Abs(proxy.RetryDelay(3, null, null) - capped) < 1e-10);
            Assert.True(Math.Abs(proxy.RetryDelay(ulong.MaxValue, null, null) - capped) < 1e-10);
        }
    }

    [Fact]
    public void ShortRetryIntervalsStayNonnegativeAndCapped()
    {
        foreach (var (random, expected) in new[] { (0.0, 0.0), (0.5, 0.1), (1.0, 0.2) })
        {
            var proxy = Proxy(0.1, 0.2, () => random);
            Assert.Equal(expected, proxy.RetryDelay(0, null, null));
            Assert.Equal(expected, proxy.RetryDelay(ulong.MaxValue, null, null));
        }
    }

    [Fact]
    public void RetryAfterJitterRespectsUpstreamMinimumAndIsUncapped()
    {
        foreach (var (random, jitter) in new[] { (0.0, 0.0), (0.246, 0.123), (1.0, 0.5) })
        {
            var proxy = Proxy(0.5, 4.0, () => random);
            foreach (var (header, minimum) in new[] { ("0", 0.0), ("10", 10.0) })
            {
                var headers = new HeaderList();
                headers.Set("retry-after", header);
                foreach (var status in new[] { 429, 503 })
                {
                    var delay = proxy.RetryDelay(0, status, headers);
                    Assert.True(Math.Abs(delay - (minimum + jitter)) < 1e-10);
                    Assert.True(delay >= minimum);
                }
            }
        }
    }

    [Fact]
    public async Task RetryBodyBufferStopsAtTheFirstOverflowChunk()
    {
        var prefix = new byte[Pipeline.MaxRetryResponseBodyBytes];
        Array.Fill(prefix, (byte)'x');
        using var reader = new ChunkReader(new ListChunkSource(new ReadOnlyMemory<byte>[] { prefix, Encoding.ASCII.GetBytes("overflow"), Encoding.ASCII.GetBytes("unread") }));
        var buffered = await Pipeline.BufferUpstreamResponseAsync(reader, "buffer-test", MonotonicInstant.Now, new ProxyMetrics(), CancellationToken.None);
        Assert.True(buffered.TooLarge, "oversized errors must stop buffering");
        Assert.Equal(prefix, buffered.Body.ToArray());
        Assert.Equal("overflow", Encoding.ASCII.GetString(buffered.Chunk.Span));
        var next = reader.Consume(await Task.FromResult(reader.Peek()));
        Assert.Equal("unread", Encoding.ASCII.GetString(next!.Value.Span));
    }

    [Fact]
    public async Task RetryBodyBufferAcceptsTheExactLimit()
    {
        var expected = new byte[Pipeline.MaxRetryResponseBodyBytes];
        Array.Fill(expected, (byte)'x');
        using var reader = new ChunkReader(new ListChunkSource(new ReadOnlyMemory<byte>[] { expected }));
        var buffered = await Pipeline.BufferUpstreamResponseAsync(reader, "buffer-test", MonotonicInstant.Now, new ProxyMetrics(), CancellationToken.None);
        Assert.False(buffered.TooLarge, "the exact limit is still retryable");
        Assert.Equal(expected, buffered.Body.ToArray());
        Assert.NotNull(buffered.FirstByteSeconds);
    }

    [Fact]
    public async Task RetryBodyBufferDoesNotCopyAnOversizedFirstChunk()
    {
        var expected = new byte[Pipeline.MaxRetryResponseBodyBytes + 1];
        Array.Fill(expected, (byte)'x');
        using var reader = new ChunkReader(new ListChunkSource(new ReadOnlyMemory<byte>[] { expected }));
        var buffered = await Pipeline.BufferUpstreamResponseAsync(reader, "buffer-test", MonotonicInstant.Now, new ProxyMetrics(), CancellationToken.None);
        Assert.True(buffered.TooLarge, "an oversized first chunk must bypass buffering");
        Assert.True(buffered.Body.IsEmpty);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffered.Chunk, out var segment));
        Assert.Same(expected, segment.Array);
        Assert.Equal(expected, buffered.Chunk.ToArray());
    }

    [Fact]
    public void RequestHeadersDropManagedValues()
    {
        var headers = new HeaderList();
        headers.Set("host", "secret");
        headers.Set("authorization", "Bearer x");
        headers.Set("connection", "x-custom");
        headers.Set("x-custom", "remove");
        headers.Set("x-retry-keepalive", "1");
        headers.Set("x-retry-preparation-id", "local-only");
        var copied = HeaderRules.CopyRequestHeaders(headers);
        Assert.NotNull(copied.Get("authorization"));
        Assert.Null(copied.Get("host"));
        Assert.Null(copied.Get("x-custom"));
        Assert.Null(copied.Get("x-retry-keepalive"));
        Assert.Null(copied.Get("x-retry-preparation-id"));
    }

    [Fact]
    public void RequestHeadersStripKeepAliveMarkerFromCodexTurnMetadata()
    {
        var headers = new HeaderList();
        headers.Set("x-codex-turn-metadata", "{\"thread_id\":\"t\",\"retry_proxy_keepalive\":\"m\"}");
        headers.Append("x-codex-turn-metadata", "{\"retry_proxy_keepalive\":\"only\"}");
        var copied = HeaderRules.CopyRequestHeaders(headers);
        var values = new List<string>(copied.GetAll("x-codex-turn-metadata"));
        Assert.Single(values);
        Assert.Equal("{\"thread_id\":\"t\"}", values[0]);
    }

    [Fact]
    public void RetryAfterParsesSecondsAndHttpDates()
    {
        Assert.Equal(10.0, Pipeline.ParseRetryAfter(" 10 "));
        Assert.Equal(0.0, Pipeline.ParseRetryAfter("-5"));
        Assert.Null(Pipeline.ParseRetryAfter("inf"));
        Assert.Null(Pipeline.ParseRetryAfter("soon"));
        var future = DateTimeOffset.UtcNow.AddSeconds(30).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
        var parsed = Pipeline.ParseRetryAfter(future);
        Assert.NotNull(parsed);
        Assert.InRange(parsed.Value, 25.0, 30.0);
        Assert.Equal(0.0, Pipeline.ParseRetryAfter("Wed, 21 Oct 2015 07:28:00 GMT"));
    }

    [Fact]
    public void SystemProxyBypassRulesMatchWindowsSemantics()
    {
        var target = new Uri("https://api.example.com:443/v1");
        Assert.True(SystemProxyResolver.BypassPatternList(new Uri("http://localhost:8080/"), string.Empty));
        Assert.True(SystemProxyResolver.BypassPatternList(new Uri("http://127.0.0.1:1/"), string.Empty));
        Assert.False(SystemProxyResolver.BypassPatternList(target, string.Empty));
        Assert.True(SystemProxyResolver.BypassPatternList(target, "*"));
        Assert.True(SystemProxyResolver.BypassPatternList(target, "*.example.com"));
        Assert.True(SystemProxyResolver.BypassPatternList(target, "example.com"));
        Assert.True(SystemProxyResolver.BypassPatternList(target, "https://api.example.com:443/path"));
        Assert.False(SystemProxyResolver.BypassPatternList(target, "api.example.com:8443"));
        Assert.False(SystemProxyResolver.BypassPatternList(target, "<local>"));
        Assert.True(SystemProxyResolver.BypassPatternList(new Uri("http://intranet/"), "<local>"));
        Assert.Equal("http://127.0.0.1:7897", SystemProxyResolver.NormalizeProxyUrl(" 127.0.0.1:7897 "));
        Assert.Equal("http://127.0.0.1:7897", SystemProxyResolver.SelectProxyServer("http=127.0.0.1:7897;https=127.0.0.1:7898", "http"));
        Assert.Equal("http://127.0.0.1:7898", SystemProxyResolver.SelectProxyServer("http=127.0.0.1:7897;https=127.0.0.1:7898", "HTTPS"));
        Assert.Equal("http://proxy:3128", SystemProxyResolver.SelectProxyServer("proxy:3128", "https"));
        Assert.Null(SystemProxyResolver.SelectProxyServer("socks=proxy:1080", "https"));
    }

    [Fact]
    public async Task BodyMarkedKeepAliveReachesUpstreamWithoutCancellingItself()
    {
        string? forwarded = null;
        var sawMarker = false;
        var config = Configs.Default();
        config.MaxRetries = 0;
        config.TimeoutSeconds = 3.0;
        config.TotalTimeoutSeconds = 3.0;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            sawMarker |= context.Request.Headers.ContainsKey(Pipeline.KeepAliveMarkerHeader);
            forwarded = await Upstream.ReadBody(context);
            await Upstream.Json(context, 200, "{\"object\":\"response\",\"status\":\"completed\",\"model\":\"fixture-model\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Java answer\"}]}],\"usage\":{\"input_tokens\":20,\"output_tokens\":5}}");
        }, config);
        using var session = new CancellationTokenSource();
        InternalSessions.Register("marker-1", session);
        try
        {
            using var client = TestClient.Create();
            var turn = JsonSerializer.Serialize("{\"thread_id\":\"fixture-thread\",\"retry_proxy_keepalive\":\"marker-1\"}");
            var body = $"{{\"input\":\"Java question\",\"client_metadata\":{{\"other\":\"preserved\",\"x-codex-turn-metadata\":{turn}}}}}";
            var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Java answer", await response.Content.ReadAsStringAsync());
            Assert.False(sawMarker);
            using var document = JsonDocument.Parse(forwarded!);
            Assert.Equal("Java question", document.RootElement.GetProperty("input").GetString());
            Assert.Equal("preserved", document.RootElement.GetProperty("client_metadata").GetProperty("other").GetString());
            Assert.Equal("{\"thread_id\":\"fixture-thread\"}", document.RootElement.GetProperty("client_metadata").GetProperty("x-codex-turn-metadata").GetString());
            Assert.False(session.IsCancellationRequested);
            Assert.Equal(0UL, fixture.Metrics.Snapshot().TotalRequests);
            Assert.Equal(0UL, fixture.Metrics.Snapshot().FailedRequests);
            Assert.Equal(0UL, fixture.Proxy.KeepAlive.Snapshot().ActiveRequests);
        }
        finally
        {
            InternalSessions.Unregister("marker-1");
        }
    }
}
