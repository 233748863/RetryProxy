using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Proxy;
using RetryProxy.Tests.Support;
using Xunit;
using Pipeline = RetryProxy.Core.Proxy.RetryProxy;

namespace RetryProxy.Tests;

/// <summary>PRD-供应商管理 §6、§7、§9 的纯规则：地址拼接、模型映射、认证头改名、快照与切换信号、保活凭据。</summary>
public class ChannelRulesTests
{
    // ---------------------------------------------------------------- 地址规则（§6.1）

    [Theory]
    [InlineData(ClientType.Claude, "https://anyrouter.top", "/v1/messages", "beta=true", "https://anyrouter.top/v1/messages?beta=true")]
    [InlineData(ClientType.Claude, "https://relay.example/api/", "/v1/messages/count_tokens", "", "https://relay.example/api/v1/messages/count_tokens")]
    [InlineData(ClientType.Codex, "https://anyrouter.top", "/v1/responses", "", "https://anyrouter.top/v1/responses")]
    [InlineData(ClientType.Codex, "https://anyrouter.top/", "/v1/responses", "", "https://anyrouter.top/v1/responses")]
    [InlineData(ClientType.Codex, "https://x666.me/v1", "/v1/responses", "", "https://x666.me/v1/responses")]
    [InlineData(ClientType.Codex, "https://new.sharedchat.cc/codex", "/v1/responses", "", "https://new.sharedchat.cc/codex/responses")]
    [InlineData(ClientType.Codex, "https://anyrouter.top", "/responses", "", "https://anyrouter.top/v1/responses")]
    [InlineData(ClientType.Codex, "https://anyrouter.top", "/v1", "", "https://anyrouter.top/v1/")]
    [InlineData(ClientType.Codex, "https://anyrouter.top", "/v1beta/models", "a=1", "https://anyrouter.top/v1/v1beta/models?a=1")]
    public void UpstreamTargetsFollowTheClientAddressRules(ClientType client, string baseUrl, string path, string query, string expected)
    {
        Assert.Equal(expected, Pipeline.BuildTargetUrl(client, baseUrl, path, query));
    }

    [Fact]
    public void ClientAddressesAddV1OnlyForCodex()
    {
        Assert.Equal("http://127.0.0.1:18080/v1", UrlRules.ClientBaseUrl(ClientType.Codex, "http://127.0.0.1:18080/"));
        Assert.Equal("http://127.0.0.1:18081", UrlRules.ClientBaseUrl(ClientType.Claude, "http://127.0.0.1:18081"));
        Assert.Equal("https://anyrouter.top/v1", UrlRules.CodexApiRoot("https://anyrouter.top/"));
        Assert.Equal("https://x666.me/v1", UrlRules.CodexApiRoot("https://x666.me/v1/"));
    }

    // ---------------------------------------------------------------- 模型映射（§6.2）

    private static ChannelSnapshot Claude(Action<ProviderModels>? configure = null, KeyModelOverride? custom = null)
    {
        var models = new ProviderModels { Model = "glm-5" };
        configure?.Invoke(models);
        return new ChannelSnapshot { ClientType = ClientType.Claude, ApiKey = "sk-a", Models = models, ModelOverride = custom };
    }

    private static HeaderList Headers(params (string Name, string Value)[] entries)
    {
        var headers = new HeaderList();
        foreach (var (name, value) in entries)
        {
            headers.Append(name, value);
        }

        return headers;
    }

    private static HeaderList Json(string? beta = null) => beta is null
        ? Headers(("content-type", "application/json"))
        : Headers(("content-type", "application/json"), ("anthropic-beta", beta));

    private static (string Body, ModelRewriter.Result Result) Rewrite(ClientType client, ChannelSnapshot snapshot, HeaderList headers, string body)
    {
        var result = ModelRewriter.Apply(client, snapshot, headers, Encoding.UTF8.GetBytes(body));
        return (Encoding.UTF8.GetString(result.Body.Span), result);
    }

