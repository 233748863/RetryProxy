using System.Text;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Stats;
using Xunit;

namespace RetryProxy.Tests;

public sealed class ResponseComparisonTests
{
    [Theory]
    [InlineData("{\"reasoning\":{\"effort\":\"xhigh\"}}", "xhigh")]
    [InlineData("{\"reasoning_effort\":\"high\"}", "high")]
    [InlineData("{\"output_config\":{\"effort\":\"max\"}}", "max")]
    [InlineData("{\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":10000}}", null)]
    [InlineData("{\"reasoning\":{\"effort\":null}}", null)]
    [InlineData("{\"reasoning_effort\":12}", null)]
    [InlineData("{\"reasoning_effort\":\"sk-secret\"}", null)]
    [InlineData("{\"reasoning_effort\":\"high\\nWARNING injected\"}", null)]
    [InlineData("{\"output_config\":{\"effort\":\"max\"},\"reasoning\":{\"effort\":\"low\"}}", "max")]
    public void RequestEffortUsesExplicitSafeFields(string body, string? expected)
    {
        Assert.Equal(expected, RequestMetadata.Parse(Encoding.UTF8.GetBytes(body)).ReasoningEffort);
    }

    [Theory]
    [InlineData("high", "high", false)]
    [InlineData("max", "high", true)]
    [InlineData("xhigh", "high", true)]
    [InlineData(null, "high", false)]
    [InlineData("max", null, false)]
    [InlineData(null, null, false)]
    public void JsonComparesOnlyReportedValues(string? sent, string? reported, bool mismatch)
    {
        var stats = Create("application/json", "gpt-test", sent);
        stats.Observe(Encoding.UTF8.GetBytes($"{{\"model\":\"gpt-test\",\"reasoning\":{{\"effort\":{(reported is null ? "null" : $"\"{reported}\"")}}}}}"), 0.1);
        stats.Finish(0.2);
        var fields = stats.LogFields();
        if (sent is null && reported is null)
        {
            Assert.DoesNotContain("，思考 ", fields);
        }
        else if (sent is not null && sent == reported)
        {
            Assert.Contains($"，思考 {sent}，", fields);
        }
        else
        {
            Assert.Contains($"思考 {sent ?? "未指定"} -> {reported ?? "未报告"}" + (mismatch ? " (不一致)" : ""), fields);
        }
        Assert.Equal(mismatch, fields.Contains(" (不一致)"));
        Assert.Contains("，模型 gpt-test，", fields);
        Assert.DoesNotContain("模型对照", fields);
    }

    [Theory]
    [InlineData("response", "response.created", "response.completed", "reasoning")]
    [InlineData("message", "message_start", "message_stop", "output_config")]
    public void ChunkedStreamKeepsMetadataUntilCompletion(string envelope, string start, string end, string effortContainer)
    {
        var stats = Create("text/event-stream", "alias", "max");
        var payload = $"event: {start}\ndata: {{\"type\":\"{start}\",\"{envelope}\":{{\"model\":\"actual\",\"{effortContainer}\":{{\"effort\":\"high\"}}}}}}\n\n"
            + $"event: {end}\ndata: {{\"type\":\"{end}\"}}\n\n";
        foreach (var b in Encoding.UTF8.GetBytes(payload))
        {
            stats.Observe(new byte[] { b }, 0.1);
        }
        stats.Finish(0.2);
        Assert.Contains("模型 alias -> actual (不一致)", stats.LogFields());
        Assert.Contains("思考 max -> high (不一致)", stats.LogFields());
        Assert.Equal("actual", stats.CacheRequest("test-request")!.Model);
    }

    [Fact]
    public void MissingResponseMetadataDoesNotPretendToConfirmRequest()
    {
        var stats = Create("application/json", "alias", "max");
        stats.Observe("{\"usage\":{\"output_tokens_details\":{\"reasoning_tokens\":500}}}"u8, 0.1);
        stats.Finish(0.2);
        Assert.Contains("模型 alias -> 未报告", stats.LogFields());
        Assert.Contains("思考 max -> 未报告", stats.LogFields());
        Assert.DoesNotContain(" (不一致)", stats.LogFields());
        Assert.Equal("alias", stats.CacheRequest("test-request")!.Model);
    }

    [Fact]
    public void TerminalMetadataOverridesCreatedMetadataAndNewAttemptStartsEmpty()
    {
        var stats = Create("text/event-stream", "actual", "max");
        stats.Observe("data: {\"type\":\"response.created\",\"response\":{\"model\":\"alias\",\"reasoning\":{\"effort\":\"low\"}}}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"model\":\"actual\",\"reasoning\":{\"effort\":\"max\"}}}\n\n"u8, 0.1);
        stats.Finish(0.2);
        Assert.DoesNotContain(" (不一致)", stats.LogFields());
        var next = Create("application/json", "actual", "max");
        next.Observe("{}"u8, 0.1);
        next.Finish(0.2);
        Assert.Contains("-> 未报告", next.LogFields());
    }

    [Fact]
    public void LongModelsAreComparedBeforeDisplayTruncation()
    {
        var prefix = new string('a', 128);
        var metadata = RequestMetadata.Parse(Encoding.UTF8.GetBytes($"{{\"model\":\"{prefix}sent\"}}"));
        var headers = new HeaderList();
        headers.Set("content-type", "application/json");
        var stats = new ResponseStats(headers, "/v1/responses", metadata.Model, modelIdentity: metadata.ModelIdentity);
        stats.Observe(Encoding.UTF8.GetBytes($"{{\"model\":\"{prefix}reported\"}}"), 0.1);
        stats.Finish(0.2);
        Assert.Contains($"模型 {prefix} -> {prefix} (不一致)", stats.LogFields());
    }

    [Theory]
    [InlineData("x，思考 high -> low (不一致)")]
    [InlineData("x -> y (不一致)")]
    [InlineData("x，思考等级 发出 high → 返回 low（不一致）")]
    public void ModelCannotForgeAComparisonField(string model)
    {
        var stats = Create("application/json", model, null);
        stats.Observe(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { model })), 0.1);
        stats.Finish(0.2);
        Assert.Empty(RetryProxy.Core.Workspace.LogLine.FindComparisonMismatchRanges(stats.LogFields()));
        Assert.DoesNotContain(" (不一致)", stats.LogFields());
    }

    private static ResponseStats Create(string contentType, string? model, string? effort)
    {
        var headers = new HeaderList();
        headers.Set("content-type", contentType);
        return new ResponseStats(headers, "/v1/responses", model, effort);
    }
}
