using System;
using System.Globalization;

namespace RetryProxy.Core.Balance;

/// <summary>Key 行与托盘共用余额文案；显示与颜色均根据原始数值判断，例如 -0.001 仍标红。</summary>
public static class BalanceText
{
    public static string Display(BalanceSnapshot snapshot, Func<string, string>? translate = null)
    {
        string T(string text) => translate?.Invoke(text) ?? text;
        if (!snapshot.IsEnabled) return string.Empty;
        if (snapshot.Result is not { } result) return snapshot.IsRefreshing ? T("查询中…") : "—";
        if (result.Error is not null) return "—";
        if (result.IsInvalid) return T("Key 已失效");
        if (result.IsUnlimited) return T("不限额");
        if (result.Amount is not { } amount) return "—";
        var number = amount.ToString("0.00##########################", CultureInfo.InvariantCulture);
        return result.Unit.Length == 0 ? number : $"{number} {result.Unit}";
    }

    public static bool IsCritical(BalanceSnapshot snapshot) => snapshot.IsEnabled
        && snapshot.Result is { Error: null } result
        && (result.IsInvalid || !result.IsUnlimited && result.Amount <= 0m);

    public static string Hint(BalanceSnapshot snapshot, Func<string, string>? translate = null)
    {
        string T(string text) => translate?.Invoke(text) ?? text;
        if (!snapshot.IsEnabled) return T("不查询");
        var text = snapshot.Result?.Error is { } error ? Error(error, T) : Display(snapshot, translate);
        if (snapshot.CheckedAt is { } time)
            text += "\n" + string.Format(CultureInfo.CurrentCulture, T("查询于 {0}"), time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
        else if (!snapshot.IsRefreshing) text = T("尚未查询余额");
        if (snapshot.PersistenceError is { } persistenceError) text += "\n" + T(persistenceError);
        if (snapshot.IsRefreshing && snapshot.Result is not null) text += "\n" + T("正在刷新余额…");
        return text;
    }

    private static string Error(string error, Func<string, string> translate)
    {
        const string httpPrefix = "余额查询失败：服务商返回 HTTP ";
        if (error.StartsWith(httpPrefix, StringComparison.Ordinal)
            && int.TryParse(error.AsSpan(httpPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            return string.Format(CultureInfo.CurrentCulture, translate("余额查询失败：服务商返回 HTTP {0}"), code);
        return translate(error);
    }
}
