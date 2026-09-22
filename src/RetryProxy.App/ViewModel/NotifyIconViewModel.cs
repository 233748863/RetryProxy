using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RetryProxy.Service.Interface;
using System;
using System.Windows;
using System.Windows.Interop;
using Vanara.PInvoke;

namespace RetryProxy.ViewModel;

public partial class NotifyIconViewModel : ObservableObject
{
    private readonly ILogger<NotifyIconViewModel> _logger;

    public NotifyIconViewModel(ILogger<NotifyIconViewModel> logger)
    {
        _logger = logger;
    }

    [RelayCommand]
    public void ShowOrHide()
    {
        if (Application.Current.MainWindow.Visibility == Visibility.Visible)
        {
            Application.Current.MainWindow.Hide();
        }
        else
        {
            Application.Current.MainWindow.Activate();
            Application.Current.MainWindow.Focus();
            Application.Current.MainWindow.Show();
            WindowBacktray.Show(Application.Current.MainWindow);
        }
    }

    /// <summary>
    /// 启用全部通道。M0 占位，M2 接入代理服务。
    /// </summary>
    [RelayCommand]
    public void EnableAll()
    {
        _logger.LogInformation("托盘：全部启用（占位）");
    }

    /// <summary>
    /// 停用全部通道。M0 占位，M2 接入代理服务。
    /// </summary>
    [RelayCommand]
    public void DisableAll()
    {
        _logger.LogInformation("托盘：全部停用（占位）");
    }

    [RelayCommand]
    public void Exit()
    {
        App.GetService<IConfigService>()?.Save();
        Application.Current.Shutdown();
    }
}

file static class WindowBacktray
{
    public static void Hide(Window window)
    {
        if (window != null)
        {
            window.Visibility = Visibility.Hidden;
            window.WindowState = WindowState.Minimized;
        }
    }

    public static void Show(Window window)
    {
        if (window != null)
        {
            if (window.Visibility != Visibility.Visible)
            {
                window.Visibility = Visibility.Visible;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                nint hWnd = new WindowInteropHelper(Application.Current.MainWindow).Handle;
                _ = User32.SendMessage(hWnd, User32.WindowMessage.WM_SYSCOMMAND, User32.SysCommand.SC_RESTORE, IntPtr.Zero);
            }
        }
    }
}
