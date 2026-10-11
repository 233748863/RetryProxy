using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Proxy;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

public sealed class RequestRuleTests
{
    private static RequestRule Rule(string path, string value = "null", RequestRuleAction action = RequestRuleAction.Set, string model = "") =>
        new() { Path = path, ValueJson = value, Action = action, ModelPattern = model };

    private static HeaderList Headers() { var headers = new HeaderList(); headers.Set("content-type", "application/json"); return headers; }
    private static RequestRuleRewriter.Result Apply(string body, params RequestRule[] rules) =>
        RequestRuleRewriter.Apply(rules, Headers(), Encoding.UTF8.GetBytes(body));
    private static string Body(RequestRuleRewriter.Result result) => Encoding.UTF8.GetString(result.Body.Span);

    [Theory]
    [InlineData("", null, true)]
    [InlineData("gpt-*", "GPT-5.5", true)]
    [InlineData("*opus*", "claude-opus-4", true)]
    [InlineData("gpt-5", "gpt-5.5", false)]
    [InlineData("*a*b", "abbb", true)]
    [InlineData("*a*b", "abbbc", false)]
    [InlineData("gpt-*", null, false)]
    public void ModelConditionsAreBoundedWildcards(string pattern, string? model, bool expected) =>
        Assert.Equal(expected, Rule("temperature", model: pattern).Matches(model));

    [Theory]
    [InlineData("model")]
    [InlineData("stream")]
    [InlineData("messages.extra")]
    [InlineData("tools")]
    [InlineData("metadata.user_id")]
    [InlineData("previous_response_id")]
    [InlineData("prompt_cache_key")]
    [InlineData("input.0")]
    [InlineData(".temperature")]
    [InlineData("a.b.c.d.e.f.g.h.i")]
    public void InvalidAndProtectedPathsAreRejected(string path) => Assert.Throws<ConfigException>(() => Rule(path).Validate());

    [Fact]
    public void EditsPreserveUntouchedWhitespaceEscapesNumbersAndContent()
    {
        const string input = " {\r\n \"input\" : \"\\u4e00 1.2300e+04\", \"temperature\":1.00, \"custom\": 1.234567890123456789e99 } \n";
        var result = Apply(input, Rule("temperature", "0.5"));
        Assert.Equal(input.Replace("\"temperature\":1.00", "\"temperature\":0.5"), Body(result));
        Assert.Equal(1, result.Changed);
        Assert.Equal(0, result.Skipped);
        var original = Encoding.UTF8.GetBytes(input);
        Assert.True(original.AsMemory().Equals(RequestRuleRewriter.Apply(new[] { Rule("temperature", "1.00") }, Headers(), original).Body));
    }

    [Theory]
    [InlineData("{\"a\":1, \"b\":2, \"c\":3}", "a", "{\"b\":2, \"c\":3}")]
    [InlineData("{\"a\":1, \"b\":2, \"c\":3}", "b", "{\"a\":1, \"c\":3}")]
    [InlineData("{\"a\":1, \"b\":2, \"c\":3}", "c", "{\"a\":1, \"b\":2}")]
    [InlineData("{ \"a\":1 }", "a", "{  }")]
    [InlineData("{\"reasoning\":{\"effort\":\"high\"},\"x\":3}", "reasoning.effort", "{\"reasoning\":{},\"x\":3}")]
    public void RemovalKeepsCommasValidAndPreservesOtherBytes(string input, string path, string expected) =>
        Assert.Equal(expected, Body(Apply(input, Rule(path, action: RequestRuleAction.Remove))));

