using System.Text;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Stats;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

public class CompactLoggingTests
{
    [Fact]
    public void CompletionCombinesRepeatedFieldsWithoutDroppingUsage()
    {
        var stats = Create("gpt-6-astra", "xhigh").WithCacheKeyState(CacheKeyState.Client);
        stats.Observe("""
            {"model":"gpt-6-astra","reasoning":{"effort":"low"},"usage":{
              "input_tokens":112787,"output_tokens":273,"cache_creation_input_tokens":1985,
              "input_tokens_details":{"cached_tokens":110799},"output_tokens_details":{"reasoning_tokens":160}}}
            """u8, 0.1);
        stats.Finish(0.2);

        Assert.Equal("，模型 gpt-6-astra，思考 xhigh -> low (不一致)，输入/输出 112787/273 token，缓存 98.2%（读 110799 / 写 1985），推理 160 token，缓存标识 客户端", stats.LogFields());
        var range = Assert.Single(LogLine.FindComparisonMismatchRanges(stats.LogFields()));
        Assert.Equal("思考 xhigh -> low (不一致)", stats.LogFields()[range.Start..range.End]);
        Assert.Equal("gpt-6-astra", stats.CacheRequest("id")!.Model);
    }

    [Fact]
    public void AbsentComparisonMetadataDoesNotProduceEmptyFields()
    {
        var stats = Create(null, null);
        stats.Observe("{\"usage\":{\"input_tokens\":0,\"output_tokens\":0}}"u8, 0.1);
        stats.Finish(0.2);
        Assert.Equal("，输入/输出 0/0 token", stats.LogFields());
    }

    [Fact]
    public void EqualMetadataIsPrintedOnceButMissingResponseStaysExplicit()
    {
        var same = Create("model", "high");
        same.Observe("{\"model\":\"model\",\"reasoning\":{\"effort\":\"high\"}}"u8, 0.1);
        same.Finish(0.2);
        Assert.StartsWith("，模型 model，思考 high，", same.LogFields());
        Assert.DoesNotContain("->", same.LogFields());

        var missing = Create("model", "high");
        missing.Observe("{}"u8, 0.1);
        missing.Finish(0.2);
        Assert.StartsWith("，模型 model -> 未报告，思考 high -> 未报告，", missing.LogFields());
        Assert.Empty(LogLine.FindComparisonMismatchRanges(missing.LogFields()));
    }

    [Fact]
    public void ModelArrowRemainsVisibleWithoutLookingLikeAComparison()
    {
        const string model = "model -> variant (不一致)";
        var stats = Create(model, null);
        stats.Observe(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { model })), 0.1);
        stats.Finish(0.2);
        Assert.Contains("模型 model → variant [不一致]", stats.LogFields());
        Assert.Empty(LogLine.FindComparisonMismatchRanges(stats.LogFields()));
    }

    private static ResponseStats Create(string? model, string? effort)
    {
        var headers = new HeaderList();
        headers.Set("content-type", "application/json");
        return new ResponseStats(headers, "/v1/responses", model, effort);
    }
}
