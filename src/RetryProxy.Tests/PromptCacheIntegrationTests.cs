using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RetryProxy.Core.Config;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>对应 tests/prompt_cache.rs：走真实 Kestrel 的缓存标识、兼容重发与用量统计集成用例。</summary>
public class PromptCacheIntegrationTests
{
    private const string Answer = "{\"model\":\"gpt-test\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"fixture-answer\"}]}],\"usage\":{\"input_tokens\":2048,\"output_tokens\":8,\"input_tokens_details\":{\"cached_tokens\":1024}}}";
    private const string Rejection = "{\"error\":{\"code\":\"unknown_parameter\",\"param\":\"prompt_cache_key\",\"message\":\"fixture-private-error\"}}";

    private static Task<LifecycleProxy> Start(Func<HttpContext, Task> upstream, double totalTimeout, long retries)
    {
        var config = Configs.Default();
        config.MaxRetries = retries;
        config.TimeoutSeconds = 3.0;
        config.TotalTimeoutSeconds = totalTimeout;
        config.BaseDelaySeconds = 0.0;
        config.MaxDelaySeconds = 0.0;
        return LifecycleProxy.StartAsync(upstream, config);
    }

    private static HttpClient Client() => TestClient.Create(5);

    private static Task<HttpResponseMessage> Send(HttpClient client, LifecycleProxy proxy, string body)
    {
        return TestClient.Send(client, HttpMethod.Post, $"{proxy.Address}/v1/responses", body, "application/json", new Dictionary<string, string>
        {
            ["authorization"] = "Bearer fixture-secret",
            ["thread_id"] = "fixture-thread",
        });
    }

    private static async Task<string> Text(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    [Fact]
    public async Task ChannelCacheCountsClaudeAndOtherModelsWithoutChangingReplies()
    {
        const string claudeRequest = "{\"model\":\"claude-test\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"fixture-private-input\",\"cache_control\":{\"type\":\"ephemeral\"}}]}]}";
        const string claudeReply =
            "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-test\",\"content\":[],\"usage\":{\"input_tokens\":100,\"output_tokens\":0,\"cache_read_input_tokens\":800,\"cache_creation_input_tokens\":100}}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"fixture-answer\"}}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"input_tokens\":0,\"output_tokens\":5,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}\n\n"
            + "data: {\"type\":\"message_stop\"}\n\n";
        const string otherReply = "{\"model\":\"other-model\",\"choices\":[{\"message\":{\"content\":\"fixture-answer\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":2000,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":1000}}}";
        string? claudeSeen = null;
        await using var proxy = await Start(async context =>
        {
            if (context.Request.Path == "/v1/messages")
            {
                claudeSeen = await Upstream.ReadBody(context);
                await Upstream.EventStream(context, claudeReply);
            }
            else if (context.Request.Path == "/v1/chat/completions")
            {
                await Upstream.Json(context, 200, otherReply);
            }
            else
            {
                await Upstream.Text(context, 404, "unexpected");
            }
        }, 5.0, 0);
        using var client = Client();
        foreach (var (path, body, expected) in new[]
                 {
                     ("/v1/messages", claudeRequest, claudeReply),
                     ("/v1/chat/completions", "{\"model\":\"other-model\",\"messages\":[]}", otherReply),
                 })
        {
            var response = await TestClient.Send(client, HttpMethod.Post, $"{proxy.Address}{path}", body, "application/json");
            Assert.Equal(expected, await Text(response));
        }

        Assert.Equal(claudeRequest, claudeSeen);
        var logs = await proxy.CompletedLogs();
        var snapshot = proxy.Metrics.Snapshot();
        Assert.Equal(2UL, snapshot.Cache.MeasuredRequests);
        Assert.Equal(3000UL, snapshot.Cache.InputTokens);
        Assert.Equal(1800UL, snapshot.Cache.CachedTokens);
        Assert.Equal(100UL, snapshot.Cache.CacheCreationTokens);
        Assert.Equal(1UL, snapshot.Cache.CacheCreationMeasuredRequests);
        Assert.Equal(60.0, snapshot.Cache.HitRatePercent());
        Assert.Equal(1000UL, snapshot.Cache.RecentRequests[0].TotalInputTokens());
        Assert.Equal(0UL, snapshot.GptCache.MeasuredRequests);
        Assert.True(logs.Contains("缓存命中率 80.0%"), logs);
        Assert.DoesNotContain("fixture-private-input", logs);
        using var health = await TestClient.Health(client, proxy.Address);
        var metrics = health.RootElement.GetProperty("metrics");
        Assert.Equal(60.0, metrics.GetProperty("cache_hit_rate_percent").GetDouble());
        Assert.Equal("excludes_cached", metrics.GetProperty("cache").GetProperty("recent_requests")[0].GetProperty("input_accounting").GetString());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("gpt_cache_hit_rate_percent").ValueKind);
    }

