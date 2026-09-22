using System;
using System.Linq;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>移植 window_recovery.rs 的单元测试与 ui/cache.rs 的筛选测试。</summary>
public class WindowRecoveryTests
{
    private static WindowRect Rect(int left, int top, int width, int height) => WindowRect.FromSize(left, top, width, height);

    private static WindowMetrics Metrics(uint dpi) => new(
        WindowMetrics.Scaled(780.0, dpi),
        WindowMetrics.Scaled(540.0, dpi),
        WindowMetrics.Scaled(1000.0, dpi),
        WindowMetrics.Scaled(700.0, dpi),
        WindowMetrics.Scaled(32.0, dpi),
        WindowMetrics.Scaled(96.0, dpi),
        WindowMetrics.Scaled(16.0, dpi));

    private static MonitorArea[] Primary() => [new MonitorArea(Rect(0, 0, 1920, 1040), true)];

    [Fact]
    public void DamagedMinimizedRectangleRecoversLastValidBounds()
    {
        var saved = Rect(120, 80, 1100, 800);
        Assert.Equal(saved, WindowRecoveryMath.RecoveryRect(Rect(-32000, -32000, 160, 28), saved, Primary(), Metrics(96)));
    }

    [Fact]
    public void MissingSavedBoundsRecoversCenteredDefaultSize()
    {
        Assert.Equal(Rect(460, 170, 1000, 700), WindowRecoveryMath.RecoveryRect(Rect(-32000, -32000, 160, 28), null, Primary(), Metrics(96)));
    }

    [Fact]
    public void TinyOnscreenWindowAlsoRecovers()
    {
        Assert.Equal(Rect(460, 170, 1000, 700), WindowRecoveryMath.RecoveryRect(Rect(100, 100, 160, 28), null, Primary(), Metrics(96)));
    }

    [Fact]
    public void NegativeMonitorCoordinatesRemainUnchanged()
    {
        MonitorArea[] monitors = [Primary()[0], new MonitorArea(Rect(-1920, -1080, 1920, 1040), false)];
        Assert.Null(WindowRecoveryMath.RecoveryRect(Rect(-1700, -1000, 1000, 700), null, monitors, Metrics(96)));
    }

    [Fact]
    public void AccessiblePartiallyOffscreenWindowsRemainUnchanged()
    {
        foreach (var bounds in new[] { Rect(-500, 100, 1000, 700), Rect(1800, 100, 1000, 700), Rect(200, -8, 1000, 700) })
        {
            Assert.Null(WindowRecoveryMath.RecoveryRect(bounds, null, Primary(), Metrics(96)));
        }
    }

    [Fact]
    public void InaccessibleTitleBarRecovers()
    {
        var recovered = WindowRecoveryMath.RecoveryRect(Rect(100, -100, 1000, 700), null, Primary(), Metrics(96));
        Assert.NotNull(recovered);
        Assert.True(Metrics(96).IsUsable(recovered.Value, Primary()));
    }

    [Fact]
    public void RemovedMonitorRecoversOntoThePrimaryWorkArea()
    {
        var recovered = WindowRecoveryMath.RecoveryRect(Rect(-32000, -32000, 160, 28), Rect(-1800, 100, 1000, 700), Primary(), Metrics(96));
        Assert.Equal(Rect(460, 170, 1000, 700), recovered);
    }

    [Fact]
    public void SmallWorkAreaLimitsSizeWithoutARecoveryLoop()
    {
        MonitorArea[] monitors = [new MonitorArea(Rect(20, 40, 640, 480), true)];
        var recovered = WindowRecoveryMath.RecoveryRect(Rect(-32000, -32000, 160, 28), null, monitors, Metrics(192));
        Assert.Equal(monitors[0].Work, recovered);
        Assert.Null(WindowRecoveryMath.RecoveryRect(recovered!.Value, null, monitors, Metrics(192)));
    }

    [Fact]
    public void HighDpiSizesArePhysicalPixels()
    {
        MonitorArea[] monitors = [new MonitorArea(Rect(0, 0, 3840, 2160), true)];
        Assert.Equal(Rect(920, 380, 2000, 1400), WindowRecoveryMath.RecoveryRect(Rect(100, 100, 1000, 700), null, monitors, Metrics(192)));
    }

