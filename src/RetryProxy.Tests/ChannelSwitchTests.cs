using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.Metrics;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>
/// PRD-供应商管理 §4.2、§7、§12.4–12.6：统一注入当前 Key、访问限制，以及用假上游 A、B 验证的即时切换。
/// </summary>
public class ChannelSwitchTests
{
    private const string LocalToken = "local-token-fixture";

    private static Dictionary<string, string> Auth(string value) => new() { ["Authorization"] = value };

    private static Dictionary<string, string> Token() => Auth($"Bearer {LocalToken}");

    /// <summary>供应商（默认 Any）的一把 Key：a 为"主号"，b 为"群号"；keyId 为空表示供应商还没有 Key。</summary>
    private static ChannelSnapshot Key(string upstream, string keyId, string apiKey = "", string model = "", ClientType client = ClientType.Codex,
        double backoff = 0.05, ClaudeAuthMode authMode = ClaudeAuthMode.Bearer, Action<ProviderModels>? configure = null, string provider = "any")
    {
        var models = new ProviderModels { Model = model };
        configure?.Invoke(models);
        return new ChannelSnapshot
        {
            ClientType = client,
            ProviderId = provider,
            ProviderName = provider == "any" ? "Any" : provider,
            KeyId = keyId,
            KeyName = keyId switch { "a" => "主号", "b" => "群号", _ => keyId },
            ApiKey = apiKey,
            AuthMode = authMode,
            UpstreamBaseUrl = upstream,
            LocalToken = LocalToken,
            Models = models,
            MaxRetries = 3,
            TimeoutSeconds = 5.0,
            GenerationTimeoutSeconds = 30.0,
            TotalTimeoutSeconds = 20.0,
            BaseDelaySeconds = backoff,
            MaxDelaySeconds = backoff,
        };
    }

    /// <summary>假上游 A + 通道；通道启动后立即换上 <paramref name="snapshot"/>（参数是 A 的地址）。退避取区间上限，便于控制时序。</summary>
    private static Task<LifecycleProxy> Start(Func<HttpContext, Task> upstreamA, Func<string, ChannelSnapshot> snapshot, ClientType client = ClientType.Codex)
    {
        var config = Configs.Default();
        config.ClientType = client;
        return LifecycleProxy.StartAsync(upstreamA, config, proxy =>
        {
            proxy.WithRandomValue(() => 1.0);
            proxy.UpdateSnapshot(snapshot(config.UpstreamBaseUrl));
            return proxy;
        });
    }

