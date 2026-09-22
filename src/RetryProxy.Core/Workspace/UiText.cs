using System;
using System.Globalization;
using RetryProxy.Core.Service;

namespace RetryProxy.Core.Workspace;

/// <summary>界面上的小型文案函数（对应 ui.rs 的 parse_idle_minutes / format_duration_cn / trim_float / state_label）。</summary>
public static class UiText
{
    /// <summary>分钟数输入框的解析。空的或者认不出来的返回 null，由调用方回落到原值。</summary>
    public static double? ParseIdleMinutes(string text)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return double.IsFinite(value) && value > 0.0 ? value : null;
    }

    /// <summary>把时长说成中文：秒、分、小时各有各的写法。</summary>
    public static string FormatDurationCn(TimeSpan duration)
    {
        var total = (long)Math.Floor(Math.Max(0, duration.TotalSeconds));
        if (total < 60)
        {
            return $"{total} 秒";
        }

        if (total < 3600)
        {
            var minutes = total / 60;
            var seconds = total % 60;
            return seconds == 0 ? $"{minutes} 分" : $"{minutes} 分 {seconds} 秒";
        }

        var hours = total / 3600;
        var remaining = (total % 3600) / 60;
        return remaining == 0 ? $"{hours} 小时" : $"{hours} 小时 {remaining} 分";
    }

    /// <summary>去掉浮点数末尾多余的 0，让 300 秒不显示成 300.0。</summary>
    public static string TrimFloat(double value)
    {
        var text = value.ToString("F2", CultureInfo.InvariantCulture);
        var trimmed = text.TrimEnd('0').TrimEnd('.');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    public static string StateLabel(ServiceState state) => state switch
    {
        ServiceState.Stopped => "已停止",
        ServiceState.Starting => "启动中",
        ServiceState.Running => "运行中",
        ServiceState.Stopping => "停止中",
        _ => "异常",
    };
}