    [Fact]
    public void UnavailableMonitorsDoNotMoveTheWindow()
    {
        Assert.Null(WindowRecoveryMath.RecoveryRect(Rect(-32000, -32000, 160, 28), null, Array.Empty<MonitorArea>(), Metrics(96)));
    }

    [Fact]
    public void RecoveryWaitsForAPersistentInvalidRectangle()
    {
        var now = TimeSpan.FromSeconds(100);
        var gate = new RecoveryGate();
        Assert.False(gate.Ready(now));
        Assert.False(gate.Ready(now + TimeSpan.FromMilliseconds(249)));
        gate = new RecoveryGate();
        Assert.False(gate.Ready(now + TimeSpan.FromMilliseconds(250)));
        Assert.True(gate.Ready(now + TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void FailedRecoveryRetriesAtMostEveryFiveSeconds()
    {
        var now = TimeSpan.FromSeconds(100);
        var gate = new RecoveryGate();
        Assert.False(gate.Ready(now));
        Assert.True(gate.FirstAttempt);
        Assert.True(gate.Ready(now + RecoveryGate.ConfirmDelay));
        Assert.False(gate.FirstAttempt);
        Assert.False(gate.Ready(now + RecoveryGate.ConfirmDelay + RecoveryGate.RetryInterval - TimeSpan.FromMilliseconds(1)));
        Assert.True(gate.Ready(now + RecoveryGate.ConfirmDelay + RecoveryGate.RetryInterval));
        Assert.True(gate.Pending);
        gate.Reset();
        Assert.False(gate.Pending);
    }

    [Fact]
    public void WindowRectFormatsLikeTheRustDebugOutput()
    {
        Assert.Equal("WindowRect { left: -32000, top: -32000, right: -31840, bottom: -31972 }", Rect(-32000, -32000, 160, 28).ToString());
        Assert.Equal(0, new WindowRect(10, 10, 0, 0).Width);
    }

    private static CacheRequest Sample(int id, ulong? cached) => new()
    {
        RequestId = id.ToString("D8"),
        Model = "gpt-test",
        CompletedAtUnixMs = 1_700_000_000_000,
        InputTokens = 11_558,
        CachedTokens = cached,
        CacheCreationTokens = null,
        InputAccounting = CacheInputAccounting.IncludesCached,
        CacheKeyStatus = "客户端已设置",
    };

    [Fact]
    public void MissingUsageIsDistinctFromZeroHitsAndReplacesTheLastVisibleRate()
    {
        var cache = new CacheSnapshot();
        cache.Record(Sample(0, 0));
        cache.Record(Sample(1, 11_555));
        cache.Record(Sample(2, null));
        Assert.Equal(1, cache.RecentRequests.Count(request => CacheFilter.ZeroHit.Accepts(request)));
        Assert.Equal(1, cache.RecentRequests.Count(request => CacheFilter.Unmeasured.Accepts(request)));
        Assert.Equal(3, cache.RecentRequests.Count(request => CacheFilter.All.Accepts(request)));
        Assert.Equal("99.97%", CacheText.RequestRateText(cache.RecentRequests[1]));
        // 摘要：累计命中率来自两次有效请求，最近一次未获取用量不覆盖它。
        Assert.Equal("49.99%", CacheText.RateText(cache.HitRatePercent()));
        Assert.Equal("未获取", CacheText.RequestRateText(cache.RecentRequests[^1]));
        Assert.Equal("用量未计入累计命中率", CacheText.LatestDetail(cache.RecentRequests[^1]));
        Assert.Equal("今日最近 3 次成功请求 · 命中 49.99%", CacheText.RecentSummary(cache));
        Assert.Equal("· 今日有效 2 次 / 未计入 1 次", CacheText.LegendCounts(cache));
        Assert.Equal("未获取 / 11558", CacheText.UsageCell(cache.RecentRequests[^1]));
        Assert.Null(CacheText.CacheKeyHelp(cache));
        cache.AddedKeyRequests = 2;
        Assert.Contains("代理补全 2 次", CacheText.CacheKeyHelp(cache));
        Assert.Equal("全部", CacheFilterExtensions.Parse("anything").Label());
        Assert.Equal(CacheFilter.ZeroHit, CacheFilterExtensions.Parse("zero-hit"));
    }
}
