using System;
using System.Collections.Generic;

namespace RetryProxy.Core.Workspace;

/// <summary>窗口外框矩形（物理像素），对应 window_recovery.rs 的 WindowRect；ToString 与 Rust Debug 输出一致，验收脚本按它匹配日志。</summary>
public readonly record struct WindowRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => (int)Math.Max(Math.Min((long)Right - Left, int.MaxValue), 0);

    public int Height => (int)Math.Max(Math.Min((long)Bottom - Top, int.MaxValue), 0);

    public static WindowRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    public WindowRect Intersection(WindowRect other) => new(
        Math.Max(Left, other.Left),
        Math.Max(Top, other.Top),
        Math.Min(Right, other.Right),
        Math.Min(Bottom, other.Bottom));

    public override string ToString() => $"WindowRect {{ left: {Left}, top: {Top}, right: {Right}, bottom: {Bottom} }}";
}

/// <summary>一块显示器的工作区。</summary>
public readonly record struct MonitorArea(WindowRect Work, bool Primary);

/// <summary>按窗口当前 DPI 换算好的外框尺寸阈值（物理像素）。</summary>
public sealed record WindowMetrics(int MinWidth, int MinHeight, int DefaultWidth, int DefaultHeight, int CaptionHeight, int GripWidth, int GripHeight)
{
    /// <summary>逻辑像素 → 物理像素，向上取整。</summary>
    public static int Scaled(double value, uint dpi) => (int)Math.Ceiling(value * dpi / 96.0);

    /// <summary>窗口是否可用：尺寸不小于下限，且标题栏区域至少有 96×16 dp 落在某块显示器的工作区内。</summary>
    public bool IsUsable(WindowRect rect, IReadOnlyList<MonitorArea> monitors)
    {
        var captionBottom = (int)Math.Min(Math.Min((long)rect.Top + CaptionHeight, int.MaxValue), rect.Bottom);
        var caption = rect with { Bottom = captionBottom };
        foreach (var monitor in monitors)
        {
            var work = monitor.Work;
            var visible = caption.Intersection(work);
            if (rect.Width >= Math.Min(MinWidth, work.Width)
                && rect.Height >= Math.Min(MinHeight, work.Height)
                && visible.Width >= Math.Min(GripWidth, work.Width)
                && visible.Height >= Math.Min(GripHeight, work.Height))
            {
                return true;
            }
        }

        return false;
    }
}

public static class WindowRecoveryMath
{
    /// <summary>
    /// 当前矩形不可用时给出恢复目标：优先上次有效矩形；否则落到与来源重叠最多（并列取主显示器）的工作区居中，
    /// 尺寸沿用来源（不小于下限时）或默认尺寸，并夹在工作区内。可用或没有显示器时返回 null。
    /// </summary>
    public static WindowRect? RecoveryRect(WindowRect current, WindowRect? saved, IReadOnlyList<MonitorArea> monitors, WindowMetrics metrics)
    {
        if (metrics.IsUsable(current, monitors))
        {
            return null;
        }

        if (saved is { } usable && metrics.IsUsable(usable, monitors))
        {
            return usable;
        }

        var source = saved ?? current;
        MonitorArea? best = null;
        (long Area, bool Primary) bestKey = default;
        foreach (var monitor in monitors)
        {
            var overlap = source.Intersection(monitor.Work);
            var key = ((long)overlap.Width * overlap.Height, monitor.Primary);
            // Rust max_by_key：并列时取最后一个。
            if (best is null || key.CompareTo(bestKey) >= 0)
            {
                best = monitor;
                bestKey = key;
            }
        }

        if (best is not { } target)
        {
            return null;
        }

        var work = target.Work;
        var minWidth = Math.Min(metrics.MinWidth, work.Width);
        var minHeight = Math.Min(metrics.MinHeight, work.Height);
        var (sizeWidth, sizeHeight) = source.Width >= minWidth && source.Height >= minHeight
            ? (source.Width, source.Height)
            : (metrics.DefaultWidth, metrics.DefaultHeight);
        var width = Math.Clamp(sizeWidth, minWidth, work.Width);
        var height = Math.Clamp(sizeHeight, minHeight, work.Height);
        var left = work.Left + (work.Width - width) / 2;
        var top = work.Top + (work.Height - height) / 2;
        return WindowRect.FromSize(left, top, width, height);
    }
}

/// <summary>恢复节流：矩形持续异常 250 ms 才动手，失败后每 5 秒重试一次。</summary>
public sealed class RecoveryGate
{
    public static readonly TimeSpan ConfirmDelay = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private TimeSpan? _invalidSince;
    private TimeSpan? _lastAttempt;

    /// <summary>正在等待确认或等待下一次重试；此时界面需要定时轮询。</summary>
    public bool Pending => _invalidSince.HasValue;

    public bool FirstAttempt => _lastAttempt is null;

    public void Reset()
    {
        _invalidSince = null;
        _lastAttempt = null;
    }

    public bool Ready(TimeSpan now)
    {
        var invalidSince = _invalidSince ??= now;
        if (now - invalidSince < ConfirmDelay || (_lastAttempt is { } last && now - last < RetryInterval))
        {
            return false;
        }

        _lastAttempt = now;
        return true;
    }
}
