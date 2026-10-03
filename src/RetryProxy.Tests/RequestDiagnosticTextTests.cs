using System;
using RetryProxy.Core.Config;
using RetryProxy.Core.Diagnostics;
using Xunit;

namespace RetryProxy.Tests;

public sealed class RequestDiagnosticTextTests
{
    private static DiagnosticSummary Summary => new(new DiagnosticRequestInfo(
        "3a1422a746cd45cf95e96f7ea419818a", ClientType.Claude, new DateOnly(2026, 10, 3),
        new DateTimeOffset(2026, 10, 3, 10, 20, 30, TimeSpan.Zero), "POST", "/v1/messages"));

    [Theory]
    [InlineData(DiagnosticOutcome.Pending, "处理中")]
    [InlineData(DiagnosticOutcome.Success, "成功")]
    [InlineData(DiagnosticOutcome.Failure, "失败")]
    [InlineData(DiagnosticOutcome.Unknown, "结束状态未记录")]
    public void OutcomeOnlyUsesRecordedBusinessResult(DiagnosticOutcome outcome, string expected)
    {
        var summary = Summary with { Outcome = outcome, Incomplete = true, PreviousSession = true };
        Assert.Equal(expected, RequestDiagnosticText.Result(summary));
        Assert.Equal(RequestDiagnosticText.IncompleteWarning, RequestDiagnosticText.Completeness(summary));
    }

    [Fact]
    public void RealSendCountNeverBecomesRetryCount()
    {
        var summary = Summary with { Outcome = DiagnosticOutcome.Success, SendCount = 6, RetryCount = 2 };
        Assert.Equal("成功（重试 2 次）", RequestDiagnosticText.Result(summary));
        var text = RequestDiagnosticText.SafeSummary(summary);
        Assert.Contains("实际发送次数：6", text);
        Assert.Contains("原重试次数：2", text);
        Assert.DoesNotContain("重试 5 次", text);
    }

    [Fact]
    public void PreviousSessionWithoutFinishDoesNotInventFailureOrDuration()
    {
        var summary = Summary with { Outcome = DiagnosticOutcome.Unknown, PreviousSession = true, Incomplete = true, LastElapsedSeconds = 90 };
        var text = RequestDiagnosticText.SafeSummary(summary);
        Assert.Contains("结果：结束状态未记录", text);
        Assert.Contains("首字耗时：未记录", text);
        Assert.Contains("总耗时：未记录", text);
        Assert.DoesNotContain("失败", text);
    }