    [Fact]
    public async Task ModelQueriesFollowKeySwitchWithoutEnteringUsage()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string models = "{\"data\":[{\"id\":\"model-b\"}]}";
        await using var upstreamB = await FakeUpstream.StartAsync(async context =>
        {
            Assert.Equal("Bearer sk-real-b", context.Request.Headers.Authorization.ToString());
            Assert.Equal("/v1/models?trace=1", context.Request.Path + context.Request.QueryString);
            await Upstream.Json(context, 200, models);
        });
        await using var fixture = await Start(async context =>
        {
            reached.TrySetResult();
            await Upstream.Pending(context);
        }, upstream => Key(upstream, "a", "sk-real-a"));
        using var client = TestClient.Create();
        var pending = client.GetStringAsync($"{fixture.Address}/v1/models?trace=1");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Proxy.UpdateSnapshot(Key(upstreamB.BaseUrl, "b", "sk-real-b")));
        Assert.Equal(models, await pending);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().TotalRequests);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().SuccessfulRequests);
        Assert.Equal(0UL, fixture.Metrics.Snapshot().RetryCount);
    }

    private static async Task<HttpStatusCode> Post(HttpClient client, string url, string body, Dictionary<string, string> headers)
    {
        using var response = await TestClient.Send(client, HttpMethod.Post, url, body, headers: headers);
        await response.Content.ReadAsStringAsync();
        return response.StatusCode;
    }

    // ---------------------------------------------------------------- 注入与透传（§7）

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public async Task AllManagedRequestsUseTheCurrentKeyRegardlessOfIncomingCredentials(ClientType clientType)
    {
        var seen = new ConcurrentQueue<(string Authorization, string ApiKey, string Body)>();
        await using var fixture = await Start(async context =>
        {
            seen.Enqueue((context.Request.Headers.Authorization.ToString(), context.Request.Headers["x-api-key"].ToString(), await Upstream.ReadBody(context)));
            Assert.False(context.Request.Headers.ContainsKey("api-key"));
            await Upstream.Json(context, 200, "{\"ok\":true}");
        }, upstream => Key(upstream, "a", "sk-real-a", "gpt-5.2", clientType), clientType);
        using var client = TestClient.Create();
        const string body = "{\"model\":\"gpt-4o\",\"input\":\"hi\"}";
        var url = $"{fixture.Address}/v1/{(clientType == ClientType.Claude ? "messages" : "responses")}";
        Assert.Equal(HttpStatusCode.OK, await Post(client, url, body, Token()));
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().ActiveRequests == 0);
        // 保活模板取注入前的请求头：内存里只有本地口令，没有真实 Key。
        Assert.Equal($"Bearer {LocalToken}", fixture.Proxy.KeepAlive.Template()!.Headers.Get("authorization"));
        Assert.Equal(HttpStatusCode.OK, await Post(client, url, body, new Dictionary<string, string> { ["x-api-key"] = LocalToken }));
        // 旧真实 Key、不匹配的口令、空凭据以及混合认证头都由代理替换，绝不发给当前供应商。
        Assert.Equal(HttpStatusCode.OK, await Post(client, url, body, Auth("Bearer sk-client")));
        Assert.Equal(HttpStatusCode.OK, await Post(client, url, body, Auth($"Bearer {LocalToken}-other")));
        Assert.Equal(HttpStatusCode.OK, await Post(client, url, body, new()));
        Assert.Equal(HttpStatusCode.OK, await Post(client, url, body, new()
        {
            ["Authorization"] = "Bearer sk-client", ["x-api-key"] = "sk-old-api-key", ["api-key"] = "sk-legacy-key",
        }));

        const string injectedBody = "{\"model\":\"gpt-5.2\",\"input\":\"hi\"}";
        Assert.Equal(6, seen.Count);
        Assert.All(seen, injected => Assert.Equal(("Bearer sk-real-a", string.Empty, injectedBody), injected));
        var logs = await fixture.CompletedLogs();
        Assert.Contains("，Any · 主号，模型改写 gpt-4o -> gpt-5.2", logs);
        Assert.Contains("模型 gpt-5.2 -> 未报告", logs);
        Assert.DoesNotContain("模型 gpt-4o", logs);
        Assert.DoesNotContain("客户端凭据", logs);
        var health = await client.GetStringAsync($"{fixture.Address}/_retry/health");
        foreach (var secret in new[] { "sk-real-a", LocalToken, "sk-client", "sk-old-api-key", "sk-legacy-key" })
        {
            Assert.DoesNotContain(secret, logs);
            Assert.DoesNotContain(secret, health);
        }
    }

    [Fact]
    public async Task ClaudeRequestsUseTheProviderAuthFormatAndRoleModels()
    {
        var seen = new ConcurrentQueue<(string Path, string Authorization, string ApiKey, string Beta, string Body)>();
        await using var fixture = await Start(async context =>
        {
            seen.Enqueue((context.Request.Path.ToString(), context.Request.Headers.Authorization.ToString(), context.Request.Headers["x-api-key"].ToString(),
                context.Request.Headers["anthropic-beta"].ToString(), await Upstream.ReadBody(context)));
            await Upstream.Json(context, 200, "{\"input_tokens\":1}");
        }, upstream => Key(upstream, "a", "sk-real-a", "glm-5", ClientType.Claude, authMode: ClaudeAuthMode.ApiKey,
            configure: models => models.Opus = new RoleModel { Model = "glm-5-plus", Context1M = true }), ClientType.Claude);
        using var client = TestClient.Create();
        var headers = new Dictionary<string, string>(Token()) { ["anthropic-beta"] = "context-1m-2025-08-07,oauth-2025-04-20" };
        Assert.Equal(HttpStatusCode.OK, await Post(client, $"{fixture.Address}/v1/messages/count_tokens", "{\"model\":\"claude-sonnet-4-5\",\"messages\":[]}", headers));
        Assert.Equal(HttpStatusCode.OK, await Post(client, $"{fixture.Address}/v1/messages?beta=true", "{\"model\":\"claude-opus-4-1\",\"messages\":[]}", headers));

        // count_tokens 也改写；Sonnet 未指定 → 主模型（未勾 1M，删掉 context-1m）；Opus 勾了 1M → 保留原头。
        Assert.Collection(seen,
            counted => Assert.Equal(("/v1/messages/count_tokens", string.Empty, "sk-real-a", "oauth-2025-04-20", "{\"model\":\"glm-5\",\"messages\":[]}"), counted),
            opus => Assert.Equal(("/v1/messages", string.Empty, "sk-real-a", "context-1m-2025-08-07,oauth-2025-04-20", "{\"model\":\"glm-5-plus\",\"messages\":[]}"), opus));
    }

    [Fact]
    public async Task ManagedRequestsWithoutAKeyAreAnsweredLocally()
    {
        var hits = 0;
        await using var fixture = await Start(context =>
        {
            Interlocked.Increment(ref hits);
            return Upstream.Json(context, 200, "{}");
        }, upstream => Key(upstream, string.Empty));
        using var client = TestClient.Create();
        using (var missing = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"input\":\"hi\"}", headers: Token()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
            using var error = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            Assert.Equal("no_provider_key", error.RootElement.GetProperty("error").GetProperty("type").GetString());
        }

        Assert.Equal(HttpStatusCode.Forbidden, await Post(client, $"{fixture.Address}/v1/responses", "{\"input\":\"hi\"}", Auth("Bearer sk-client")));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(client, $"{fixture.Address}/v1/responses", "{\"input\":\"hi\"}", new()));
        Assert.Equal(0, hits);
        var logs = await fixture.CompletedLogs();
        Assert.Contains("当前供应商“Any”没有 Key，未转发，向客户端返回 HTTP 403", logs);
        Assert.Equal(3UL, fixture.Metrics.Snapshot().FailedRequests);
    }

    [Fact]
    public async Task RequestsFromWebPagesOrForeignHostsAreRejected()
    {
        var hits = 0;
        await using var fixture = await LifecycleProxy.StartAsync(context =>
        {
            Interlocked.Increment(ref hits);
            return Upstream.Json(context, 200, "{}");
        }, Configs.Default());
        using var client = TestClient.Create();
        using var origin = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{}",
            headers: new Dictionary<string, string> { ["Origin"] = "https://evil.example" });
        Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        using var healthFromPage = await TestClient.Send(client, HttpMethod.Get, $"{fixture.Address}/_retry/health",
            headers: new Dictionary<string, string> { ["Origin"] = "null" });
        Assert.Equal(HttpStatusCode.Forbidden, healthFromPage.StatusCode);
        // DNS 重绑定：连的是本机端口，Host 却是外部域名。
        using var rebound = new HttpRequestMessage(HttpMethod.Get, $"{fixture.Address}/_retry/health");
        rebound.Headers.Host = $"rebind.example:{fixture.Port}";
        using (var response = await client.SendAsync(rebound))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var local = new HttpRequestMessage(HttpMethod.Get, $"{fixture.Address}/_retry/health");
        local.Headers.Host = $"LOCALHOST:{fixture.Port}";
        using (var response = await client.SendAsync(local))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(0, hits);
        var logs = string.Join("\n", fixture.DrainLogs());
        // 拒绝日志一分钟最多一条，不写请求里的原值。
        Assert.Single(Regex.Matches(logs, "已拒绝非本机客户端的请求"));
        Assert.Contains("已拒绝非本机客户端的请求：请求带有 Origin 头（来自网页），返回 HTTP 403", logs);
        Assert.DoesNotContain("evil.example", logs);
    }

    // ---------------------------------------------------------------- 即时切换（§4.2、§12.4）

    [Theory]
    [InlineData(ClientType.Claude, false)]
    [InlineData(ClientType.Codex, false)]
    [InlineData(ClientType.Claude, true)]
    [InlineData(ClientType.Codex, true)]
    public async Task ExistingManagedClientsFollowSwitchAndUndoWithoutChangingTheirSettings(ClientType clientType, bool oldCredentials)
    {
        var seen = new ConcurrentQueue<(string Provider, string Authorization, string Body)>();
        await using var upstreamB = await FakeUpstream.StartAsync(async context =>
        {
            seen.Enqueue(("b", context.Request.Headers.Authorization.ToString(), await Upstream.ReadBody(context)));
            await Upstream.Json(context, 200, "{\"ok\":true}");
        });
        ChannelSnapshot? snapshotA = null;
        await using var fixture = await Start(async context =>
        {
            seen.Enqueue(("a", context.Request.Headers.Authorization.ToString(), await Upstream.ReadBody(context)));
            await Upstream.Json(context, 200, "{\"ok\":true}");
        }, upstream => snapshotA = Key(upstream, "a", "sk-real-a", "model-a", clientType), clientType);
        using var first = TestClient.Create();
        using var second = TestClient.Create();
        using var third = TestClient.Create();
        var clients = new[] { first, second, third };
        var url = $"{fixture.Address}/v1/{(clientType == ClientType.Claude ? "messages" : "responses")}";
        const string body = "{\"model\":\"old-client-model\",\"input\":\"hi\"}";
        var headers = oldCredentials ? Auth("Bearer sk-old-window-key") : Token();

        // 三个已打开的窗口保留同一地址、旧凭据和旧模型；切换与撤销都只改代理快照，不重建客户端。
        async Task AssertRound(string provider, string apiKey, string model)
        {
            var statuses = await Task.WhenAll(clients.Select(client => Post(client, url, body, headers)));
            Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
            Assert.Equal(clients.Length, seen.Count);
            while (seen.TryDequeue(out var request))
                Assert.Equal((provider, $"Bearer {apiKey}", $"{{\"model\":\"{model}\",\"input\":\"hi\"}}"), request);
        }

        await AssertRound("a", "sk-real-a", "model-a");
        fixture.Proxy.UpdateSnapshot(Key(upstreamB.BaseUrl, "b", "sk-real-b", "model-b", clientType, provider: "other"));
        await AssertRound("b", "sk-real-b", "model-b");
        fixture.Proxy.UpdateSnapshot(snapshotA!);
        await AssertRound("a", "sk-real-a", "model-a");
    }

    [Theory]
    [InlineData("backoff", false)]
    [InlineData("response", false)]
    [InlineData("generation", false)]
    [InlineData("backoff", true)]
    [InlineData("response", true)]
    [InlineData("generation", true)]
    public async Task SwitchingResendsUndeliveredRequestsToTheNewKeyImmediately(string stage, bool oldCredentials)
    {
        var hitsA = 0;
        var seenB = new ConcurrentQueue<(string Authorization, string Target, string Body)>();
        await using var upstreamB = await FakeUpstream.StartAsync(async context =>
        {
            seenB.Enqueue((context.Request.Headers.Authorization.ToString(), $"{context.Request.Path}{context.Request.QueryString}", await Upstream.ReadBody(context)));
            await Upstream.EventStream(context, Configs.GeneratedStream);
        });
        await using var fixture = await Start(async context =>
        {
            Interlocked.Increment(ref hitsA);
            switch (stage)
            {
                case "backoff":
                    await Upstream.Json(context, 500, "{\"error\":{\"message\":\"busy\"}}");
                    break;
                case "response":
                    await Upstream.Pending(context);
                    break;
                default:
                    await Upstream.Begin(context, 200, "text/event-stream");
                    await Upstream.Chunk(context, "data: {\"type\":\"response.created\",\"response\":{\"output\":[]}}\n\n");
                    await Upstream.Pending(context);
                    break;
            }
        }, upstream => Key(upstream, "a", "sk-real-a", "model-a", backoff: 30.0));
        using var client = TestClient.Create(10.0);
        var request = TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses?trace=1", "{\"model\":\"gpt-4o\",\"stream\":true}",
            headers: oldCredentials ? Auth("Bearer sk-old-window-key") : Token());
        var phase = stage switch
        {
            "backoff" => RequestPhase.WaitingRetry,
            "response" => RequestPhase.WaitingResponse,
            _ => RequestPhase.WaitingGeneration,
        };
        await TestClock.WaitUntil(() => Volatile.Read(ref hitsA) == 1 && fixture.Metrics.Snapshot().Requests.Any(item => item.Phase == phase));

        var switchedAt = Stopwatch.StartNew();
        Assert.Equal(1, fixture.Proxy.UpdateSnapshot(Key($"{upstreamB.BaseUrl}/v1", "b", "sk-real-b", "model-b")));
        using var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("recovered", text);
        Assert.True(switchedAt.Elapsed < TimeSpan.FromSeconds(3), $"{stage}: {switchedAt.Elapsed}");
        Assert.Equal(1, Volatile.Read(ref hitsA));
        Assert.Equal(("Bearer sk-real-b", "/v1/responses?trace=1", "{\"model\":\"model-b\",\"stream\":true}"), Assert.Single(seenB));
        var logs = await fixture.CompletedLogs();
        Assert.Contains("改投 Any · 群号，未输出，立即重发（不计重试）", logs);
        Assert.Contains("，Any · 群号，模型改写 gpt-4o -> model-b", logs);
        Assert.DoesNotContain("sk-real", logs);
    }

    [Fact]
    public async Task RequestsAlreadyStreamingStayOnTheOldKey()
    {
        var hitsA = 0;
        var hitsB = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstreamB = await FakeUpstream.StartAsync(context =>
        {
            Interlocked.Increment(ref hitsB);
            return Upstream.EventStream(context, Configs.GeneratedStream);
        });
        await using var fixture = await Start(async context =>
        {
            Interlocked.Increment(ref hitsA);
            await Upstream.Begin(context, 200, "text/event-stream");
            await Upstream.Chunk(context, "data: {\"type\":\"response.created\",\"response\":{\"output\":[]}}\n\n"
                + "data: {\"type\":\"response.output_text.delta\",\"delta\":\"from-a\"}\n\n");
            await release.Task;
            await Upstream.Chunk(context, "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n");
        }, upstream => Key(upstream, "a", "sk-real-a"));
        using var client = TestClient.Create(10.0);
        using var response = await TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"model\":\"gpt-4o\",\"stream\":true}", headers: Token());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync();
        var received = new StringBuilder(Encoding.UTF8.GetString((await TestClient.NextChunk(stream))!));
        Assert.Contains("from-a", received.ToString());

        // 已开始输出：不算未输出的请求，切换后仍在 A 上收完。
        Assert.Equal(0, fixture.Proxy.UpdateSnapshot(Key(upstreamB.BaseUrl, "b", "sk-real-b")));
        release.SetResult();
        while (await TestClient.NextChunk(stream) is { } chunk)
        {
            received.Append(Encoding.UTF8.GetString(chunk));
        }

        Assert.Contains("response.completed", received.ToString());
        Assert.Equal((1, 0), (Volatile.Read(ref hitsA), Volatile.Read(ref hitsB)));
        var logs = await fixture.CompletedLogs();
        Assert.DoesNotContain("改投 ", logs);
    }

    [Fact]
    public async Task OldCredentialRequestsSwitchWithoutForwardingTheirCredentials()
    {
        var hitsA = 0;
        var seenB = new ConcurrentQueue<string>();
        await using var upstreamB = await FakeUpstream.StartAsync(context =>
        {
            seenB.Enqueue(context.Request.Headers.Authorization.ToString());
            return Upstream.Json(context, 200, "{\"from\":\"b\"}");
        });
        await using var fixture = await Start(context => Interlocked.Increment(ref hitsA) == 1
                ? Upstream.Json(context, 500, "{}")
                : Upstream.Json(context, 200, "{\"from\":\"a\"}"),
            upstream => Key(upstream, "a", "sk-real-a", backoff: 1.0));
        using var client = TestClient.Create(10.0);
        var url = $"{fixture.Address}/v1/responses";
        var request = TestClient.Send(client, HttpMethod.Post, url, "{\"input\":\"hi\"}", headers: Auth("Bearer sk-client"));
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().Requests.Any(item => item.Phase == RequestPhase.WaitingRetry));

        // 旧凭据不再透传；尚未输出的请求立即改投 B，并注入 B 的 Key。
        Assert.Equal(1, fixture.Proxy.UpdateSnapshot(Key(upstreamB.BaseUrl, "b", "sk-real-b", provider: "other")));
        using (var response = await request)
        {
            Assert.Equal("{\"from\":\"b\"}", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(1, Volatile.Read(ref hitsA));
        Assert.Equal("Bearer sk-real-b", Assert.Single(seenB));
        Assert.Equal(HttpStatusCode.OK, await Post(client, url, "{\"input\":\"hi\"}", Auth("Bearer sk-client")));
        Assert.Equal(2, seenB.Count);
        Assert.All(seenB, value => Assert.Equal("Bearer sk-real-b", value));
        var logs = await fixture.CompletedLogs();
        Assert.Contains("改投 ", logs);
        Assert.DoesNotContain("sk-client", logs);
    }

    [Fact]
    public async Task EditingTheCurrentKeyAppliesToTheNextAttemptWithoutInterruptingTheWait()
    {
        var hitsA = 0;
        var seenB = new ConcurrentQueue<string>();
        await using var upstreamB = await FakeUpstream.StartAsync(context =>
        {
            seenB.Enqueue(context.Request.Headers.Authorization.ToString());
            return Upstream.Json(context, 200, "{\"from\":\"b\"}");
        });
        await using var fixture = await Start(context =>
        {
            Interlocked.Increment(ref hitsA);
            return Upstream.Json(context, 500, "{}");
        }, upstream => Key(upstream, "a", "sk-real-a", backoff: 1.0));
        using var client = TestClient.Create(10.0);
        var request = TestClient.Send(client, HttpMethod.Post, $"{fixture.Address}/v1/responses", "{\"input\":\"hi\"}", headers: Token());
        await TestClock.WaitUntil(() => fixture.Metrics.Snapshot().Requests.Any(item => item.Phase == RequestPhase.WaitingRetry));

        // 同一把 Key 只改了地址与密钥：不算切换，本次退避照常等完，下一次尝试用新设置。
        Assert.Null(fixture.Proxy.UpdateSnapshot(Key(upstreamB.BaseUrl, "a", "sk-real-a2")));
        using var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"from\":\"b\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, Volatile.Read(ref hitsA));
        Assert.Equal("Bearer sk-real-a2", Assert.Single(seenB));
        var logs = await fixture.CompletedLogs();
        Assert.DoesNotContain("改投 ", logs);
    }
}
