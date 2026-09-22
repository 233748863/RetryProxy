using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RetryProxy.Helpers.Win32;

/// <summary>任务栏闪烁提醒（准备完成/未完成/终止等通知时使用）。</summary>
public static class WindowFlash
{
    private const uint FlashwAll = 3;
    private const uint FlashwTimerNoFg = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>闪烁主窗口的任务栏按钮，直到窗口获得焦点。</summary>
    public static void Flash(Window? window)
    {
        if (window is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = handle,
            dwFlags = FlashwAll | FlashwTimerNoFg,
            uCount = 3,
            dwTimeout = 0,
        };
        _ = FlashWindowEx(ref info);
    }
}
