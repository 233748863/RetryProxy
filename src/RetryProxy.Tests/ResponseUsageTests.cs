using System;
using System.Text;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Stats;
using Xunit;

namespace RetryProxy.Tests;

public sealed class ResponseUsageTests
{
    private static ResponseStats Stream(string path)
    {
        var headers = new HeaderList();
        headers.Set("content-type", "text/event-stream");
        return new ResponseStats(headers, path, "requested-model");
    }

    private static void Event(ResponseStats stats, string json, double elapsed) =>
        stats.Observe(Encoding.UTF8.GetBytes($"data: {json}\n\n"), elapsed);

    [Fact]
    public void ResponsesKeepReasoningInsideOutputAndMeasureTextAfterThinking()
    {
        var stats = Stream("/v1/responses");
        Event(stats, """{"type":"response.created","response":{"model":"gpt-test","output":[]}}""", 0.2);
        Event(stats, """{"type":"response.reasoning_summary_text.delta","delta":"thinking"}""", 1);
        Event(stats, """{"type":"response.output_text.delta","delta":"answer"}""", 3);
        Event(stats, """{"type":"response.completed","response":{"model":"gpt-test","usage":{"input_tokens":1000,"input_tokens_details":{"cached_tokens":800},"output_tokens":300,"output_tokens_details":{"reasoning_tokens":100}}}}""", 5);
        stats.Finish(5);
        var usage = Assert.IsType<RequestUsage>(stats.Usage(5));
        Assert.Equal("gpt-test", usage.Model);
        Assert.Equal(CacheInputAccounting.IncludesCached, usage.InputAccounting);
        Assert.Equal(1, usage.FirstContentSeconds);
        Assert.Equal(3, usage.FirstTextSeconds);
        Assert.Equal(1300m, usage.Normalize().KnownTokens);
        Assert.Equal(100UL, usage.ReasoningTokens);
        Assert.Equal((200m, 2d), usage.Decode());
    }

    [Fact]
    public void ClaudeCombinesCacheWriteDurationsAndKeepsInitialUsageThroughFinalDelta()
    {
        var stats = Stream("/v1/messages");
        Event(stats, """{"type":"message_start","message":{"model":"claude-test","content":[],"usage":{"input_tokens":100,"output_tokens":0,"cache_read_input_tokens":800,"cache_creation":{"ephemeral_5m_input_tokens":60,"ephemeral_1h_input_tokens":40}}}}""", 0.1);
        Event(stats, """{"type":"content_block_delta","delta":{"type":"text_delta","text":"answer"}}""", 0.5);
        Event(stats, """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":50}}""", 2);
        Event(stats, """{"type":"message_stop"}""", 2);
        stats.Finish(2);
        var usage = Assert.IsType<RequestUsage>(stats.Usage(2));
        Assert.Equal(CacheInputAccounting.ExcludesCached, usage.InputAccounting);
        Assert.Equal(100UL, usage.CacheCreationTokens);
        Assert.Equal(40UL, usage.CacheCreation1hTokens);
        Assert.Equal(0.5, usage.FirstTextSeconds);
        Assert.Equal(1050m, usage.Normalize().KnownTokens);
        Assert.True(usage.Normalize().Complete);
    }

    [Fact]
    public void NonStreamingUsageKeepsUnknownFieldsAndDoesNotInventSpeed()
    {
        var headers = new HeaderList();
        headers.Set("content-type", "application/json");
        var stats = new ResponseStats(headers, "/v1/chat/completions", null);
        stats.Observe(Encoding.UTF8.GetBytes("""{"model":"gpt-test","choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":20}}"""), 2);
        stats.Finish(2);
        var usage = Assert.IsType<RequestUsage>(stats.Usage(2));
        Assert.False(usage.IsStreaming);
        Assert.Equal(1020m, usage.Normalize().KnownTokens);
        Assert.Null(usage.CachedTokens);
        Assert.Null(usage.FirstContentSeconds);
        Assert.Null(usage.Decode());
        Assert.Null(stats.Usage(double.NaN)!.DurationSeconds);
    }
}
