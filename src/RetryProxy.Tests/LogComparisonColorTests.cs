using System;
using System.Linq;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

public class LogComparisonColorTests
{
    [Fact]
    public void HighlightsOnlyCompleteMismatchedComparisonFields()
    {
        const string model = "模型对照 发出 requested-model → 返回 actual-model（不一致）";
        const string effort = "思考等级 发出 high → 返回 low（不一致）";
        var body = $"请求完成 HTTP 200，模型 actual-model，{model}，{effort}，耗时 1 秒";

        var ranges = LogLine.FindComparisonMismatchRanges(body);

        Assert.Equal(new[] { model, effort }, ranges.Select(range => body[range.Start..range.End]));
        Assert.Equal(body.IndexOf(model, StringComparison.Ordinal), ranges[0].Start);
        Assert.Equal(body.IndexOf(effort, StringComparison.Ordinal) + effort.Length, ranges[1].End);
    }

    [Theory]
    [InlineData("")]
    [InlineData("，，")]
    [InlineData("模型对照 发出 a → 返回 a，思考等级 发出 high → 返回 high")]
    [InlineData("模型对照 发出 a → 返回 未提供，思考等级 发出 未提供 → 返回 high")]
    [InlineData("模型 a（不一致），耗时 1 秒（不一致）")]
    [InlineData("说明 模型对照 发出 a → 返回 b（不一致）")]
    [InlineData("模型对照x 发出 a → 返回 b（不一致）")]
    [InlineData("思考等级x 发出 high → 返回 low（不一致）")]
    [InlineData("模型对照 发出 a → 返回 b（不一致） 后缀")]
    [InlineData("模型对照 发出 a → 返回 b，耗时（不一致）")]
    [InlineData("思考等级 发出 high → 返回 low，其他（不一致）")]
    [InlineData("模型对照 发出 a，返回 b（不一致）")]
    public void DoesNotHighlightUnmarkedOrUnrelatedFields(string body)
    {
        Assert.Empty(LogLine.FindComparisonMismatchRanges(body));
    }

    [Theory]
    [InlineData("模型对照 发出 a → 返回 b（不一致）")]
    [InlineData("思考等级 发出 high → 返回 low（不一致）")]
    public void SupportsSingleFieldAndTrimsSurroundingWhitespace(string field)
    {
        var body = $"， \t{field} \t，";

        var range = Assert.Single(LogLine.FindComparisonMismatchRanges(body));

        Assert.Equal(3, range.Start);
        Assert.Equal(field, body[range.Start..range.End]);
        Assert.Equal((0, field.Length), Assert.Single(LogLine.FindComparisonMismatchRanges(field)));
    }

    [Fact]
    public void DoesNotExtendHighlightIntoEffectiveModelContainingChineseComma()
    {
        const string body = "模型 alpha，beta，模型对照 发出 alpha,beta → 返回 gamma（不一致），思考等级 发出 high → 返回 high";

        var range = Assert.Single(LogLine.FindComparisonMismatchRanges(body));

        Assert.Equal("模型对照 发出 alpha,beta → 返回 gamma（不一致）", body[range.Start..range.End]);
    }

    [Fact]
    public void UnmarkedComparisonDoesNotConsumeFollowingMismatchedField()
    {
        const string body = "模型对照 发出 a → 返回 a，思考等级 发出 high → 返回 low（不一致）";

        var range = Assert.Single(LogLine.FindComparisonMismatchRanges(body));

        Assert.Equal("思考等级 发出 high → 返回 low（不一致）", body[range.Start..range.End]);
    }

    [Theory]
    [InlineData(200, StatusColorClass.Level)]
    [InlineData(302, StatusColorClass.Level)]
    [InlineData(429, StatusColorClass.Warning)]
    [InlineData(500, StatusColorClass.Danger)]
    public void ComparisonRangesPreserveHttpStatusDetection(int status, StatusColorClass expectedColor)
    {
        var body = $"请求 HTTP {status}，模型对照 发出 a → 返回 b（不一致），思考等级 发出 high → 返回 low（不一致）";

        var ranges = LogLine.FindComparisonMismatchRanges(body);
        var found = LogLine.FindStatusCode(body)!.Value;

        Assert.Equal(2, ranges.Count);
        Assert.Equal(status, found.Status);
        Assert.Equal($"HTTP {status}", body[found.Start..found.End]);
        Assert.Equal(expectedColor, LogLine.StatusColor(found.Status));
        Assert.All(ranges, range => Assert.True(range.Start >= found.End));
    }
}
