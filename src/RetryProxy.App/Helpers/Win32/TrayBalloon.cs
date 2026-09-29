using System;
using System.Runtime.InteropServices;
using Wpf.Ui.Tray.Controls;

namespace RetryProxy.Helpers.Win32;

/// <summary>复用现有托盘图标发送 Windows 通知，不创建第二个图标。</summary>
internal static class TrayBalloon
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Notify(uint message, ref NotifyIconData data);

    public static bool Show(NotifyIcon icon, string message)
    {
        if (!icon.IsRegistered || icon.HookWindow is null) return false;
        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = icon.HookWindow.Handle,
            Id = (uint)icon.Id,
            Flags = 0x10, // NIF_INFO：只更新通知文字，不改图标与菜单。
            Tip = string.Empty,
            Info = message.Length > 255 ? message[..255] : message,
            Title = "LLM Retry Proxy",
            InfoFlags = 1,
            Timeout = 5000,
        };
        return Notify(1, ref data);
    }
}
