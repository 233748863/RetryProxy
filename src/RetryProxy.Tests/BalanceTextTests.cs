using System;
using System.Collections.Generic;
using RetryProxy.Core.Balance;
using Xunit;

namespace RetryProxy.Tests;

public sealed class BalanceTextTests
{
    private static BalanceSnapshot Snapshot(decimal? amount = null, string unit = "USD", bool invalid = false,
        bool unlimited = false, string? error = null) => new(
        new BalanceResult(amount, unit, unlimited, invalid, null, error), null, false, true);

    [Theory]
    [InlineData("12.3", "12.30 USD", false)]
    [InlineData("0", "0.00 USD", true)]
    [InlineData("-0.001", "-0.001 USD", true)]
    [InlineData("0.00000001", "0.00000001 USD", false)]
    public void AmountsKeepPrecisionAndCriticalState(string value, string expected, bool critical)
    {
        var snapshot = Snapshot(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected, BalanceText.Display(snapshot));
        Assert.Equal(critical, BalanceText.IsCritical(snapshot));
    }

    [Fact]
    public void InactiveUnlimitedFailureAndDisabledHaveDistinctStates()
    {
        Assert.Equal("Key 已失效", BalanceText.Display(Snapshot(invalid: true)));
        Assert.True(BalanceText.IsCritical(Snapshot(invalid: true)));
        Assert.Equal("不限额", BalanceText.Display(Snapshot(unlimited: true)));
        Assert.False(BalanceText.IsCritical(Snapshot(0, unlimited: true)));
        Assert.Equal("—", BalanceText.Display(Snapshot(0, error: "查询失败")));
        Assert.False(BalanceText.IsCritical(Snapshot(0, error: "查询失败")));
        Assert.Equal(string.Empty, BalanceText.Display(BalanceSnapshot.Empty));
        Assert.Equal("不查询", BalanceText.Hint(BalanceSnapshot.Empty));
        Assert.Equal("12.00", BalanceText.Display(Snapshot(12, unit: string.Empty)));
    }

    [Fact]
    public void RefreshKeepsLastValueAndIncludesTimeAndPersistenceWarning()
    {
        var checkedAt = new DateTimeOffset(2026, 9, 30, 3, 4, 0, TimeSpan.Zero);
        var snapshot = Snapshot(12.3m) with { CheckedAt = checkedAt, IsRefreshing = true,
            PersistenceError = "已查询余额，但识别方式保存失败" };
        Assert.Equal("12.30 USD", BalanceText.Display(snapshot));
        var hint = BalanceText.Hint(snapshot);
        Assert.Contains("查询于 " + checkedAt.ToLocalTime().ToString("HH:mm"), hint);
        Assert.Contains(snapshot.PersistenceError, hint);
        Assert.Contains("正在刷新余额…", hint);
        var pending = new BalanceSnapshot(null, null, true, true);
        Assert.Equal("查询中…", BalanceText.Display(pending));
        Assert.Equal("查询中…", BalanceText.Hint(pending));
        Assert.Equal("尚未查询余额", BalanceText.Hint(pending with { IsRefreshing = false }));
    }

    [Fact]
    public void DynamicHttpErrorUsesTranslatedTemplate()
    {
        var translations = new Dictionary<string, string>
        {
            ["余额查询失败：服务商返回 HTTP {0}"] = "HTTP {0} returned by the provider",
            ["Key 已失效"] = "Inactive key",
            ["不限额"] = "Unlimited",
        };
        string T(string text) => translations.GetValueOrDefault(text) ?? text;
        var snapshot = Snapshot(error: "余额查询失败：服务商返回 HTTP 503") with { CheckedAt = DateTimeOffset.UtcNow };
        Assert.StartsWith("HTTP 503 returned by the provider", BalanceText.Hint(snapshot, T));
        Assert.Equal("Inactive key", BalanceText.Display(Snapshot(invalid: true), T));
        Assert.Equal("Unlimited", BalanceText.Display(Snapshot(unlimited: true), T));
    }
}