    [Fact]
    public void ClaudeMapsRolesByKeywordAndFallsBackToTheMainModel()
    {
        var snapshot = Claude(models =>
        {
            models.Opus = new RoleModel { Model = "opus-target", Context1M = true };
            models.Haiku = new RoleModel { Model = "haiku-target" };
        });
        Assert.Equal(new ModelRewriter.ClaudeRole("opus-target", true), ModelRewriter.ClaudeTarget(snapshot, "claude-opus-4-1"));
        Assert.Equal(new ModelRewriter.ClaudeRole("haiku-target", false), ModelRewriter.ClaudeTarget(snapshot, "Claude-HAIKU-4-5"));
        // Sonnet 没单独指定、关键字都不匹配：都用主模型。
        Assert.Equal(new ModelRewriter.ClaudeRole("glm-5", false), ModelRewriter.ClaudeTarget(snapshot, "claude-sonnet-4-5"));
        Assert.Equal(new ModelRewriter.ClaudeRole("glm-5", false), ModelRewriter.ClaudeTarget(snapshot, "my-model"));

        // Key 的模型覆盖代替主模型和主模型的 1M，单独指定了模型的角色不受影响。
        var custom = Claude(models => models.Opus = new RoleModel { Model = "opus-target" }, new KeyModelOverride { Model = "kimi-k2", Context1M = true });
        Assert.Equal(new ModelRewriter.ClaudeRole("kimi-k2", true), ModelRewriter.ClaudeTarget(custom, "claude-sonnet-4-5"));
        Assert.Equal(new ModelRewriter.ClaudeRole("opus-target", false), ModelRewriter.ClaudeTarget(custom, "claude-opus-4-1"));

        // 供应商没设模型：不改写。
        var empty = Claude(models => models.Model = string.Empty);
        Assert.Null(ModelRewriter.ClaudeTarget(empty, "claude-opus-4-1"));
        var (body, result) = Rewrite(ClientType.Claude, empty, Json("context-1m-2025-08-07"), "{\"model\":\"claude-opus-4-1\"}");
        Assert.Equal("{\"model\":\"claude-opus-4-1\"}", body);
        Assert.Null(result.From);
    }

    [Fact]
    public void ClaudeDropsTheContext1MBetaOnlyWhenTheTargetRoleLacksIt()
    {
        var snapshot = Claude(models => models.Opus = new RoleModel { Model = "opus-target", Context1M = true });
        var headers = Json("context-1m-2025-08-07, fine-grained-tool-streaming-2025-05-14");
        var (body, result) = Rewrite(ClientType.Claude, snapshot, headers, "{\"model\":\"claude-sonnet-4-5\",\"max_tokens\":1}");
        Assert.Equal("{\"model\":\"glm-5\",\"max_tokens\":1}", body);
        Assert.Equal(("claude-sonnet-4-5", "glm-5"), (result.From, result.To));
        Assert.Equal("fine-grained-tool-streaming-2025-05-14", headers.Get("anthropic-beta"));

        headers = Json("context-1m-2025-08-07");
        Rewrite(ClientType.Claude, snapshot, headers, "{\"model\":\"claude-sonnet-4-5\"}");
        Assert.False(headers.Contains("anthropic-beta"));

        // 目标角色勾了 1M：原有的头保留；只删不加。
        headers = Json("context-1m-2025-08-07");
        Rewrite(ClientType.Claude, snapshot, headers, "{\"model\":\"claude-opus-4-1\"}");
        Assert.Equal("context-1m-2025-08-07", headers.Get("anthropic-beta"));
        headers = Json();
        Rewrite(ClientType.Claude, snapshot, headers, "{\"model\":\"claude-opus-4-1\"}");
        Assert.False(headers.Contains("anthropic-beta"));
    }