    [Fact]
    public void SuccessAndUnknownDeliveryRemainSeparate()
    {
        var summary = Summary with { Outcome = DiagnosticOutcome.Success, Delivery = DiagnosticDelivery.Unknown, Incomplete = true };
        var text = RequestDiagnosticText.SafeSummary(summary);
        Assert.Contains("结果：成功", text);
        Assert.Contains("客户端交付：交付状态未记录", text);
        Assert.Contains(RequestDiagnosticText.IncompleteWarning, text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MissingOrInvalidTimingIsNeverZero(double? value) =>
        Assert.Equal("未记录", RequestDiagnosticText.Seconds(value));

    [Fact]
    public void ZeroTimingIsARecordedValue() => Assert.EndsWith("秒", RequestDiagnosticText.Seconds(0));

    [Fact]
    public void WaitEndUsesActualDurationAndNotPlannedDuration()
    {
        var entry = new DiagnosticEntry(DiagnosticEventKind.RetryWaitFinished, 4)
        { DurationSeconds = 1.5, PlannedWaitSeconds = 30, Reason = "等待被中断" };
        var text = RequestDiagnosticText.EventBody(entry);
        Assert.Contains("实际等待：", text);
        Assert.Contains(RequestDiagnosticText.Seconds(1.5), text);
        Assert.Contains("等待被中断", text);
        Assert.DoesNotContain("30", text);
    }

    [Fact]
    public void SendAndAttemptOrdinalsRemainDistinct()
    {
        var entry = new DiagnosticEntry(DiagnosticEventKind.SendStarted, 1)
        { SendNumber = 4, AttemptNumber = 2, RetryCount = 1 };
        var text = RequestDiagnosticText.EventBody(entry);
        Assert.Contains("实际发送序号：4", text);
        Assert.Contains("外层尝试序号：2", text);
        Assert.Contains("原重试次数：1", text);
        Assert.Contains("出站模型：未记录", text);
    }

    [Theory]
    [InlineData(DiagnosticEventKind.CompatibilityResend, "兼容重发（不计重试）")]
    [InlineData(DiagnosticEventKind.Switched, "手动改投（不计重试）")]
    [InlineData(DiagnosticEventKind.WaitingGeneration, "等待生成")]
    public void SpecialStagesAreExplicit(DiagnosticEventKind kind, string expected) =>
        Assert.Equal(expected, RequestDiagnosticText.EventTitle(kind));

    [Fact]
    public void SwitchingNotificationDoesNotClaimAnActualSend()
    {
        var entry = new DiagnosticEntry(DiagnosticEventKind.Switched, 1)
        { Target = new DiagnosticTarget("p", "notification-target", "k", "key", "model") };
        Assert.DoesNotContain("notification-target", RequestDiagnosticText.EventBody(entry));
        Assert.DoesNotContain("实际发送", RequestDiagnosticText.EventBody(entry));
    }

    [Fact]
    public void SafeSummaryOnlyExportsAllowlistedTextAndNormalizedEndpoint()
    {
        var summary = Summary with
        {
            Request = Summary.Request with { Endpoint = "/v1/messages?secret=private-body" },
            LastTarget = new DiagnosticTarget("provider-id", "https://private.example/token", "key-id", "sk-secret", "model\nforged"),
            FailureReason = "https://upstream.example/raw-error",
        };
        var text = RequestDiagnosticText.SafeSummary(summary);
        Assert.Contains("POST /messages", text);
        Assert.Contains("[已隐藏]", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain("raw-error", text);
        Assert.DoesNotContain("model\nforged", text);
        Assert.DoesNotContain("provider-id", text);
        Assert.DoesNotContain("key-id", text);
        Assert.DoesNotContain("{", text);
    }

    [Fact]
    public void HistoricalNamesAreNotTranslatedAsUiLabels()
    {
        var summary = Summary with { LastTarget = new DiagnosticTarget("p", "成功", "k", "默认", "模型") };
        Assert.Equal("成功 · 默认", RequestDiagnosticText.Target(summary.LastTarget, _ => "translated"));
        Assert.Equal("模型", RequestDiagnosticText.Value(summary.LastTarget.Model, _ => "translated"));
    }

    [Fact]
    public void CompactOverviewKeepsIdentityAndCountersWithinEightLines()
    {
        var summary = Summary with
        {
            Outcome = DiagnosticOutcome.Success, Delivery = DiagnosticDelivery.Complete,
            SendCount = 3, RetryCount = 1, TotalSeconds = 2,
            LastTarget = new DiagnosticTarget("p", "provider", "k", "key", "model"),
        };
        var text = RequestDiagnosticText.Overview(summary);
        Assert.Equal(8, text.Split(Environment.NewLine).Length);
        Assert.Contains(summary.Request.RequestId, text);
        Assert.Contains("实际发送次数：3", text);
        Assert.Contains("原重试次数：1", text);
        Assert.Contains("provider · key", text);
        Assert.DoesNotContain("结束原因：未记录", text);
    }

    [Fact]
    public void CompactOverviewDoesNotInventMissingOutcomeOrDuration()
    {
        var text = RequestDiagnosticText.Overview(Summary with
        {
            Outcome = DiagnosticOutcome.Unknown, Incomplete = true, PreviousSession = true,
        });
        Assert.Contains("结束状态未记录", text);
        Assert.Contains("总耗时：未记录", text);
        Assert.Contains(RequestDiagnosticText.IncompleteWarning, text);
        Assert.DoesNotContain("失败", text);
    }

    [Fact]
    public void RecordedWallClockOffsetIsPreservedInDisplayAndCopy()
    {
        var summary = Summary with
        {
            Request = Summary.Request with { StartedAt = new DateTimeOffset(2026, 10, 3, 1, 2, 3, TimeSpan.FromHours(14)) },
        };
        Assert.Contains("2026-10-03 01:02:03 +14:00", RequestDiagnosticText.Overview(summary));
        Assert.Contains("2026-10-03 01:02:03 +14:00", RequestDiagnosticText.SafeSummary(summary));
    }

    [Fact]
    public void TranslatesTemplatesAndFixedLabelsWithoutParsingFormattedChinese()
    {
        static string Translate(string key) => key switch
        {
            "成功" => "Success",
            "{0}（重试 {1} 次）" => "{0} ({1} retries)",
            "未记录" => "Not recorded",
            _ => key,
        };
        Assert.Equal("Success (3 retries)", RequestDiagnosticText.Result(Summary with
        { Outcome = DiagnosticOutcome.Success, RetryCount = 3 }, Translate));
        Assert.Equal("Not recorded", RequestDiagnosticText.Seconds(null, Translate));
    }
}
