using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Stats;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>压缩透传：原样转发 Accept-Encoding 与压缩流，代理自行解压解析。</summary>
public class CompressionPassThroughTests
{
    private const string ClientEncoding = "gzip, deflate, br, zstd";

    private static readonly string[] CompletedStream =
    {
        "event: ping\ndata: {\"type\":\"ping\"}\n\n",
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-test\",\"usage\":{\"input_tokens\":1200,\"output_tokens\":1}}}\n\n",
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}\n\n",
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":42}}\n\n",
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
    };

    /// <summary>按片段逐段压缩并刷新，模拟上游流式压缩；最后一段含压缩尾。</summary>
    internal static List<byte[]> CompressSegments(string coding, IEnumerable<string> parts, bool rawDeflate = false)
    {
        var output = new MemoryStream();
        Stream compressor = coding switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true),
            "deflate" when rawDeflate => new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true),
            "deflate" => new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true),
            "zstd" => new ZstdSharp.CompressionStream(output, 3, 0, true),
            _ => throw new ArgumentException(coding),
        };
        var segments = new List<byte[]>();
        var written = 0;
        void Take()
        {
            var all = output.ToArray();
            segments.Add(all[written..]);
            written = all.Length;
        }

        foreach (var part in parts)
        {
            compressor.Write(Encoding.UTF8.GetBytes(part));
            compressor.Flush();
            Take();
        }

        compressor.Dispose();
        Take();
        return segments;
    }

    internal static string Decompress(string coding, byte[] body)
    {
        using var input = new MemoryStream(body);
        using Stream decompressor = coding switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "deflate" => new ZLibStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            "zstd" => new ZstdSharp.DecompressionStream(input),
            _ => throw new ArgumentException(coding),
        };
        using var reader = new StreamReader(decompressor, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static ProxyConfig PassThroughConfig(bool passThrough = true)
    {
        var config = Configs.Default();
        config.ClientType = ClientType.Claude;
        config.MaxRetries = 0;
        config.TimeoutSeconds = 5.0;
        config.PassThroughCompression = passThrough;
        return config;
    }

    // ------------------------------------------------------------------ 解码器

    [Theory]
    [InlineData("gzip", false)]
    [InlineData("deflate", false)]
    [InlineData("deflate", true)]
    [InlineData("br", false)]
    [InlineData("zstd", false)]
    public void DecoderDecodesTinyFragmentsIncrementally(string coding, bool rawDeflate)
    {
        var events = Enumerable.Range(0, 40).Select(index => $"data: {{\"i\":{index},\"t\":\"{new string('x', index * 5)}\"}}\n\n").ToArray();
        var segments = CompressSegments(coding, events, rawDeflate);
        using var decoder = ContentDecoder.Create(coding)!;
        var decoded = new StringBuilder();
        var random = new Random(7);
        var afterFirstSegment = string.Empty;
        for (var segment = 0; segment < segments.Count; segment++)
        {
            var bytes = segments[segment];
            for (var offset = 0; offset < bytes.Length;)
            {
                var length = Math.Min(random.Next(1, 6), bytes.Length - offset);
                decoded.Append(Encoding.UTF8.GetString(decoder.Push(bytes.AsSpan(offset, length))));
                offset += length;
            }

            if (segment == 0)
            {
                afterFirstSegment = decoded.ToString();
            }
        }

        Assert.False(decoder.Failed);
        Assert.Equal(string.Concat(events), decoded.ToString());
        // 增量：第一段刷新后就能解出第一条事件，而不是等到流结束。
        Assert.Equal(events[0], afterFirstSegment);
    }

    [Fact]
    public void DecoderStopsOnCorruptDataAndRejectsUnsupportedCodings()
    {
        using var decoder = ContentDecoder.Create("gzip")!;
        Assert.True(decoder.Push("data: not gzip\n\n"u8).IsEmpty);
        Assert.True(decoder.Failed);
        Assert.True(decoder.Push(CompressSegments("gzip", new[] { "x" })[0]).IsEmpty);

        Assert.Null(ContentDecoder.Create(null));
        Assert.Null(ContentDecoder.Create(" identity "));
        Assert.Null(ContentDecoder.Create("compress"));
        Assert.Null(ContentDecoder.Create("gzip, br"));
        Assert.NotNull(ContentDecoder.Create(" GZIP "));
        Assert.NotNull(ContentDecoder.Create("x-gzip"));
        Assert.True(ContentDecoder.IsEncoded("compress"));
        Assert.False(ContentDecoder.IsEncoded("identity"));
        Assert.Null(ContentDecoder.DecodeAll("gzip", "broken"u8));
        Assert.Equal("plain"u8.ToArray(), ContentDecoder.DecodeAll(null, "plain"u8));
    }

    [Theory]
    [InlineData("gzip, deflate, br, zstd", true)]
    [InlineData("gzip;q=1.0, identity; q=0.5", true)]
    [InlineData("br", true)]
    [InlineData("gzip, compress", false)]
    [InlineData("*", false)]
    [InlineData("gzip, *;q=0.1", false)]
    public void AcceptEncodingIsForwardedOnlyWhenEveryCodingIsDecodable(string acceptEncoding, bool expected)
    {
        Assert.Equal(expected, ContentDecoder.AcceptsOnlySupported(acceptEncoding));
    }

    [Fact]
    public void StatsAndGenerationGateParseCompressedStreams()
    {
        var segments = CompressSegments("br", CompletedStream);
        var headers = new HeaderList();
        headers.Set("content-type", "text/event-stream");
        headers.Set("content-encoding", "br");
        var stats = new ResponseStats(headers, "/v1/messages", "claude-test");
        var gate = new GenerationGate(ContentDecoder.Create("br"));
        Assert.True(stats.IsApiEventStream);
        Assert.False(gate.Observe(segments[0]));
        var released = false;
        foreach (var segment in segments)
        {
            stats.Observe(segment, 0.1);
            released |= gate.Observe(segment);
        }

        stats.Finish(0.2);
        Assert.True(released);
        Assert.False(stats.Outcome!.Value.IsFailed);
        Assert.False(stats.MissingTerminalEvent);
        Assert.Equal(1242UL, stats.ContextTokens(true));
        Assert.Contains("，压缩 br", stats.LogFields());

        var broken = new ResponseStats(headers, "/v1/messages", "claude-test");
        broken.Observe(Encoding.UTF8.GetBytes(string.Concat(CompletedStream)), 0.1);
        broken.Finish(0.2);
        Assert.False(broken.MissingTerminalEvent);
        Assert.Null(broken.Outcome);
        Assert.Contains("压缩 br（解压失败）", broken.LogFields());
        Assert.True(new GenerationGate(ContentDecoder.Create("br")).Observe("not brotli"u8));
    }

    // ------------------------------------------------------------------ 转发

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    [InlineData("zstd")]
    public async Task CompressedStreamIsForwardedVerbatimAndStillParsed(string coding)
    {
        var segments = CompressSegments(coding, CompletedStream);
        string? seenEncoding = null;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            seenEncoding = context.Request.Headers.AcceptEncoding.ToString();
            await Upstream.Begin(context, 200, "text/event-stream", new Dictionary<string, string> { ["content-encoding"] = coding });
            foreach (var segment in segments.Take(segments.Count - 1))
            {
                await Upstream.Chunk(context, segment);
            }

            // 完成事件之后压缩尾迟到：代理不能在 message_stop 处切断，否则客户端解压报截断。
            await Task.Delay(150);
            await Upstream.Chunk(context, segments[^1]);
        }, PassThroughConfig());
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/messages",
            "{\"model\":\"claude-test\",\"stream\":true}", "application/json",
            new Dictionary<string, string> { ["accept-encoding"] = ClientEncoding });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(coding, string.Join(",", response.Content.Headers.ContentEncoding));
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(segments.SelectMany(segment => segment).ToArray(), body);
        Assert.Equal(string.Concat(CompletedStream), Decompress(coding, body));
        Assert.Equal(ClientEncoding, seenEncoding);

        var logs = await fixture.CompletedLogs();
        Assert.Contains("HTTP 200", logs);
        Assert.Contains("输入/输出 1200/42 token", logs);
        Assert.Contains($"压缩 {coding}", logs);
        Assert.DoesNotContain("未收到完成事件", logs);
        var snapshot = fixture.Metrics.Snapshot();
        Assert.Equal(1UL, snapshot.SuccessfulRequests);
        Assert.Equal(0UL, snapshot.FailedRequests);
    }

    [Theory]
    [InlineData(false, ClientEncoding)]
    [InlineData(true, "gzip, compress")]
    public async Task UpstreamIsAskedForIdentityWhenPassThroughIsOffOrCodingIsUnsupported(bool passThrough, string clientEncoding)
    {
        string? seenEncoding = null;
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            seenEncoding = context.Request.Headers.AcceptEncoding.ToString();
            await Upstream.EventStream(context, string.Concat(CompletedStream));
        }, PassThroughConfig(passThrough));
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/messages",
            "{\"model\":\"claude-test\",\"stream\":true}", "application/json",
            new Dictionary<string, string> { ["accept-encoding"] = clientEncoding });
        Assert.Equal(string.Concat(CompletedStream), await response.Content.ReadAsStringAsync());
        Assert.Equal("identity", seenEncoding);
    }

    [Fact]
    public async Task GenerationGateStillWaitsAndRetriesOnCompressedHeartbeats()
    {
        var hits = 0;
        var retried = CompressSegments("gzip", CompletedStream);
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            var attempt = Interlocked.Increment(ref hits) - 1;
            await Upstream.Begin(context, 200, "text/event-stream", new Dictionary<string, string> { ["content-encoding"] = "gzip" });
            if (attempt > 0)
            {
                foreach (var segment in retried)
                {
                    await Upstream.Chunk(context, segment);
                }

                return;
            }

            // 第一次只发压缩后的心跳：代理若不解压就会把二进制当成可转发内容，不会重试。
            var heartbeats = CompressSegments("gzip", Enumerable.Repeat("event: ping\ndata: {\"type\":\"ping\"}\n\n", 200));
            foreach (var segment in heartbeats.Take(heartbeats.Count - 1))
            {
                try
                {
                    await Upstream.Chunk(context, segment);
                    await Task.Delay(20, context.RequestAborted);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }, PassThroughGenerationConfig());
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/messages",
            "{\"model\":\"claude-test\",\"stream\":true}", "application/json",
            new Dictionary<string, string> { ["accept-encoding"] = ClientEncoding });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(string.Concat(CompletedStream), Decompress("gzip", body));
        var logs = await fixture.CompletedLogs();
        Assert.Equal(2, hits);
        Assert.Contains("等待生成达到 0.25秒", logs);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().RetryCount);
        Assert.Equal(1UL, fixture.Metrics.Snapshot().SuccessfulRequests);
    }

    [Fact]
    public async Task UndecodableBodyIsForwardedIntactWithoutAbortingTheClient()
    {
        var garbage = Encoding.UTF8.GetBytes(string.Concat(CompletedStream));
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            await Upstream.Begin(context, 200, "text/event-stream", new Dictionary<string, string> { ["content-encoding"] = "gzip" });
            await Upstream.Chunk(context, garbage);
        }, PassThroughConfig());
        using var client = TestClient.Create();
        var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/messages",
            "{\"model\":\"claude-test\",\"stream\":true}", "application/json",
            new Dictionary<string, string> { ["accept-encoding"] = ClientEncoding });
        Assert.Equal(garbage, await TestClient.TryReadAll(response));
        var logs = await fixture.CompletedLogs();
        Assert.Contains("压缩 gzip（解压失败）", logs);
        Assert.DoesNotContain("未收到完成事件", logs);
    }

    private static ProxyConfig PassThroughGenerationConfig()
    {
        var config = Configs.Generation();
        config.ClientType = ClientType.Claude;
        config.PassThroughCompression = true;
        return config;
    }

    // ------------------------------------------------------------------ 配置

    [Fact]
    public void PassThroughCompressionIsPerRouteAndRoundTrips()
    {
        var config = TestConfigs.BuiltinWithProviders();
        config.Routes[1].PassThroughCompression = true;
        var json = ProxyConfigJson.ToCanonicalJson(config);
        var (loaded, _) = ProxyConfigJson.Parse(json);
        Assert.False(loaded.Routes[0].PassThroughCompression);
        Assert.True(loaded.Routes[1].PassThroughCompression);
        Assert.True(loaded.RuntimeConfigFor(loaded.Routes[1].Id).PassThroughCompression);
        Assert.False(loaded.RuntimeConfigFor(loaded.Routes[0].Id).PassThroughCompression);
        var toggled = loaded.Routes[0].Clone();
        toggled.PassThroughCompression = true;
        Assert.NotEqual(loaded.Routes[0], toggled);

        // 旧配置没有该字段：默认关闭。
        var stripped = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var route in stripped["routes"]!.AsArray())
        {
            Assert.NotNull(route!["pass_through_compression"]);
            route.AsObject().Remove("pass_through_compression");
        }

        var (legacy, _) = ProxyConfigJson.Parse(stripped.ToJsonString());
        Assert.All(legacy.Routes, route => Assert.False(route.PassThroughCompression));
    }
}