    [Fact]
    public void RewritingReplacesOnlyTheTopLevelModelBytes()
    {
        var original = " {\r\n  \"metadata\" : {\"model\":\"nested\"},\n  \"model\" :\t\"claude-\\u006fpus-4-1\" ,"
            + " \"messages\":[{\"model\":\"x\",\"content\":\"\\u4e00 1.2300e+04\"}], \"model\":\"claude-opus-4-1\" }\n";
        var (body, result) = Rewrite(ClientType.Claude, Claude(), Json(), original);
        var expected = original.Replace("\"claude-\\u006fpus-4-1\"", "\"glm-5\"").Replace("\"model\":\"claude-opus-4-1\"", "\"model\":\"glm-5\"");
        Assert.Equal(expected, body);
        Assert.Equal(("claude-opus-4-1", "glm-5"), (result.From, result.To));

        // 目标模型按 JSON 字符串转义写入。
        var (escaped, _) = Rewrite(ClientType.Claude, Claude(models => models.Model = "模型\"x"), Json(), "{\"model\":\"claude-opus-4-1\"}");
        Assert.Equal("模型\"x", JsonDocument.Parse(escaped).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void BodiesThatCannotBeEditedStayUntouched()
    {
        const string valid = "{\"model\":\"claude-opus-4-1\"}";
        foreach (var (headers, body) in new[]
                 {
                     (Headers(("content-type", "application/json"), ("content-encoding", "gzip")), valid),
                     (Headers(("content-type", "text/plain")), valid),
                     (Headers(("content-type", "application/json"), ("digest", "sha-256=fixture")), valid),
                     (Json(), "{\"model\":\"claude-opus-4-1\""),
                     (Json(), "[{\"model\":\"claude-opus-4-1\"}]"),
                     (Json(), "{\"model\":42}"),
                     (Json(), "{\"input\":\"no model\"}"),
                 })
        {
            var (text, result) = Rewrite(ClientType.Claude, Claude(), headers, body);
            Assert.Equal(body, text);
            Assert.Null(result.From);
        }
    }

    [Fact]
    public void InvalidUtf8ModelsAreLeftAlone()
    {
        var body = new byte[] { (byte)'{', (byte)'"', (byte)'m', (byte)'o', (byte)'d', (byte)'e', (byte)'l', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}' };
        var result = ModelRewriter.Apply(ClientType.Claude, Claude(), Json(), body);
        Assert.Null(result.From);
        Assert.Equal(body, result.Body.ToArray());
        Assert.Null(RetryProxy.Core.Stats.RequestMetadata.Parse(body).Model);
    }

    [Fact]
    public void CodexKeepsKnownModelsAndRewritesUnknownOnes()
    {
        var snapshot = new ChannelSnapshot
        {
            ClientType = ClientType.Codex,
            ApiKey = "sk-a",
            Models = new ProviderModels { Model = "gpt-5.1-codex" },
            ModelOverride = new KeyModelOverride { Model = "gpt-5.2" },
            KnownModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gpt-5.1-codex", "gpt-5.2", "o3" },
        };
        foreach (var known in new[] { "gpt-5.1-codex", "GPT-5.2", "O3" })
        {
            var body = $"{{\"model\":\"{known}\",\"stream\":true}}";
            Assert.Equal(body, Rewrite(ClientType.Codex, snapshot, Json(), body).Body);
        }

        var (rewritten, result) = Rewrite(ClientType.Codex, snapshot, Json(), "{\"model\":\"gpt-4o\",\"stream\":true}");
        Assert.Equal("{\"model\":\"gpt-5.2\",\"stream\":true}", rewritten);
        Assert.Equal(("gpt-4o", "gpt-5.2"), (result.From, result.To));

        // 没有 Key 覆盖时用供应商模型；供应商也没设模型时不改。
        var plain = new ChannelSnapshot { ClientType = ClientType.Codex, Models = new ProviderModels { Model = "gpt-5.1-codex" } };
        Assert.Equal("gpt-5.1-codex", ModelRewriter.CodexTarget(plain, "gpt-4o"));
        Assert.Null(ModelRewriter.CodexTarget(new ChannelSnapshot { ClientType = ClientType.Codex }, "gpt-4o"));
    }

    // ---------------------------------------------------------------- 注入与头序（§7、§9）

    [Fact]
    public void AuthHeadersAreReplacedAndRenamedInPlace()
    {
        var names = new[] { "authorization", "x-api-key", "api-key" };
        var headers = Headers(("Accept", "*/*"), ("Authorization", "Bearer local"), ("x-api-key", "local"), ("User-Agent", "cli"));
        headers.ReplaceAll(names, "x-api-key", "sk-real");
        Assert.Equal(new[] { "Accept", "x-api-key", "User-Agent" }, headers.Select(entry => entry.Key));
        Assert.Equal("sk-real", headers.Get("x-api-key"));
        var bare = Headers(("Accept", "*/*"));
        bare.ReplaceAll(names, "authorization", "Bearer sk-real");
        Assert.Equal(new[] { "Accept", "authorization" }, bare.Select(entry => entry.Key));

        Assert.Equal(new[] { "accept", "x-api-key", "content-type" }, Pipeline.RenameAuthHeader(new[] { "accept", "authorization", "content-type" }, "x-api-key"));
        Assert.Equal(new[] { "Accept", "X-Api-Key", "Host" }, Pipeline.RenameAuthHeader(new[] { "Accept", "Authorization", "x-api-key", "Host" }, "x-api-key"));
        Assert.Equal(new[] { "Host", "Authorization" }, Pipeline.RenameAuthHeader(new[] { "Host", "X-Api-Key" }, "authorization"));
    }

    // ---------------------------------------------------------------- 快照与切换信号（§4.2、§9）

    [Fact]
    public void SnapshotsDescribeTheKeyWithoutExposingSecrets()
    {
        var snapshot = new ChannelSnapshot
        {
            ProviderId = "any",
            ProviderName = "Any",
            KeyId = "k1",
            KeyName = "主号",
            ApiKey = "sk-secret",
            LocalToken = "local-secret",
            UpstreamBaseUrl = "https://anyrouter.top",
        };
        Assert.Equal("Any · 主号", snapshot.Label);
        Assert.DoesNotContain("secret", snapshot.ToString());
        Assert.Equal("Any", new ChannelSnapshot { ProviderName = "Any" }.Label);
        Assert.Equal(string.Empty, new ChannelSnapshot { ApiKey = "sk-secret" }.Label);
        Assert.True(snapshot.SameKeyAs(snapshot.WithKey("sk-other", "token", ClaudeAuthMode.ApiKey)));
        Assert.False(snapshot.SameKeyAs(new ChannelSnapshot { ProviderId = "any", KeyId = "k2" }));
    }

    [Fact]
    public void ChannelSnapshotsCollectTheCurrentKeyAndKnownModels()
    {
        var config = TestConfigs.BuiltinWithProviders();
        var codex = config.Providers.First(provider => provider.ClientType == ClientType.Codex);
        codex.Models.Model = "gpt-5.1-codex";
        codex.Keys.Add(new ProviderKey { Id = "k1", Name = "主号", ApiKey = "sk-one" });
        codex.Keys.Add(new ProviderKey { Id = "k2", Name = "群号", ApiKey = "sk-two", ModelOverride = new KeyModelOverride { Model = "gpt-5.2" } });
        var claude = config.Providers.First(provider => provider.ClientType == ClientType.Claude);
        claude.AuthMode = ClaudeAuthMode.ApiKey;
        claude.Keys.Add(new ProviderKey { Id = "c1", Name = "默认", ApiKey = "sk-claude" });
        config.Routes.First(route => route.ClientType == ClientType.Codex).CurrentKeyId = "k2";
        config.Routes.First(route => route.ClientType == ClientType.Claude).CurrentKeyId = "c1";
        config = config.Normalize();
        var codexRoute = config.Routes.First(route => route.ClientType == ClientType.Codex);

        var snapshot = config.SnapshotFor(codexRoute.Id, new[] { " o3 ", "" });
        Assert.Equal(("anyrouter.top", "k2", "群号", "sk-two"), (snapshot.ProviderName, snapshot.KeyId, snapshot.KeyName, snapshot.ApiKey));
        Assert.Equal(codexRoute.LocalToken, snapshot.LocalToken);
        Assert.NotEmpty(snapshot.LocalToken);
        Assert.Equal("https://anyrouter.top", snapshot.UpstreamBaseUrl);
        Assert.Equal(new[] { "gpt-5.1-codex", "gpt-5.2", "o3" }, snapshot.KnownModels.OrderBy(model => model, StringComparer.Ordinal));
        Assert.Equal("gpt-5.2", snapshot.ModelOverride?.Model);
        Assert.Equal(ClaudeAuthMode.Bearer, snapshot.AuthMode);

        var claudeSnapshot = config.SnapshotFor(config.Routes.First(route => route.ClientType == ClientType.Claude).Id);
        Assert.Equal((ClaudeAuthMode.ApiKey, "sk-claude"), (claudeSnapshot.AuthMode, claudeSnapshot.ApiKey));
    }

    [Fact]
    public void ChannelStateSignalsOnlyKeySwitches()
    {
        var state = new ChannelState(new ChannelSnapshot { ProviderId = "any", KeyId = "a", MaxRetries = 1 });
        var (_, version, token) = state.Read();
        using (state.EnterUndelivered())
        using (state.EnterUndelivered())
        {
            // 只改参数：版本加一，切换信号不变。
            Assert.Null(state.Update(new ChannelSnapshot { ProviderId = "any", KeyId = "a", MaxRetries = 5 }));
            var (current, next, same) = state.Read();
            Assert.Equal((5L, version + 1), (current.MaxRetries, next));
            Assert.Equal(token, same);
            Assert.False(token.IsCancellationRequested);

            // 换 Key：返回此刻未输出的请求数，旧信号触发，新快照带新信号。
            Assert.Equal(2, state.Update(new ChannelSnapshot { ProviderId = "any", KeyId = "b" }));
            Assert.True(SpinWait.SpinUntil(() => token.IsCancellationRequested, TimeSpan.FromSeconds(2)));
            Assert.False(state.Read().Switch.IsCancellationRequested);
        }

        Assert.Equal(0, state.Update(new ChannelSnapshot { ProviderId = "other", KeyId = "c" }));
    }

    [Fact]
    public void PromptCacheNamespacesFollowTheAttemptUpstream()
    {
        var cache = new PromptCache();
        var headers = Headers(("thread_id", "fixture-thread"), ("authorization", "Bearer sk-fixture"));
        var body = Encoding.UTF8.GetBytes("{\"model\":\"gpt-test\",\"input\":\"question\"}");
        CacheRequestBody Prepare(string upstream) => cache.Prepare(upstream, "POST", "/v1/responses", headers, body, false);
        string Key(CacheRequestBody request) => JsonDocument.Parse(request.Body).RootElement.GetProperty("prompt_cache_key").GetString()!;

        var a = Prepare("https://a.example");
        var b = Prepare("https://b.example");
        Assert.Equal((CacheKeyState.Added, CacheKeyState.Added), (a.State, b.State));
        Assert.NotEqual(Key(a), Key(b));
        Assert.Equal(Key(a), Key(Prepare("https://a.example")));
        // 某个地址不接受补充的缓存标识，只暂停这个地址。
        cache.Reject(a);
        Assert.Equal(CacheKeyState.Unsupported, Prepare("https://a.example").State);
        Assert.Equal(CacheKeyState.Added, Prepare("https://b.example").State);
    }

    // ---------------------------------------------------------------- 保活凭据（§7）

    [Fact]
    public void ChannelCredentialsCarryTheLocalTokenThroughTheChannel()
    {
        var credential = CliCredential.ForChannel("  local-secret ", "http://127.0.0.1:18080/");
        Assert.Equal(("local-secret", "http://127.0.0.1:18080", true), (credential.ApiKey, credential.BaseUrl, credential.IsChannelToken));
        Assert.Null(credential.Model);
        Assert.Equal(ClaudeAuthMode.Bearer, credential.AuthMode);
        Assert.DoesNotContain("local-secret", credential.ToString());
        Assert.NotEqual(CliCredential.Create("local-secret", "http://127.0.0.1:18080"), credential);
        Assert.Throws<CliException>(() => CliCredential.ForChannel(" ", "http://127.0.0.1:18080"));
        Assert.Throws<CliException>(() => CliCredential.ForChannel("local-secret", " "));

        var overrides = CliSession.CodexCredentialOverrides(credential);
        Assert.Contains("model_providers.retry_proxy_prepare.name=\"Retry Proxy 通道保活\"", overrides);
        Assert.Contains("model_providers.retry_proxy_prepare.base_url=\"http://127.0.0.1:18080/v1\"", overrides);
        Assert.All(overrides, setting => Assert.DoesNotContain("local-secret", setting));
        var settings = JsonNode.Parse(CliSession.ClaudeCredentialSettings(CliCredential.ForChannel("local-secret", "http://127.0.0.1:18081")))!;
        Assert.Equal("local-secret", settings["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
        Assert.Equal(string.Empty, settings["env"]!["ANTHROPIC_API_KEY"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:18081", settings["env"]!["ANTHROPIC_BASE_URL"]!.GetValue<string>());
    }
}