    [Fact]
    public async Task ClaudeCacheDistinguishesCreationOnlyMissingUsageAndFailedReplies()
    {
        foreach (var (body, measured, unmeasured, zeroHit) in new (string, ulong, ulong, ulong)[]
                 {
                     ("{\"model\":\"claude-test\",\"content\":[{\"type\":\"text\",\"text\":\"fixture-answer\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":0,\"output_tokens\":5,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":1000}}", 1, 0, 1),
                     ("{\"model\":\"claude-test\",\"content\":[{\"type\":\"text\",\"text\":\"fixture-answer\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":100,\"output_tokens\":5,\"cache_read_input_tokens\":800}}", 0, 1, 0),
                     ("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"},\"usage\":{\"input_tokens\":100,\"output_tokens\":0,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}", 0, 0, 0),
                 })
        {
            await using var proxy = await Start(context => Upstream.Json(context, 200, body), 5.0, 0);
            using var client = Client();
            var response = await TestClient.Send(client, HttpMethod.Post, $"{proxy.Address}/v1/messages", "{\"model\":\"claude-test\",\"messages\":[]}", "application/json");
            await response.Content.ReadAsByteArrayAsync();
            await proxy.CompletedLogs();
            var snapshot = proxy.Metrics.Snapshot();
            Assert.Equal(measured, snapshot.Cache.MeasuredRequests);
            Assert.Equal(unmeasured, snapshot.Cache.UnmeasuredRequests);
            Assert.Equal(zeroHit, snapshot.Cache.ZeroHitRequests);
            Assert.Equal((int)(measured + unmeasured), snapshot.Cache.RecentRequests.Count);
            Assert.Equal(0UL, snapshot.GptCache.MeasuredRequests);
            if (measured == 1)
            {
                Assert.Equal(1000UL, snapshot.Cache.InputTokens);
                Assert.Equal(1000UL, snapshot.Cache.CacheCreationTokens);
                Assert.Equal(0.0, snapshot.Cache.HitRatePercent());
            }
            else
            {
                Assert.Null(snapshot.Cache.HitRatePercent());
            }
        }
    }

    [Fact]
    public async Task StableKeyPreservesPayloadAndIsVisibleInLogsAndWeightedMetrics()
    {
        var seen = new List<string>();
        var headers = new List<(string? Authorization, string? ThreadId)>();
        await using var proxy = await Start(async context =>
        {
            lock (headers)
            {
                headers.Add((context.Request.Headers["authorization"].ToString(), context.Request.Headers["thread_id"].ToString()));
            }

            var body = await Upstream.ReadBody(context);
            lock (seen)
            {
                seen.Add(body);
            }

            await Upstream.Json(context, 200, Answer);
        }, 5.0, 0);
        var bodies = new[]
        {
            "{ \"model\":\"gpt-test\", \"input\":\"private-one\", \"number\":1.00e+01 }",
            "{ \"model\":\"gpt-test\", \"input\":\"private-two\", \"number\":1.00e+01 }",
        };
        using var client = Client();
        foreach (var body in bodies)
        {
            Assert.Equal(Answer, await Text(await Send(client, proxy, body)));
        }

        var logs = await proxy.CompletedLogs();
        foreach (var (authorization, threadId) in headers)
        {
            Assert.Equal("Bearer fixture-secret", authorization);
            Assert.Equal("fixture-thread", threadId);
        }

        Assert.Equal(2, seen.Count);
        using var first = JsonDocument.Parse(seen[0]);
        using var second = JsonDocument.Parse(seen[1]);
        var key = first.RootElement.GetProperty("prompt_cache_key").GetString()!;
        Assert.Equal(key, second.RootElement.GetProperty("prompt_cache_key").GetString());
        var addition = $",\"prompt_cache_key\":{JsonSerializer.Serialize(key)}";
        for (var index = 0; index < bodies.Length; index++)
        {
            Assert.Equal(bodies[index], seen[index].Replace(addition, string.Empty));
        }

        Assert.True(logs.Contains("缓存标识：代理已补全"), logs);
        Assert.True(logs.Contains("缓存命中率 50.0%"), logs);
        foreach (var privateText in new[] { "fixture-secret", "private-one", "fixture-thread", key })
        {
            Assert.DoesNotContain(privateText, logs);
        }

        var healthText = await client.GetStringAsync($"{proxy.Address}/_retry/health");
        using var health = JsonDocument.Parse(healthText);
        var metrics = health.RootElement.GetProperty("metrics");
        var gptCache = metrics.GetProperty("gpt_cache");
        Assert.Equal(2, gptCache.GetProperty("added_key_requests").GetInt32());
        Assert.Equal(2, gptCache.GetProperty("measured_requests").GetInt32());
        Assert.Equal(4096, gptCache.GetProperty("input_tokens").GetInt32());
        Assert.Equal(50.0, metrics.GetProperty("gpt_cache_hit_rate_percent").GetDouble());
        var recent = gptCache.GetProperty("recent_requests");
        Assert.Equal(2, recent.GetArrayLength());
        Assert.Equal(1024, recent[1].GetProperty("cached_tokens").GetInt32());
        Assert.Equal(2048, recent[1].GetProperty("input_tokens").GetInt32());
        Assert.Equal("代理已补全", recent[1].GetProperty("cache_key_status").GetString());
        Assert.True(recent[1].GetProperty("completed_at_unix_ms").GetInt64() > 0);
        Assert.Contains(recent[1].GetProperty("request_id").GetString()!, logs);
        foreach (var privateText in new[] { "fixture-secret", "private-one", "fixture-thread", key })
        {
            Assert.DoesNotContain(privateText, healthText);
        }
    }

    [Fact]
    public async Task ExplicitRejectionResendsOriginalOnceAndRemembersCompatibility()
    {
        var seen = new List<string>();
        await using var proxy = await Start(async context =>
        {
            var body = await Upstream.ReadBody(context);
            bool amended;
            using (var document = JsonDocument.Parse(body))
            {
                amended = document.RootElement.TryGetProperty("prompt_cache_key", out _);
            }

            lock (seen)
            {
                seen.Add(body);
            }

            if (amended)
            {
                await Upstream.Json(context, 400, Rejection);
            }
            else
            {
                await Upstream.Json(context, 200, Answer);
            }
        }, 5.0, 0);
        const string body = "{ \"model\":\"gpt-test\", \"input\":\"private-question\" }";
        using var client = Client();
        for (var round = 0; round < 2; round++)
        {
            Assert.Equal(Answer, await Text(await Send(client, proxy, body)));
        }

        var logs = await proxy.CompletedLogs();
        Assert.Equal(3, seen.Count);
        Assert.Equal(body, seen[1]);
        Assert.Equal(body, seen[2]);
        var snapshot = proxy.Metrics.Snapshot();
        Assert.Equal(2UL, snapshot.TotalRequests);
        Assert.Equal(2UL, snapshot.SuccessfulRequests);
        Assert.Equal(0UL, snapshot.FailedRequests);
        Assert.Equal(0UL, snapshot.RetryCount);
        Assert.Equal(1UL, snapshot.GptCache.CompatibilityFallbacks);
        Assert.Equal(1UL, snapshot.GptCache.UnsupportedKeyRequests);
        Assert.True(logs.Contains("兼容重发一次"), logs);
        Assert.DoesNotContain("fixture-private-error", logs);
        Assert.DoesNotContain("private-question", logs);
    }

    [Fact]
    public async Task SuppliedKeysAndUnrelatedValidationErrorsNeverTriggerCompatibilityRetries()
    {
        foreach (var (body, error) in new[]
                 {
                     ("{ \"model\":\"gpt-test\", \"prompt_cache_key\":\"client-private-key\" }", Rejection),
                     ("{ \"model\":\"gpt-test\", \"input\":\"question\" }", "{\"error\":{\"code\":\"invalid_responses_request\",\"message\":\"invalid codex request\"}}"),
                 })
        {
            var seen = new List<string>();
            await using var proxy = await Start(async context =>
            {
                var incoming = await Upstream.ReadBody(context);
                lock (seen)
                {
                    seen.Add(incoming);
                }

                await Upstream.Json(context, 400, error);
            }, 5.0, 3);
            using var client = Client();
            var response = await Send(client, proxy, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(error, await Text(response));
            await proxy.CompletedLogs();
            Assert.Single(seen);
            if (body.Contains("client-private-key"))
            {
                Assert.Equal(body, seen[0]);
            }

            Assert.Equal(0UL, proxy.Metrics.Snapshot().GptCache.CompatibilityFallbacks);
        }
    }

    [Fact]
    public async Task OversizedChunkedErrorIsReplayedWithoutTruncationOrResending()
    {
        var large = Encoding.UTF8.GetBytes(Rejection + new string(' ', 65536));
        await using var proxy = await Start(async context =>
        {
            await Upstream.Begin(context, 400, "application/json");
            await Upstream.Chunk(context, large[..100]);
            await Upstream.Chunk(context, large[100..]);
        }, 5.0, 3);
        using var client = Client();
        var response = await Send(client, proxy, "{\"model\":\"gpt-test\",\"input\":\"question\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(large, await response.Content.ReadAsByteArrayAsync());
        await proxy.CompletedLogs();
        Assert.Equal(0UL, proxy.Metrics.Snapshot().GptCache.CompatibilityFallbacks);
        Assert.Equal(0UL, proxy.Metrics.Snapshot().RetryCount);
    }

    [Fact]
    public async Task NonJsonErrorStartsForwardingBeforeTheUpstreamCloses()
    {
        var finish = new Notify();
        await using var proxy = await Start(async context =>
        {
            await Upstream.Begin(context, 400, "text/plain");
            await Upstream.Chunk(context, "initial-error");
            await finish.Notified();
            await Upstream.Chunk(context, "-end");
        }, 5.0, 0);
        using var client = Client();
        var sending = Send(client, proxy, "{\"model\":\"gpt-test\"}");
        var response = await sending.WaitAsync(TimeSpan.FromSeconds(2));
        var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal("initial-error", Encoding.UTF8.GetString((await TestClient.NextChunk(stream))!));
        finish.NotifyOne();
        Assert.Equal("-end", Encoding.UTF8.GetString((await TestClient.NextChunk(stream))!));
        Assert.Null(await TestClient.NextChunk(stream));
        await proxy.CompletedLogs();
    }

    [Fact]
    public async Task ErrorInspectionRespectsTheTotalDeadline()
    {
        await using var proxy = await Start(async context =>
        {
            await Upstream.Begin(context, 400, "application/json");
            await Upstream.Chunk(context, "{\"error\":");
            await Upstream.Pending(context);
        }, 0.15, 3);
        using var client = Client();
        var response = await Send(client, proxy, "{\"model\":\"gpt-test\"}");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await response.Content.ReadAsByteArrayAsync();
        await proxy.CompletedLogs();
        var snapshot = proxy.Metrics.Snapshot();
        Assert.Equal(1UL, snapshot.FailedRequests);
        Assert.Equal(0UL, snapshot.GptCache.CompatibilityFallbacks);
        Assert.Equal(0UL, snapshot.ActiveRequests);
    }

    [Fact]
    public async Task StreamBytesArePreservedAndUsageIsCountedOnce()
    {
        var events = $"data: {{\"type\":\"response.output_text.delta\",\"delta\":\"fixture-answer\"}}\n\ndata: {{\"type\":\"response.completed\",\"response\":{Answer}}}\n\n";
        await using var proxy = await Start(context => Upstream.EventStream(context, events), 5.0, 0);
        using var client = Client();
        var response = await Send(client, proxy, "{\"model\":\"gpt-test\",\"input\":\"question\",\"stream\":true}");
        Assert.Equal(events, await Text(response));
        await proxy.CompletedLogs();
        Assert.Equal(1UL, proxy.Metrics.Snapshot().GptCache.MeasuredRequests);
        Assert.Equal(50.0, proxy.Metrics.Snapshot().GptCache.HitRatePercent());
    }

    [Fact]
    public async Task MissingUsageAndFailedStreamsDoNotCountAsZeroCacheHits()
    {
        foreach (var (body, expectedUnmeasured) in new (string, ulong)[]
                 {
                     ("{\"model\":\"gpt-test\",\"status\":\"completed\",\"usage\":{\"input_tokens\":2048,\"output_tokens\":8}}", 1),
                     ("{\"model\":\"gpt-test\",\"status\":\"failed\",\"error\":{\"code\":\"server_error\"},\"usage\":{\"input_tokens\":2048,\"output_tokens\":8,\"input_tokens_details\":{\"cached_tokens\":0}}}", 0),
                 })
        {
            await using var proxy = await Start(context => Upstream.Json(context, 200, body), 5.0, 0);
            using var client = Client();
            var response = await Send(client, proxy, "{\"model\":\"gpt-test\"}");
            await response.Content.ReadAsByteArrayAsync();
            await proxy.CompletedLogs();
            Assert.Equal(0UL, proxy.Metrics.Snapshot().GptCache.MeasuredRequests);
            Assert.Equal(0UL, proxy.Metrics.Snapshot().GptCache.ZeroHitRequests);
            var cache = proxy.Metrics.Snapshot().GptCache;
            Assert.Equal(expectedUnmeasured, cache.UnmeasuredRequests);
            Assert.Equal((int)expectedUnmeasured, cache.RecentRequests.Count);
            if (cache.RecentRequests.Count > 0)
            {
                var latest = cache.RecentRequests[^1];
                Assert.Equal(2048UL, latest.InputTokens);
                Assert.Null(latest.CachedTokens);
                Assert.Null(latest.HitRatePercent());
            }
        }
    }
}
