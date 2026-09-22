using RetryProxy.Core.Logging;
using RetryProxy.Core.Workspace;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RetryProxy.Helpers.Win32;

/// <summary>
/// 主窗口位置异常自动恢复（复刻 window_recovery.rs）：窗口被系统摆到 (-32000,-32000) 或缩成 160×28 等无法操作的矩形时，
/// 持续 250 ms 后把它放回上次有效位置，否则在重叠最多的显示器工作区居中；失败每 5 秒重试一次。
/// Rust 版每帧轮询，这里改为监听窗口位置/显示器变化消息，仅在待确认或待重试期间开 300 ms 定时器。
/// </summary>
public sealed class WindowRecovery : IDisposable
{
    private const int WmSize = 0x0005;
    private const int WmShowWindow = 0x0018;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmDisplayChange = 0x007E;
    private const int WmExitSizeMove = 0x0232;
    private const int WmDpiChanged = 0x02E0;

    private static readonly TimeSpan PendingInterval = TimeSpan.FromMilliseconds(300);

    private readonly Window _window;
    private readonly ProxyLogger _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RecoveryGate _gate = new();
    private readonly DispatcherTimer _timer;
    private readonly double _minWidth;
    private readonly double _minHeight;
    private readonly double _defaultWidth;
    private readonly double _defaultHeight;
    private HwndSource? _source;
    private nint _hwnd;
    private WindowRect? _saved;
    /// <summary>首次发现异常时的矩形；恢复成功（无论由哪一次轮询确认）时都要记一条日志。</summary>
    private WindowRect? _damaged;
    private bool _suspended;
    private bool _pollQueued;