    [Fact]
    public void OrderedRulesCreateParentsAndKeepExistingNullForDefaults()
    {
        var result = Apply("{\"temperature\":null}", Rule("reasoning.effort", "\"high\""),
            Rule("temperature", "1", RequestRuleAction.SetIfMissing), Rule("custom", "1"),
            Rule("custom", action: RequestRuleAction.Remove), Rule("custom", "2", RequestRuleAction.SetIfMissing));
        using var json = JsonDocument.Parse(result.Body);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("temperature").ValueKind);
        Assert.Equal("high", json.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("custom").GetInt32());
        Assert.Equal(4, result.Changed);
    }

    [Theory]
    [InlineData("{\"reasoning\":null}")]
    [InlineData("{\"reasoning\":[]}")]
    [InlineData("{\"reasoning\":{},\"reasoning\":{}}")]
    [InlineData("{\"reasoning\":{\"effort\":\"low\",\"effort\":\"high\"}}")]
    public void AmbiguousOrIncompatibleParentsSkipOnlyThatRule(string input)
    {
        var result = Apply(input, Rule("reasoning.effort", "\"high\""), Rule("temperature", "0.7"));
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.Changed);
        Assert.StartsWith(input[..^1], Body(result));
    }

    [Fact]
    public void DisabledUnmatchedAndInapplicableRulesReturnTheOriginalBuffer()
    {
        var original = Encoding.UTF8.GetBytes("{\"model\":\"claude-test\",\"temperature\":1}");
        var disabled = Rule("temperature", "2"); disabled.Enabled = false;
        var result = RequestRuleRewriter.Apply(new[] { disabled, Rule("temperature", "0", model: "gpt-*") }, Headers(), original);
        Assert.True(original.AsMemory().Equals(result.Body));
        Assert.Equal(string.Empty, result.LogFields);
        Assert.False(RequestRuleRewriter.IsGenerationPath("/v1/messages/count_tokens"));
        Assert.False(RequestRuleRewriter.IsGenerationPath("/v1/models"));
    }

    [Theory]
    [InlineData("{broken", "", "")]
    [InlineData("[]", "", "")]
    [InlineData("{\"temperature\":1}", "content-encoding", "gzip")]
    [InlineData("{\"temperature\":1}", "digest", "sha256=fixture")]
    [InlineData("{\"temperature\":1}", "content-type", "text/plain")]
    public void UnsafeBodiesPassThroughWithoutExposingValues(string input, string name, string value)
    {
        var headers = Headers(); if (name.Length > 0) headers.Set(name, value);
        var result = RequestRuleRewriter.Apply(new[] { Rule("temperature", "\"private-value\"") }, headers, Encoding.UTF8.GetBytes(input));
        Assert.Equal(input, Body(result));
        Assert.Equal(1, result.Skipped);
        Assert.DoesNotContain("private", result.LogFields);
    }

    [Fact]
    public void BodyAndValueLimitsAreEnforced()
    {
        var bytes = new byte[RequestRuleRewriter.MaxBodyBytes + 1];
        Assert.Equal(1, RequestRuleRewriter.Apply(new[] { Rule("temperature", "1") }, Headers(), bytes).Skipped);
        Assert.Throws<ConfigException>(() => Rule("custom", "\"" + new string('a', RequestRule.MaxValueBytes) + "\"").Validate());
        Assert.Throws<ConfigException>(() => Rule("custom", new string('[', 33) + "0" + new string(']', 33)).Validate());
        Assert.Equal(1, Apply("{\"x\":" + new string('[', 129) + "0" + new string(']', 129) + "}", Rule("temperature", "1")).Skipped);
    }

    private static ProxyConfig ManagedConfig()
    {
        var config = ProxyConfig.Builtin();
        config.Providers.Add(new ProviderEndpoint
        {
            Id = "p", Name = "provider", BaseUrl = "http://127.0.0.1:9", ClientType = ClientType.Codex,
            Keys = new() { new() { Id = "k", Name = "key", ApiKey = "sk-fixture" } },
            Models = new() { Model = "test" }, RequestRules = new() { Rule("reasoning.effort", "\"high\"") },
        });
        var route = config.RouteFor(ClientType.Codex)!; route.CurrentProviderId = "p"; route.CurrentKeyId = "k";
        return config.Normalize();
    }

    [Fact]
    public void RulesRoundTripAndSnapshotsAreIsolatedIncludingPreparation()
    {
        var config = ManagedConfig();
        var saved = ProxyConfigJson.Parse(ProxyConfigJson.ToCanonicalJson(config));
        Assert.Equal(config, saved.Config);
        var clone = config.Clone();
        var snapshot = config.SnapshotFor(config.RouteFor(ClientType.Codex)!.Id);
        var preparation = PreparationCatalog.Resolve(config, ClientType.Codex, null);
        config.Providers[0].RequestRules[0].ValueJson = "\"low\"";
        Assert.NotEqual(config, clone);
        Assert.Equal("\"high\"", Assert.Single(snapshot.RequestRules).ValueJson);
        Assert.Equal("\"high\"", Assert.Single(snapshot.WithKey("other", "local", Core.Cli.ClaudeAuthMode.Bearer).RequestRules).ValueJson);
        Assert.Equal("\"high\"", Assert.Single(preparation.RequestRules).ValueJson);
        var json = JsonNode.Parse(ProxyConfigJson.ToCanonicalJson(config))!;
        json["providers"]![0]!.AsObject().Remove("request_rules");
        Assert.Empty(ProxyConfigJson.Parse(json.ToJsonString()).Config.Providers[0].RequestRules);
        json["providers"]![0]!["request_rules"] = JsonNode.Parse("[{\"path\":\"temperature\",\"action\":\"typo\",\"value\":1}]");
        Assert.Throws<ConfigException>(() => ProxyConfigJson.Parse(json.ToJsonString()));
    }

    [Fact]
    public void FailedSaveLeavesRulesUnchangedAndSuccessfulSaveCopiesThem()
    {
        using var logger = ProxyLogger.Silent(Path.Combine(Path.GetTempPath(), "rule-save-" + Guid.NewGuid().ToString("N")));
        var fail = true;
        var app = new ProxyWorkspace(logger, ManagedConfig(), _ => { if (fail) throw new IOException("fixture"); });
        var draft = app.Config.Providers[0].Clone(); draft.RequestRules[0].ValueJson = "\"low\"";
        Assert.NotNull(app.SaveProvider(draft, false));
        Assert.Equal("\"high\"", app.Config.Providers[0].RequestRules[0].ValueJson);
        fail = false;
        Assert.Null(app.SaveProvider(draft, false));
        draft.RequestRules.Clear();
        Assert.Equal("\"low\"", app.Config.Providers[0].RequestRules[0].ValueJson);
    }

    private static ChannelSnapshot Snapshot(string url, string provider, params RequestRule[] rules) => new()
    {
        ClientType = ClientType.Codex, ProviderId = provider, KeyId = "k", ApiKey = "sk-test", LocalToken = "local",
        UpstreamBaseUrl = url, Models = new() { Model = "gpt-destination" }, RequestRules = rules,
        MaxRetries = 1, BaseDelaySeconds = 0, MaxDelaySeconds = 0, TimeoutSeconds = 5, TotalTimeoutSeconds = 15,
    };

    [Fact]
    public async Task RealProxyAppliesRulesAfterMappingAndReusesTheSameBodyForRetry()
    {
        var seen = new List<string>();
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            seen.Add(await Upstream.ReadBody(context));
            await Upstream.Json(context, seen.Count == 1 ? 503 : 200, seen.Count == 1 ? "{}" :
                "{\"model\":\"gpt-destination\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}]}");
        }, Configs.Default(), proxy =>
        {
            proxy.UpdateSnapshot(Snapshot(proxy.Config.UpstreamBaseUrl, "a", Rule("temperature", "0.5", model: "gpt-destination"), Rule("service_tier", "\"private-value\"")));
            return proxy;
        });
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{\"model\":\"source\",\"input\":\"hi\",\"stream\":false}");
        Assert.True(response.IsSuccessStatusCode);
        await response.Content.ReadAsStringAsync();
        Assert.Equal(2, seen.Count);
        Assert.Equal(seen[0], seen[1]);
        using var json = JsonDocument.Parse(seen[0]);
        Assert.Equal(0.5, json.RootElement.GetProperty("temperature").GetDouble());
        Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
        var logs = await fixture.CompletedLogs();
        Assert.Contains("请求规则改动 2 条", logs);
        Assert.DoesNotContain("private-value", logs);
    }

    [Fact]
    public async Task SwitchingProvidersReappliesRulesToTheOriginalRequest()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? a = null, b = null;
        await using var upstreamB = await FakeUpstream.StartAsync(async context => { b = await Upstream.ReadBody(context); await Upstream.Json(context, 200, "{\"ok\":true}"); });
        await using var fixture = await LifecycleProxy.StartAsync(async context =>
        {
            a = await Upstream.ReadBody(context); reached.TrySetResult(); await Upstream.Pending(context);
        }, Configs.Default(), proxy => { proxy.UpdateSnapshot(Snapshot(proxy.Config.UpstreamBaseUrl, "a", Rule("temperature", action: RequestRuleAction.Remove), Rule("custom", "true"))); return proxy; });
        using var client = TestClient.Create();
        var pending = TestClient.Send(client, HttpMethod.Post, fixture.Address + "/v1/responses", "{\"model\":\"source\",\"temperature\":0.3}");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Proxy.UpdateSnapshot(Snapshot(upstreamB.BaseUrl, "b", Rule("temperature", "0.7", RequestRuleAction.SetIfMissing)));
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.IsSuccessStatusCode);
        using var jsonA = JsonDocument.Parse(a!); using var jsonB = JsonDocument.Parse(b!);
        Assert.False(jsonA.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal(0.3, jsonB.RootElement.GetProperty("temperature").GetDouble());
        Assert.False(jsonB.RootElement.TryGetProperty("custom", out _));
    }

    [Fact]
    public async Task PreparationServiceUsesRulesAndItsDedicatedAccessKey()
    {
        string? received = null;
        await using var upstream = await FakeUpstream.StartAsync(async context =>
        {
            Assert.Equal("Bearer sk-upstream", context.Request.Headers.Authorization.ToString());
            received = await Upstream.ReadBody(context);
            await Upstream.Json(context, 200, "{\"ok\":true}");
        });
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var logger = ProxyLogger.Silent(Path.Combine(Path.GetTempPath(), "rule-prepare-" + Guid.NewGuid().ToString("N")));
        var config = Configs.Default(); config.UpstreamBaseUrl = upstream.BaseUrl; config.ListenPort = port; config.KeepaliveEnabled = false;
        var snapshot = ChannelSnapshot.FromRuntime(config, new[] { Rule("temperature", "0.5") });
        var service = new ProxyService(logger, "fixture").AsPreparationProxy("sk-upstream", "local-access");
        try
        {
            service.Start(config, TimeSpan.FromSeconds(5), snapshot);
            using var client = TestClient.Create();
            using var response = await TestClient.Send(client, HttpMethod.Post, config.LocalUrl + "/v1/responses", "{\"model\":\"test\"}",
                headers: new Dictionary<string, string> { ["Authorization"] = "Bearer local-access" });
            Assert.True(response.IsSuccessStatusCode);
            using var json = JsonDocument.Parse(received!);
            Assert.Equal(0.5, json.RootElement.GetProperty("temperature").GetDouble());
        }
        finally { service.Stop(TimeSpan.FromSeconds(5)); }
    }
}
