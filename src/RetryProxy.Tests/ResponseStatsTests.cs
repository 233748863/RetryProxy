using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Stats;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>
/// 移植自 rust/src/response_stats.rs 的 #[cfg(test)] mod tests。
/// Rust 测试里直接读取的私有字段（model/usage/events/json_overflow/answer）
/// 在这里改经公开接口（CacheRequest、LogFields、ContextTokens、EventDecoder）验证。
/// </summary>
public class ResponseStatsTests
{
    private static readonly string Bom = ((char)0xFEFF).ToString();

    private static ResponseStats EventStats(string path)
    {
        var headers = new HeaderList();
        headers.Set("content-type", "text/event-stream");
        return new ResponseStats(headers, path, "request-alias");
    }

    private static void Event(ResponseStats stats, string json, double elapsed)
    {
        stats.Observe(Encoding.UTF8.GetBytes($"data: {json}\n\n"), elapsed);
    }

    private static void ObserveChunks(ResponseStats stats, byte[] bytes, int size, double elapsed)
    {
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            stats.Observe(bytes.AsSpan(offset, Math.Min(size, bytes.Length - offset)), elapsed);
        }
    }

    private static void ObserveBytes(ResponseStats stats, string text, double elapsed)
    {
        ObserveChunks(stats, Encoding.UTF8.GetBytes(text), 1, elapsed);
    }

    private static void AssertComplete(ResponseStats stats)
    {
        Assert.True(stats.Outcome is { IsFailed: false }, "expected StreamOutcome.Complete");
    }

    private static void AssertFailed(ResponseStats stats)
    {
        Assert.True(stats.Outcome is { IsFailed: true }, "expected StreamOutcome.Failed");
    }

    [Fact]
    public void GenerationGateKeepsEmptyEventsPendingAcrossByteBoundaries()
    {
        var gate = new GenerationGate();
        foreach (var prefix in new[]
                 {
                     Bom + ": heartbeat\r\n\r\n",
                     "id: pending\rretry: 1000\r\r",
                     "event: keepalive\ndata: {\"type\":\"keepalive\"}\n\n",
                     "event: ping\ndata: {}\n\n",
                     "data\n\n",
                 })
        {
            foreach (var value in Encoding.UTF8.GetBytes(prefix))
            {
                Assert.False(gate.Observe(new[] { value }), prefix);
            }
        }

        foreach (var value in new[]
                 {
                     """{"type":"response.created","response":{"id":"opening","output":[],"status":"in_progress"}}""",
                     """{"type":"response.in_progress","response":{"output":[]}}""",
                     """{"type":"response.output_item.added","item":{"type":"message","content":[],"status":"in_progress"}}""",
                     """{"type":"response.output_item.added","item":{"type":"reasoning","summary":[]}}""",
                     """{"type":"response.reasoning_summary_part.added","part":{"type":"summary_text","text":""}}""",
                     """{"type":"response.content_part.added","part":{"type":"output_text","text":"","annotations":[]}}""",
                     """{"type":"response.output_text.delta","delta":""}""",
                     """{"type":"message_start","message":{"content":[],"stop_reason":null}}""",
                     """{"type":"content_block_start","content_block":{"type":"thinking","thinking":""}}""",
                     """{"type":"content_block_delta","delta":{"type":"text_delta","text":""}}""",
                     """{"choices":[{"delta":{"role":"assistant","content":null,"reasoning_content":""},"finish_reason":null}]}""",
                 })
        {
            foreach (var b in Encoding.UTF8.GetBytes($"data: {value}\r\n\r\n"))
            {
                Assert.False(gate.Observe(new[] { b }), value);
            }
        }

        Assert.False(gate.Finish());
    }

    [Fact]
    public void GenerationGateReleasesContentToolsTerminalAndUnknownEvents()
    {
        foreach (var value in new[]
                 {
                     """{"type":"response.output_text.delta","delta":"answer"}""",
                     """{"type":"response.reasoning_summary_text.delta","delta":"thinking"}""",
                     """{"type":"response.output_item.added","item":{"type":"function_call","name":"run","arguments":""}}""",
                     """{"type":"response.output_item.added","item":{"type":"web_search_call","status":"in_progress"}}""",
                     """{"type":"response.output_item.added","item":{"type":"reasoning","encrypted_content":"payload"}}""",
                     """{"type":"response.output_item.done","item":{"type":"message","content":[{"type":"output_text","text":"answer"}]}}""",
                     """{"type":"response.content_part.added","part":{"type":"output_text","text":"answer"}}""",
                     """{"type":"content_block_start","content_block":{"type":"tool_use","id":"tool","name":"run","input":{}}}""",
                     """{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"thought"}}""",
                     """{"type":"content_block_delta","delta":{"type":"signature_delta","signature":"signature"}}""",
                     """{"choices":[{"delta":{"tool_calls":[{"function":{"name":"run","arguments":""}}]}}]}""",
                     """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                     """{"type":"response.completed","response":{"output":[]}}""",
                     """{"type":"message_stop"}""",
                     """{"type":"error","error":{"message":"failure"}}""",
                     """{"type":"response.failed","response":{"error":{"code":"server_error"}}}""",
                     """{"type":"response.incomplete","response":{"output":[]}}""",
                     """{"type":"response.in_progress","response":{"output":[],"status":"failed"}}""",
                     """{"type":"keepalive","delta":"unexpected content"}""",
                     """{"type":"keepalive","data":{"unknown":"payload"}}""",
                     """{"type":"unknown.event","payload":"opaque"}""",
                     "null",
                 })
        {
            var gate = new GenerationGate();
            Assert.False(gate.Observe(": heartbeat\n\n"u8));
            Assert.True(gate.Observe(Encoding.UTF8.GetBytes($"data: {value}\n\n")), value);
            Assert.True(gate.Observe(": another heartbeat\n\n"u8));
        }

        foreach (var payload in new[]
                 {
                     "data: [DONE]\n\n",
                     "event: unknown\ndata: not-json\n\n",
                     "event: response.output_text.delta\ndata: {\"type\":\"keepalive\"}\n\n",
                     "unknown: extension\n\n",
                     "opaque bytes without a newline",
                 })
        {
            Assert.True(new GenerationGate().Observe(Encoding.UTF8.GetBytes(payload)), payload);
        }
    }

    [Fact]
    public void GenerationGateChecksUnterminatedEventsBeforeReplaying()
    {
        var heartbeat = new GenerationGate();
        Assert.False(heartbeat.Observe("data: {\"type\":\"keepalive\"}"u8));
        Assert.False(heartbeat.Finish());
        foreach (var payload in new[]
                 {
                     "data: {\"type\":\"response.output_text.delta\",\"delta\":\"answer\"}",
                     "data: {\"type\":\"unknown\"}",
                     "data: {\"type\":",
                 })
        {
            var gate = new GenerationGate();
            Assert.False(gate.Observe(Encoding.UTF8.GetBytes(payload)), payload);
            Assert.True(gate.Finish(), payload);
        }
    }

    [Fact]
    public void ToolCallStartsCountAsGeneratedContent()
    {
        foreach (var value in new[]
                 {
                     """{"type":"response.output_item.added","item":{"type":"function_call","name":"run","arguments":""}}""",
                     """{"type":"content_block_start","content_block":{"type":"tool_use","name":"run","input":{}}}""",
                 })
        {
            var stats = EventStats("/v1/responses");
            Event(stats, value, 1.5);
            Assert.Equal((double?)1.5, stats.FirstContentSeconds());
        }
    }

    [Fact]
    public void ResponsesMeasureFirstContentAfterMetadataAndKeepRawUsage()
    {
        var stats = EventStats("/v1/responses");
        stats.Observe(": heartbeat\r\n\r\n"u8, 0.1);
        Event(stats, """{"type":"response.created","response":{"model":"gpt-actual","output":[]}}""", 0.2);
        Assert.Null(stats.FirstContentSeconds());
        const string delta = "event: response.output_text.delta\r\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"中文\"}\r\n\r\n";
        ObserveBytes(stats, delta, 1.25);
        Event(
            stats,
            """{"type":"response.completed","response":{"model":"gpt-actual","usage":{"input_tokens":120,"output_tokens":9,"input_tokens_details":{"cached_tokens":20},"output_tokens_details":{"reasoning_tokens":3}}}}""",
            2.0);
        Assert.Equal((double?)1.25, stats.FirstContentSeconds());
        var request = stats.CacheRequest("req")!;
        Assert.Equal("gpt-actual", request.Model);
        Assert.Equal((ulong?)120, request.InputTokens);
        Assert.Equal((ulong?)20, request.CachedTokens);
        var fields = stats.LogFields();
        Assert.Contains("输入 120 / 输出 9 token", fields);
        Assert.Contains("推理 3 token", fields);
        Assert.Equal((ulong?)129, stats.ContextTokens(false));
        AssertComplete(stats);
    }

    [Fact]
    public void ClaudeMergesSnapshotsWithoutSummingOrResettingToPlaceholderZero()
    {
        var stats = EventStats("/v1/messages");
        Event(
            stats,
            """{"type":"message_start","message":{"model":"claude-test","content":[],"usage":{"input_tokens":300,"output_tokens":0,"cache_read_input_tokens":1000,"cache_creation_input_tokens":200}}}""",
            0.2);
        Event(stats, """{"type":"content_block_delta","delta":{"type":"text_delta","text":"你好"}}""", 0.8);
        Event(
            stats,
            """{"type":"message_delta","usage":{"input_tokens":0,"output_tokens":7,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}""",
            1.0);
        var midway = stats.CacheRequest("req")!;
        Assert.Equal((ulong?)300, midway.InputTokens);
        Assert.Equal((ulong?)1000, midway.CachedTokens);
        Assert.Equal((ulong?)200, midway.CacheCreationTokens);
        Event(
            stats,
            """{"type":"message_delta","usage":{"input_tokens":80,"output_tokens":11,"cache_read_input_tokens":1200,"cache_creation_input_tokens":0}}""",
            1.5);
        Event(
            stats,
            """{"type":"message_delta","usage":{"input_tokens":300,"output_tokens":11,"cache_read_input_tokens":1000}}""",
            1.6);
        Event(stats, """{"type":"message_stop"}""", 1.7);
        var final = stats.CacheRequest("req")!;
        Assert.Equal((ulong?)80, final.InputTokens);
        Assert.Equal((ulong?)1200, final.CachedTokens);
        Assert.Equal((ulong?)0, final.CacheCreationTokens);
        Assert.Contains("输入 80 / 输出 11 token", stats.LogFields());
        Assert.Equal((ulong?)91, stats.ContextTokens(false));
        Assert.Equal((double?)0.8, stats.FirstContentSeconds());
        AssertComplete(stats);
    }

    [Fact]
    public void ChatCompletionsWaitForUsageAfterFinishReason()
    {
        var stats = EventStats("/v1/chat/completions");
        Event(stats, """{"model":"chat-test","choices":[{"delta":{"role":"assistant","content":""}}]}""", 0.1);
        Assert.Null(stats.FirstContentSeconds());
        Event(stats, """{"choices":[{"delta":{"content":"hi"}}]}""", 0.6);
        Event(stats, """{"choices":[{"delta":{},"finish_reason":"stop"}]}""", 0.8);
        Assert.Null(stats.Outcome);
        Event(
            stats,
            """{"choices":[],"usage":{"prompt_tokens":20,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":6,"cache_write_tokens":4},"completion_tokens_details":{"reasoning_tokens":2}}}""",
            0.9);
        stats.Observe("data: [DONE]\n\n"u8, 1.0);
        var request = stats.CacheRequest("req")!;
        Assert.Equal((ulong?)20, request.InputTokens);
        Assert.Equal((ulong?)6, request.CachedTokens);
        Assert.Equal((ulong?)4, request.CacheCreationTokens);
        var fields = stats.LogFields();
        Assert.Contains("输入 20 / 输出 5 token", fields);
        Assert.Contains("缓存命中 6 token", fields);
        Assert.Contains("缓存写入 4 token", fields);
        Assert.Contains("推理 2 token", fields);
        Assert.Equal((ulong?)25, stats.ContextTokens(false));
        Assert.Equal((double?)0.6, stats.FirstContentSeconds());
        AssertComplete(stats);
    }

    [Fact]
    public void MissingUsageIsDistinctFromReportedZero()
    {
        var missing = EventStats("/v1/responses");
        Event(missing, """{"type":"response.completed","response":{}}""", 0.3);
        Assert.Contains("输入 未获取 / 输出 未获取", missing.LogFields());
        var zero = EventStats("/v1/responses");
        Event(
            zero,
            """{"type":"response.completed","response":{"usage":{"input_tokens":0,"output_tokens":0,"input_tokens_details":{"cached_tokens":42}}}}""",
            0.3);
        Assert.Contains("输入 0 / 输出 0", zero.LogFields());
        Assert.Contains("缓存命中 42", zero.LogFields());
    }

    [Fact]
    public void JsonErrorResponsesKeepAShortSafeFailureDetail()
    {
        var headers = new HeaderList();
        headers.Set("content-type", "application/json");
        var stats = new ResponseStats(headers, "/v1/responses", "model");
        stats.Observe("{\"error\":{\"message\":\"unsupported field: reasoning\"}}"u8, 0.2);
        stats.Finish(0.3);
        Assert.Equal("上游返回错误响应：unsupported field: reasoning", stats.FailureReason());
    }

    [Fact]
    public void ErrorsInsideHttp200AreNotCompletedGenerations()
    {
        foreach (var eventType in new[] { "response.failed", "response.incomplete", "error" })
        {
            var stats = EventStats("/v1/responses");
            Event(stats, "{\"type\":\"" + eventType + "\",\"error\":{\"message\":\"private-server-message\"}}", 0.2);
            AssertFailed(stats);
            Assert.DoesNotContain("private-server-message", stats.LogFields());
            Assert.Null(stats.FirstContentSeconds());
        }
    }

    [Fact]
    public void FailureLogsIncludeStructuredDetailsWithoutErrorMessages()
    {
        var cases = new (string Payload, string[] Expected)[]
        {
            (
                """{"type":"error","code":"server_error","param":"input[0].content","message":"private-server-message","usage":{"input_tokens":12,"output_tokens":0}}""",
                new[] { "上游错误码 server_error", "错误参数 input[0].content", "输入 12 / 输出 0" }
            ),
            (
                """{"type":"response.failed","response":{"error":{"code":"invalid_encrypted_content","type":"invalid_request_error","message":"private-server-message"}}}""",
                new[] { "上游错误码 invalid_encrypted_content", "上游错误类型 invalid_request_error" }
            ),
            (
                """{"type":"response.incomplete","response":{"incomplete_details":{"reason":"max_output_tokens"}}}""",
                new[] { "未完成原因 max_output_tokens" }
            ),
            (
                """{"type":"error","error":{"code":502,"type":"upstream_error","message":"private-server-message"}}""",
                new[] { "上游错误码 502", "上游错误类型 upstream_error" }
            ),
        };
        foreach (var (payload, expected) in cases)
        {
            var stats = EventStats("/v1/responses");
            ObserveChunks(stats, Encoding.UTF8.GetBytes($"data: {payload}\n\n"), 3, 0.2);
            var fields = stats.LogFields();
            foreach (var expectedField in expected)
            {
                Assert.Contains(expectedField, fields);
            }

            Assert.DoesNotContain("private-server-message", fields);
            AssertFailed(stats);
        }
    }

    [Fact]
    public void UpstreamRateLimitAfterCreatedPreservesMissingUsage()
    {
        var stats = EventStats("/v1/responses");
        Event(
            stats,
            """{"type":"response.created","response":{"model":"gpt-6-astra","status":"in_progress","usage":null}}""",
            18.1);
        Event(
            stats,
            """{"type":"error","error":{"code":"rate_limit_exceeded","type":"too_many_requests","message":"Your requests to gpt-6-astra for gpt-6-astra in eastus2 have exceeded rate limit."}}""",
            18.2);
        stats.Finish(18.2);
        AssertFailed(stats);
        Assert.Equal("上游返回错误事件", stats.Outcome!.Value.Reason);
        Assert.Null(stats.FirstContentSeconds());
        var request = stats.CacheRequest("req")!;
        Assert.Null(request.InputTokens);
        Assert.Null(stats.ContextTokens(false));
        Assert.Equal("上游请求超限", stats.FailureSummary());
        var fields = stats.LogFields();
        Assert.Contains("上游错误码 rate_limit_exceeded", fields);
        Assert.Contains("上游错误类型 too_many_requests", fields);
        Assert.Contains("输入 未获取 / 输出 未获取", fields);
        Assert.Contains("未读取到用量统计", fields);
        Assert.Contains("生成内容：未读取到", fields);
        Assert.Contains("最后事件 error", fields);
        Assert.DoesNotContain("eastus2", fields);
    }

    [Fact]
    public void FailureSummariesUseStructuredCodesInsteadOfMessagesOrParameters()
    {
        var cases = new (string Error, string? Expected)[]
        {
            ("""{"code":"rate_limit_exceeded"}""", "上游请求超限"),
            ("""{"type":"rate_limit_error"}""", "上游请求超限"),
            ("""{"type":"too_many_requests"}""", "上游请求超限"),
            ("""{"code":"insufficient_quota","type":"rate_limit_error"}""", "上游可用额度不足"),
            ("""{"code":"novel_error","type":"too_many_requests"}""", "上游请求超限"),
            ("""{"type":"overloaded_error"}""", "上游服务繁忙"),
            ("""{"code":"context_length_exceeded"}""", "请求内容超过上游长度限制"),
            ("""{"code":"invalid_api_key"}""", "上游身份验证失败"),
            ("""{"code":"server_error"}""", "上游服务内部错误"),
            ("""{"code":"novel_error","param":"rate_limit_exceeded","message":"rate_limit_exceeded private-message"}""", null),
        };
        foreach (var (error, expected) in cases)
        {
            var stats = EventStats("/v1/responses");
            Event(stats, "{\"type\":\"error\",\"error\":" + error + "}", 0.2);
            Assert.Equal(expected, stats.FailureSummary());
            Assert.DoesNotContain("private-message", stats.FailureLogFields());
        }
    }

    [Fact]
    public void IncompleteReasonsHaveSafeHumanReadableSummaries()
    {
        var cases = new (string Reason, string? Expected)[]
        {
            ("max_output_tokens", "达到上游输出长度限制"),
            ("content_filter", "上游内容审核拦截"),
            ("novel_reason", null),
        };
        foreach (var (reason, expected) in cases)
        {
            var stats = EventStats("/v1/responses");
            Event(stats, "{\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":\"" + reason + "\"}}}", 0.2);
            Assert.Equal(expected, stats.FailureSummary());
        }
    }

    [Fact]
    public void TransportFailureFieldsKeepRequestIdProgressAndPartialUsage()
    {
        var headers = new HeaderList();
        headers.Set("content-type", "text/event-stream");
        headers.Set("x-request-id", "transport-request-123");
        var stats = new ResponseStats(headers, "/v1/responses", null);
        Event(stats, """{"type":"response.created","response":{"usage":{"input_tokens":42}}}""", 0.1);
        Event(stats, """{"type":"response.output_text.delta","delta":"private-answer"}""", 0.2);
        var fields = stats.FailureLogFields();
        Assert.Contains("上游请求 ID transport-request-123", fields);
        Assert.Contains("生成内容：已读取到", fields);
        Assert.Contains("最后事件 response.output_text.delta", fields);
        Assert.Contains("输入 42 / 输出 未获取 token（用量统计不完整）", fields);
        Assert.DoesNotContain("private-answer", fields);
        Assert.Null(stats.Outcome);
        Assert.DoesNotContain("上游请求 ID", stats.LogFields());
    }

    [Fact]
    public void FailureEventNamesCannotExposeCredentialsOrInjectLogLines()
    {
        foreach (var eventType in new[] { "sk-private-secret", "error\nprivate-line", new string('x', 129) })
        {
            var stats = EventStats("/v1/responses");
            var payload = JsonSerializer.Serialize(new { type = eventType, error = new { code = "novel_error" } });
            Event(stats, payload, 0.2);
            var fields = stats.FailureLogFields();
            Assert.DoesNotContain("最后事件", fields);
            Assert.DoesNotContain("private", fields);
        }
    }

    [Fact]
    public void ReportedZeroUsageOnFailureRemainsDistinctFromMissingUsage()
    {
        var stats = EventStats("/v1/responses");
        Event(
            stats,
            """{"type":"response.failed","response":{"usage":{"input_tokens":0,"output_tokens":0},"error":{"code":"server_error"}}}""",
            0.2);
        var fields = stats.FailureLogFields();
        Assert.Contains("输入 0 / 输出 0 token", fields);
        Assert.DoesNotContain("未读取到用量统计", fields);
        Assert.DoesNotContain("用量统计不完整", fields);
    }

    [Fact]
    public void FailureIdentifiersRejectCredentialsAndUntrustedText()
    {
        foreach (var invalid in new[]
                 {
                     "Bearer private-api-secret",
                     "sk-private-api-secret",
                     "server_error\nforged-log",
                     "https://example.test/?key=private-api-secret",
                     new string('x', 129),
                 })
        {
            var stats = EventStats("/v1/responses");
            var payload = JsonSerializer.Serialize(new
            {
                type = "error",
                error = new { code = invalid, type = invalid, param = invalid, message = "private-server-message" },
            });
            Event(stats, payload, 0.2);
            var fields = stats.LogFields();
            Assert.DoesNotContain("上游错误码", fields);
            Assert.DoesNotContain("上游错误类型", fields);
            Assert.DoesNotContain("错误参数", fields);
            Assert.DoesNotContain("private", fields);
        }
    }

    [Fact]
    public void UpstreamRequestIdIsLoggedOnlyForFailedResponses()
    {
        var headers = new HeaderList();
        headers.Set("content-type", "text/event-stream");
        headers.Set("x-oneapi-request-id", "upstream-request-123");
        foreach (var (eventType, failed) in new (string, bool)[] { ("response.completed", false), ("error", true) })
        {
            var stats = new ResponseStats(headers, "/v1/responses", null);
            Event(stats, "{\"type\":\"" + eventType + "\"}", 0.2);
            Assert.Equal(failed, stats.LogFields().Contains("上游请求 ID upstream-request-123", StringComparison.Ordinal));
        }

        headers.Set("x-oneapi-request-id", "sk-private-api-secret");
        var secret = new ResponseStats(headers, "/v1/responses", null);
        Event(secret, """{"type":"error"}""", 0.2);
        Assert.DoesNotContain("private-api-secret", secret.LogFields());
    }

    [Fact]
    public void PrematureEofIsAFailureEvenAfterContentArrives()
    {
        var stats = EventStats("/v1/responses");
        Event(stats, """{"type":"response.output_text.delta","delta":"partial"}""", 0.4);
        stats.Finish(0.8);
        AssertFailed(stats);
        Assert.True(stats.MissingTerminalEvent);
        Assert.Equal((double?)0.4, stats.FirstContentSeconds());
    }

    [Fact]
    public void ExplicitTerminalEventsAreNotMissingCompletion()
    {
        foreach (var terminal in new[] { "response.completed", "response.failed", "response.incomplete", "error" })
        {
            var stats = EventStats("/v1/responses");
            Event(stats, "{\"type\":\"" + terminal + "\"}", 0.2);
            stats.Finish(0.4);
            Assert.NotNull(stats.Outcome);
            Assert.False(stats.MissingTerminalEvent, terminal);
        }
    }

    [Fact]
    public void DecoderSupportsCrMultilineDataAndEventNameFallback()
    {
        var stats = EventStats("/v1/messages");
        var payload =
            Bom + "event: message_start\rdata: {\"message\":\rdata: {\"model\":\"claude-流\",\"usage\":{\"input_tokens\":25,\"output_tokens\":0}}}\r\r"
            + "event: message_delta\rdata: {\"usage\":{\"output_tokens\":4}}\r\r"
            + "event: message_stop\rdata: {}\r\r";
        ObserveChunks(stats, Encoding.UTF8.GetBytes(payload), 3, 1.0);
        var request = stats.CacheRequest("req")!;
        Assert.Equal("claude-流", request.Model);
        Assert.Equal((ulong?)25, request.InputTokens);
        var fields = stats.LogFields();
        Assert.Contains("模型 claude-流", fields);
        Assert.Contains("输入 25 / 输出 4 token", fields);
        AssertComplete(stats);
    }

    [Fact]
    public void NonStreamingProtocolsPreserveLargeCountsAndFirstByteTime()
    {
        foreach (var usage in new[]
                 {
                     """{"input_tokens":5000000000,"output_tokens":15,"cache_read_input_tokens":40,"cache_creation_input_tokens":2}""",
                     """{"prompt_tokens":5000000000,"completion_tokens":15,"prompt_cache_hit_tokens":40,"cache_creation_input_tokens":2}""",
                 })
        {
            var stats = new ResponseStats(new HeaderList(), "/v1/responses", null);
            var payload = "{\"model\":\"json-model\",\"usage\":" + usage + "}";
            ObserveChunks(stats, Encoding.UTF8.GetBytes(payload), 3, 0.2);
            stats.Finish(0.9);
            var request = stats.CacheRequest("req")!;
            Assert.Equal((ulong?)5000000000UL, request.InputTokens);
            Assert.Equal((ulong?)40, request.CachedTokens);
            Assert.Equal((ulong?)2, request.CacheCreationTokens);
            Assert.Contains("输入 5000000000 / 输出 15 token", stats.LogFields());
            Assert.Equal((ulong?)5000000015UL, stats.ContextTokens(false));
            Assert.Equal((double?)0.2, stats.FirstContentSeconds());
        }
    }

    [Fact]
    public void OversizedEventsDoNotHideLaterUsageOrGrowWithoutLimit()
    {
        // Rust 直接检查 stats.events.skip_event / line.len()；C# 的解码器状态是私有的，
        // 这里用独立的 EventDecoder 验证同样的丢弃行为（超限即标记 Unsupported 并清空缓冲）。
        var oversized = new byte[ResponseStats.MaxObservationBytes + 1];
        Array.Fill(oversized, (byte)'x');
        var decoder = new EventDecoder();
        Assert.Empty(decoder.Push("data: "u8));
        Assert.Empty(decoder.Push(oversized));
        Assert.True(decoder.Unsupported);
        Assert.Empty(decoder.Push("\n\n"u8));
        var later = decoder.Push("data: {\"type\":\"response.completed\"}\n\n"u8);
        Assert.Single(later);
        Assert.Equal("{\"type\":\"response.completed\"}", Encoding.UTF8.GetString(later[0].Data));

        var stats = EventStats("/v1/responses");
        stats.Observe("data: "u8, 0.1);
        stats.Observe(oversized, 0.2);
        stats.Observe("\n\n"u8, 0.3);
        Event(stats, """{"type":"response.completed","response":{"usage":{"input_tokens":12,"output_tokens":8}}}""", 0.4);
        var request = stats.CacheRequest("req")!;
        Assert.Equal((ulong?)12, request.InputTokens);
        Assert.Contains("输入 12 / 输出 8 token", stats.LogFields());
        Assert.Equal((ulong?)20, stats.ContextTokens(false));
        AssertComplete(stats);
    }

    [Fact]
    public void OversizedJsonOnlyDisablesObservation()
    {
        // Rust 直接断言 stats.json_overflow 与 json_body 已清空；C# 里这两个字段是私有的，
        // 仅验证可观察结果：超限后不再解析用量。
        var stats = new ResponseStats(new HeaderList(), "/v1/responses", null);
        stats.Observe("{"u8, 0.1);
        var spaces = new byte[ResponseStats.MaxObservationBytes];
        Array.Fill(spaces, (byte)' ');
        stats.Observe(spaces, 0.2);
        stats.Finish(0.3);
        Assert.Contains("未获取", stats.LogFields());
        Assert.Null(stats.ContextTokens(false));
    }

    [Fact]
    public void ModelLabelsAreBoundedAndCannotInjectLogLines()
    {
        var model = "gpt\n\r" + new string('x', 200);
        var body = JsonSerializer.Serialize(new { model, input = "private-prompt" });
        var extracted = RequestMetadata.Parse(Encoding.UTF8.GetBytes(body)).Model;
        Assert.NotNull(extracted);
        Assert.Equal(128, extracted!.EnumerateRunes().Count());
        Assert.DoesNotContain("\n", extracted);
        Assert.DoesNotContain("\r", extracted);
        Assert.DoesNotContain("private-prompt", extracted);
        Assert.Null(RequestMetadata.Parse("{\"model\":42}"u8.ToArray()).Model);
    }

    [Fact]
    public void BackgroundResponsesCaptureAnswersWithoutRepeatingDeltasOrCachedInput()
    {
        var stats = EventStats("/v1/responses").WithAnswerCapture();
        Event(stats, """{"type":"response.reasoning_summary_text.delta","delta":"private reasoning"}""", 0.1);
        Event(stats, """{"type":"response.output_text.delta","delta":"Java "}""", 0.2);
        Event(stats, """{"type":"response.output_text.delta","delta":"答案"}""", 0.3);
        Event(
            stats,
            """{"type":"response.completed","response":{"output":[{"type":"reasoning","summary":[{"text":"private reasoning"}]},{"type":"message","content":[{"type":"output_text","text":"Java 答案"}]}],"usage":{"input_tokens":9084,"output_tokens":1424,"input_tokens_details":{"cached_tokens":3986}}}}""",
            0.4);
        Assert.Equal("Java 答案", stats.Answer());
        Assert.Equal((ulong?)10_508, stats.ContextTokens(false));
        AssertComplete(stats);
    }

    [Fact]
    public void BackgroundClaudeUsageIncludesCacheReadAndCacheCreationOnce()
    {
        var stats = EventStats("/v1/messages").WithAnswerCapture();
        Event(
            stats,
            """{"type":"message_start","message":{"usage":{"input_tokens":100,"output_tokens":0,"cache_read_input_tokens":1000,"cache_creation_input_tokens":200}}}""",
            0.1);
        Event(stats, """{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"private reasoning"}}""", 0.2);
        Event(stats, """{"type":"content_block_start","content_block":{"type":"text","text":"Java"}}""", 0.3);
        Event(stats, """{"type":"content_block_delta","delta":{"type":"text_delta","text":" 答案"}}""", 0.4);
        Event(
            stats,
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":0,"output_tokens":50,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}""",
            0.5);
        Event(stats, """{"type":"message_stop"}""", 0.6);
        Assert.Equal("Java 答案", stats.Answer());
        Assert.Equal((ulong?)1350, stats.ContextTokens(true));
    }

    [Fact]
    public void BackgroundChatCaptureHandlesUnicodeByteSplitsAndFinalUsage()
    {
        var stats = EventStats("/v1/chat/completions").WithAnswerCapture();
        const string stream =
            "data: {\"choices\":[{\"delta\":{\"content\":\"你好 ☕\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":80,\"completion_tokens\":10,\"prompt_tokens_details\":{\"cached_tokens\":30}}}\n\n"
            + "data: [DONE]\n\n";
        ObserveBytes(stats, stream, 0.2);
        stats.Finish(0.3);
        Assert.Equal("你好 ☕", stats.Answer());
        Assert.Equal((ulong?)90, stats.ContextTokens(false));
        AssertComplete(stats);
    }

    [Fact]
    public void OrdinaryJsonAnswersAreCapturedForAllThreeProtocols()
    {
        var responses = new (string Path, string Body)[]
        {
            (
                "/v1/responses",
                """{"output":[{"type":"message","content":[{"type":"output_text","text":"答案"}]}],"usage":{"input_tokens":20,"output_tokens":5}}"""
            ),
            (
                "/v1/messages",
                """{"content":[{"type":"thinking","thinking":"private"},{"type":"text","text":"答案"}],"usage":{"input_tokens":20,"output_tokens":5}}"""
            ),
            (
                "/v1/chat/completions",
                """{"choices":[{"message":{"content":"答案"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":5}}"""
            ),
        };
        foreach (var (path, body) in responses)
        {
            var stats = new ResponseStats(new HeaderList(), path, null).WithAnswerCapture();
            stats.Observe(Encoding.UTF8.GetBytes(body), 0.1);
            stats.Finish(0.2);
            Assert.Equal("答案", stats.Answer());
            Assert.Equal((ulong?)25, stats.ContextTokens(path.EndsWith("messages", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void RealTrafficDoesNotCollectAnAnswerBody()
    {
        var stats = EventStats("/v1/responses");
        Event(stats, """{"type":"response.output_text.delta","delta":"private answer"}""", 0.1);
        Event(stats, """{"type":"response.completed","response":{"output":[{"content":[{"text":"private answer"}]}]}}""", 0.2);
        Assert.Null(stats.Answer());
        Assert.DoesNotContain("private", stats.LogFields());
    }

    [Fact]
    public void MissingOrOverflowingUsageIsNeverFabricated()
    {
        foreach (var usage in new[]
                 {
                     """{"input_tokens":10}""",
                     """{"output_tokens":3}""",
                     """{"input_tokens":18446744073709551615,"output_tokens":1}""",
                 })
        {
            var stats = EventStats("/v1/responses");
            Event(stats, "{\"type\":\"response.completed\",\"response\":{\"usage\":" + usage + "}}", 0.1);
            Assert.Null(stats.ContextTokens(false));
        }
    }

    [Fact]
    public void TruncatedOrOversizedAnswersAreNotSavedAsCompleteTurns()
    {
        // Rust 直接调用私有的 append_answer；这里改用带答案捕获的增量事件喂入同样的文本。
        var stats = EventStats("/v1/responses").WithAnswerCapture();
        Event(stats, "{\"type\":\"response.output_text.delta\",\"delta\":\"" + new string('a', 512 * 1024) + "\"}", 0.1);
        Assert.NotNull(stats.Answer());
        Event(stats, """{"type":"response.output_text.delta","delta":"b"}""", 0.2);
        Assert.Null(stats.Answer());

        var cases = new (string Path, string Partial, string Value)[]
        {
            (
                "/v1/messages",
                """{"type":"content_block_delta","delta":{"type":"text_delta","text":"partial"}}""",
                """{"type":"message_delta","delta":{"stop_reason":"max_tokens"}}"""
            ),
            (
                "/v1/chat/completions",
                """{"choices":[{"delta":{"content":"partial"}}]}""",
                """{"choices":[{"delta":{"content":"partial"},"finish_reason":"length"}]}"""
            ),
        };
        foreach (var (path, partial, value) in cases)
        {
            var truncated = EventStats(path).WithAnswerCapture();
            Event(truncated, partial, 0.1);
            Assert.NotNull(truncated.Answer());
            Event(truncated, value, 0.2);
            Assert.Null(truncated.Answer());
        }
    }
}
