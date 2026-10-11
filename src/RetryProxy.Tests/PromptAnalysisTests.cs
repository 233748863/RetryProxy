using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

public sealed class PromptAnalysisTests
{
    private static PromptAnalysis Capture(string json, HeaderList? headers = null, ClientType client = ClientType.Codex)
        => PromptAnalyzer.Capture(client, headers ?? new(), Encoding.UTF8.GetBytes(json), 1000, 123);

    [Theory]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("中文😀", 3)]
    [InlineData("abcd中文", 3)]
    public void EstimatesUnicodeScalarsWithoutSplittingSurrogates(string text, long expected) => Assert.Equal(expected, PromptAnalyzer.Estimate(text));

    [Fact]
    public void ClaudeSeparatesInstructionsToolsReadResultsAndConversationWithoutRetainingText()
    {
        const string json = """
            {"system":"system-private","tools":[{"name":"Read","input_schema":{"type":"object"}}],
             "metadata":{"user_id":"user_private_account_private_session_my-session"},"messages":[
             {"role":"user","content":"# AGENTS.md instructions\nprivate-rule"},
             {"role":"assistant","content":[{"type":"tool_use","id":"call-1","name":"Read","input":{"file_path":"private.cs"}}]},
             {"role":"user","content":[{"type":"tool_result","tool_use_id":"call-1","content":"private-file-contents"},{"type":"tool_result","tool_use_id":"call-2","content":"private-result"},{"type":"text","text":"private-chat"}]}]}
            """;
        var result = Capture(json, client: ClientType.Claude);
        Assert.Equal("ok", result.Status);
        Assert.All(result.Parts, part => Assert.True(part.Tokens > 0, part.Kind.ToString()));
        Assert.Equal(64, result.SessionHash!.Length);
        var saved = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private", saved);
        Assert.DoesNotContain("my-session", saved);
        Assert.DoesNotContain("Read", saved);
    }

    [Fact]
    public void ResponsesReadCallsLinkToOutputsAndSchemaStaysSeparate()
    {
        var result = Capture("""
            {"instructions":"system","tools":[{"type":"function","name":"read_file"}],"input":[
             {"type":"function_call","name":"read_file","call_id":"a","arguments":"{}"},
             {"type":"function_call_output","call_id":"a","output":"file content"},
             {"type":"function_call_output","call_id":"b","output":"command output"},
             {"role":"user","content":[{"type":"input_text","text":"hello"}]}]}
            """);
        Assert.Equal(3, result.Parts.Single(p => p.Kind == PromptPartKind.Files).Tokens);
        Assert.Equal(4, result.Parts.Single(p => p.Kind == PromptPartKind.Results).Tokens);
        Assert.False(result.HasUnmeasuredContent);
    }

    [Fact]
    public void ExplicitConversationIdentifiersAgreeAcrossHeaderAndBodyButStayClientScoped()
    {
        var header = new HeaderList(); header.Set("thread_id", "session-a");
        var codex = Capture("{\"input\":\"hi\"}", header);
        var body = Capture("""{"input":"hi","client_metadata":{"x-codex-turn-metadata":"{\"thread_id\":\"session-a\"}"}}""");
        var claude = Capture("""{"messages":[],"metadata":{"user_id":"{\"session_id\":\"session-a\"}"}}""", client: ClientType.Claude);
        Assert.Equal(codex.SessionHash, body.SessionHash);
        Assert.NotEqual(codex.SessionHash, claude.SessionHash);
        Assert.Null(Capture("""{"input":"hi","prompt_cache_key":"shared-cache"}""").SessionHash);
        Assert.Null(Capture("""{"messages":[],"metadata":{"user_id":"same-user"}}""", client: ClientType.Claude).SessionHash);
    }

    [Fact]
    public void ImagesAndServerHistoryDoNotBecomeInventedMeasuredParts()
    {
        var result = Capture("""{"previous_response_id":"last","input":[{"role":"user","content":[{"type":"input_text","text":"abcd"},{"type":"input_image","image_url":"data:private"}]}]}""");
        var measured = result.Measure(new RequestUsage { InputTokens = 1000, InputAccounting = CacheInputAccounting.IncludesCached });
        Assert.True(result.HasServerHistory);
        Assert.True(result.HasUnmeasuredContent);
        Assert.True(measured.Measured);
        Assert.Equal(1000, measured.Tokens);
        Assert.Equal(1, measured.Parts.Sum(p => p.Tokens));
        Assert.Equal(100, measured.FillPercent);
        Assert.False(result.Measure(null).Complete);
    }

    [Fact]
    public void TextPartsCalibrateOnlyWithCompleteValidReportedInput()
    {
        var result = Capture("""{"instructions":"abcd","input":"12345678"}""");
        var measured = result.Measure(new RequestUsage { InputTokens = 300, InputAccounting = CacheInputAccounting.IncludesCached });
        Assert.Equal(300, measured.Parts.Sum(p => p.Tokens));
        Assert.Equal(100, measured.Parts.Single(p => p.Kind == PromptPartKind.System).Tokens);
        var lower = result.Measure(new RequestUsage { InputTokens = 100, CachedTokens = 100, InputAccounting = CacheInputAccounting.ExcludesCached });
        Assert.False(lower.Measured);
        Assert.Equal(3, lower.Tokens);
        Assert.Null((result with { Window = null }).Measure(null).FillPercent);
    }

    [Theory]
    [InlineData("{broken", "invalid_json")]
    [InlineData("[]", "unsupported")]
    [InlineData("{\"model\":\"test\"}", "unsupported")]
    public void UnsupportedInputsHaveNoFakeZeroBreakdown(string json, string status)
    {
        var result = Capture(json);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Measure(null).Tokens);
        Assert.Empty(result.Parts);
    }

    [Fact]
    public void LimitsAndCompressionDoNotAttemptToParseBodies()
    {
        var large = PromptAnalyzer.Capture(ClientType.Codex, new(), new byte[PromptAnalyzer.MaxBodyBytes + 1], null, 0);
        Assert.Equal("size_limit", large.Status);
        var headers = new HeaderList(); headers.Set("content-encoding", "gzip");
        Assert.Equal("encoded", Capture("{", headers).Status);
        Assert.Equal("invalid_json", Capture("{\"input\":" + new string('[', 129) + "0" + new string(']', 129) + "}").Status);
    }

    [Fact]
    public void MidnightCarriesAnalysisAndAttributionUntilCompletionAndIgnoresBackgroundRequests()
    {
        var storage = new MemoryStorage();
        var now = DateTime.Today.AddHours(23);
        using var state = new MetricsState(now, storage);
        state.Active["r"] = new ActiveRequest { RequestId = "r" };
        state.Change("r", new DailyChange.Started(), 1);
        state.Change("r", new DailyChange.Analyzed(Capture("{\"input\":\"hi\"}")), 2);
        state.Change("r", new DailyChange.Attributed(new RequestAttribution { ProviderId = "p" }), 3);
        state.Rollover(now.AddDays(1));
        state.Change("r", new DailyChange.Failed(), 4);
        var row = storage.Rows[DateOnly.FromDateTime(now.AddDays(1))]["r"];
        Assert.NotNull(row.PromptAnalysis);
        Assert.Equal("p", row.Attribution!.ProviderId);
        using var metrics = new ProxyMetrics(storage);
        metrics.RequestStarted(ProxyMetrics.KeepAlivePrefix + "bg", "POST", "/v1/responses");
        metrics.Analyze(ProxyMetrics.KeepAlivePrefix + "bg", Capture("{\"input\":\"hi\"}"));
        Assert.DoesNotContain(storage.Rows.Values.SelectMany(x => x.Keys), id => id.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealProxyAnalyzesUserRequestsOnceAcrossRetryWithoutChangingRequestBody()
    {
        const string body = """{"model":"test-model","prompt_cache_key":"already-set","input":"private text"}""";
        var seen = new List<string>();
        await using var upstream = await FakeUpstream.StartAsync(async context =>
        {
            seen.Add(await Upstream.ReadBody(context));
            if (seen.Count == 1) await Upstream.Json(context, 503, "{\"error\":{\"message\":\"temporary\"}}");
            else await Upstream.Json(context, 200, """{"model":"test-model","output":[{"type":"message","content":[{"type":"output_text","text":"answer"}]}],"usage":{"input_tokens":10,"output_tokens":1}}""");
        });
        var directory = Path.Combine(Path.GetTempPath(), "retry-context-test-" + Guid.NewGuid().ToString("N"));
        using var logger = ProxyLogger.Silent(directory);
        var storage = new MemoryStorage();
        using var metrics = new ProxyMetrics(storage);
        var config = Configs.Default(); config.UpstreamBaseUrl = upstream.BaseUrl; config.MaxRetries = 1; config.BaseDelaySeconds = 0; config.MaxDelaySeconds = 0;
        var proxy = new Core.Proxy.RetryProxy(config, logger, metrics, CancellationToken.None);
        await using var host = await ProxyHost.StartAsync(proxy, 0);
        using var client = TestClient.Create();
        using var response = await TestClient.Send(client, HttpMethod.Post, $"http://127.0.0.1:{host.Port}/v1/responses", body,
            headers: new Dictionary<string, string> { ["thread_id"] = "real-thread" });
        Assert.True(response.IsSuccessStatusCode);
        await response.Content.ReadAsStringAsync();
        await TestClock.WaitUntil(() => metrics.Snapshot().ActiveRequests == 0);
        var record = Assert.Single(storage.Rows.Values.SelectMany(x => x.Values));
        Assert.Equal(2, seen.Count);
        Assert.All(seen, value => Assert.Equal(body, value));
        Assert.Equal(1UL, record.RetryCount);
        Assert.Equal("ok", record.PromptAnalysis!.Status);
        Assert.NotNull(record.PromptAnalysis.SessionHash);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(record));
    }

    private sealed class MemoryStorage : IDailyStorage
    {
        public Dictionary<DateOnly, Dictionary<string, DailyRequest>> Rows { get; } = new();
        public DailyJournalOpenResult Open(DateOnly date, bool importLegacy)
        {
            if (!Rows.TryGetValue(date, out var rows)) Rows[date] = rows = new();
            return new() { Records = new Dictionary<string, DailyRequest>(rows), Journal = new Journal(rows) };
        }
        private sealed class Journal(Dictionary<string, DailyRequest> rows) : IDailyJournal
        { public void Append(DailyRequest record) => rows[record.RequestId] = record.Clone(); }
    }
}