    public WindowRecovery(Window window, ProxyLogger logger)
    {
        _window = window;
        _logger = logger;
        _minWidth = window.MinWidth;
        _minHeight = window.MinHeight;
        _defaultWidth = window.Width;
        _defaultHeight = window.Height;
        _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = PendingInterval };
        _timer.Tick += (_, _) => Poll();
        window.SourceInitialized += OnSourceInitialized;
        window.IsVisibleChanged += OnVisibleChanged;
        window.Closing += (_, _) => Suspend();
    }

    /// <summary>正在等待确认或等待下一次重试。</summary>
    public bool Pending => _gate.Pending;

    /// <summary>窗口藏入托盘或准备退出时停止监视。</summary>
    public void Suspend()
    {
        _gate.Reset();
        _timer.Stop();
        _damaged = null;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(Hook);
        QueuePoll();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _suspended = e.NewValue is false;
        if (_suspended)
        {
            Suspend();
        }
        else
        {
            QueuePoll();
        }
    }

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmWindowPosChanged:
            case WmExitSizeMove:
            case WmDisplayChange:
            case WmDpiChanged:
            case WmSize:
            case WmShowWindow:
                QueuePoll();
                break;
        }

        return 0;
    }

    /// <summary>不在消息处理里直接 SetWindowPos，合并到下一次空闲时检查。</summary>
    private void QueuePoll()
    {
        if (_pollQueued || _hwnd == 0)
        {
            return;
        }

        _pollQueued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pollQueued = false;
            Poll();
        }));
    }

    private void Poll()
    {
        if (_suspended || _hwnd == 0)
        {
            Suspend();
            return;
        }

        if (Inspect() is not var (current, metrics))
        {
            Suspend();
            return;
        }

        var monitors = MonitorAreas();
        if (monitors.Count == 0)
        {
            Suspend();
            return;
        }

        if (metrics.IsUsable(current, monitors))
        {
            _saved = current;
            var damaged = _damaged;
            Suspend();
            if (damaged is { } from)
            {
                // SetWindowPos 之后 WPF 可能还要再走一轮布局，成功可能要到下一次轮询才能确认。
                _logger.Warn($"窗口位置或尺寸异常，已自动恢复：{from} -> {current}");
            }
            return;
        }

        // 异常矩形：从现在起定时轮询，直到恢复成功或窗口被藏起。
        _damaged ??= current;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }

        var firstAttempt = _gate.FirstAttempt;
        if (!_gate.Ready(_clock.Elapsed))
        {
            return;
        }

        if (WindowRecoveryMath.RecoveryRect(current, _saved, monitors, metrics) is not { } target)
        {
            return;
        }

        var succeeded = SetWindowPos(_hwnd, 0, target.Left, target.Top, target.Width, target.Height, SwpNoActivate | SwpNoZOrder);
        if (succeeded && Inspect() is var (restored, restoredMetrics) && restoredMetrics.IsUsable(restored, monitors))
        {
            var from = _damaged ?? current;
            _saved = restored;
            Suspend();
            _logger.Warn($"窗口位置或尺寸异常，已自动恢复：{from} -> {restored}");
            return;
        }

        if (firstAttempt)
        {
            _logger.Warn($"窗口位置或尺寸恢复未完成，将间隔 5 秒重试：{current} -> {target}");
        }
    }

    /// <summary>窗口不可见、最小化、最大化或正在被拖动时返回 null（这些状态下的矩形不算异常）。</summary>
    private (WindowRect Rect, WindowMetrics Metrics)? Inspect()
    {
        if (!IsWindowVisible(_hwnd) || IsIconic(_hwnd) || IsZoomed(_hwnd))
        {
            return null;
        }

        var threadInfo = new GuiThreadInfo { cbSize = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        var threadId = GetWindowThreadProcessId(_hwnd, 0);
        if (threadId == 0 || !GetGUIThreadInfo(threadId, ref threadInfo) || (threadInfo.flags & GuiInMoveSize) != 0)
        {
            return null;
        }

        var dpi = GetDpiForWindow(_hwnd);
        if (dpi == 0 || !GetWindowRect(_hwnd, out var rect))
        {
            return null;
        }

        var style = unchecked((uint)GetWindowLongW(_hwnd, GwlStyle));
        var extendedStyle = unchecked((uint)GetWindowLongW(_hwnd, GwlExStyle));
        if (OuterSize(_minWidth, _minHeight) is not var (minWidth, minHeight)
            || OuterSize(_defaultWidth, _defaultHeight) is not var (defaultWidth, defaultHeight))
        {
            return null;
        }

        return (
            new WindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
            new WindowMetrics(
                minWidth,
                minHeight,
                defaultWidth,
                defaultHeight,
                WindowMetrics.Scaled(32.0, dpi),
                WindowMetrics.Scaled(96.0, dpi),
                WindowMetrics.Scaled(16.0, dpi)));

        (int Width, int Height)? OuterSize(double innerWidth, double innerHeight)
        {
            var bounds = new Rect { Right = WindowMetrics.Scaled(innerWidth, dpi), Bottom = WindowMetrics.Scaled(innerHeight, dpi) };
            return AdjustWindowRectExForDpi(ref bounds, style, false, extendedStyle, dpi)
                ? (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)
                : null;
        }
    }

    private static List<MonitorArea> MonitorAreas()
    {
        var monitors = new List<MonitorArea>();
        var succeeded = EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref Rect _, nint _) =>
        {
            var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                var work = new WindowRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
                if (work.Width > 0 && work.Height > 0)
                {
                    monitors.Add(new MonitorArea(work, (info.dwFlags & MonitorInfoPrimary) != 0));
                }
            }

            return true;
        }, 0);
        if (!succeeded)
        {
            monitors.Clear();
        }

        return monitors;
    }

    public void Dispose()
    {
        Suspend();
        _source?.RemoveHook(Hook);
        _source = null;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.IsVisibleChanged -= OnVisibleChanged;
    }

    // ---- Win32 ----

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GuiInMoveSize = 0x1;
    private const uint MonitorInfoPrimary = 0x1;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public Rect rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect bounds, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, nint processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool AdjustWindowRectExForDpi(ref Rect rect, uint style, bool menu, uint extendedStyle, uint dpi);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
