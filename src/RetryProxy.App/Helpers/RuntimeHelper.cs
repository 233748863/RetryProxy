using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Vanara.PInvoke;

namespace RetryProxy.Helpers;

internal static class RuntimeHelper
{
    public static bool IsElevated { get; } = GetElevated();
    public static bool IsDebuggerAttached => Debugger.IsAttached;
    public static bool IsDesignMode { get; } = GetDesignMode();

    public static bool IsDebug =>
#if DEBUG
        true;
#else
        false;
#endif

    private static bool GetElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool GetDesignMode()
    {
        if (LicenseManager.UsageMode == LicenseUsageMode.Designtime)
        {
            return true;
        }

        return Process.GetCurrentProcess().ProcessName == "devenv";
    }

    /// <summary>
    /// 单实例检查：已有实例时唤醒它并退出当前进程。
    /// </summary>
    public static void CheckSingleInstance(string instanceName, Action<bool>? callback = null)
    {
        EventWaitHandle? handle;

        try
        {
            handle = EventWaitHandle.OpenExisting(instanceName);
            handle.Set();
            callback?.Invoke(false);
            Environment.Exit(0xFFFF);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            callback?.Invoke(true);
            handle = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName);
        }

        _ = Task.Factory.StartNew(() =>
        {
            while (handle.WaitOne())
            {
                Application.Current?.Dispatcher?.BeginInvoke(() =>
                {
                    var mainWindow = Application.Current.MainWindow;
                    if (mainWindow == null)
                    {
                        return;
                    }

                    mainWindow.Show();
                    mainWindow.Activate();
                    var hWnd = new WindowInteropHelper(mainWindow).Handle;
                    _ = User32.SendMessage(hWnd, User32.WindowMessage.WM_SYSCOMMAND, User32.SysCommand.SC_RESTORE, IntPtr.Zero);
                });
            }
        }, TaskCreationOptions.LongRunning).ConfigureAwait(false);
    }
}
