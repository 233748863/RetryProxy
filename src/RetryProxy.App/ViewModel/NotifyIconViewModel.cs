using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RetryProxy.Service;
using RetryProxy.Service.Interface;
using System;
using System.Windows;
using System.Windows.Interop;
using Vanara.PInvoke;

namespace RetryProxy.ViewModel;

public partial class NotifyIconViewModel : ObservableObject
{
    private readonly ILogger<NotifyIconViewModel> _logger;
    private readonly WorkspaceService _workspaceService;

    public NotifyIconViewModel(ILogger<NotifyIconViewModel> logger, WorkspaceService workspaceService)
    {
        _logger = logger;
        _workspaceService = workspaceService;
    }

    [RelayCommand]
    public void ShowOrHide()
    {
        var window = Application.Current.MainWindow;
        if (window.Visibility == Visibility.Visible && window.WindowState != WindowState.Minimized)
        {
            WindowBacktray.Hide(window);
        }
        else
        {
            WindowBacktray.Show(window);
            window.Activate();
            window.Focus();
        }
    }

    /// <summary>启用全部通道。</summary>
    [RelayCommand]
    public void EnableAll()
    {
        _logger.LogInformation("托盘：全部启用");
        _workspaceService.Workspace.StartAll();
        _workspaceService.Flush();
    }

    /// <summary>停用全部通道。</summary>
    [RelayCommand]
    public void DisableAll()
    {
        _logger.LogInformation("托盘：全部停用");
        _workspaceService.Workspace.StopAll();
        _workspaceService.Flush();
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
    /// <summary>只改可见性，不改 WindowState：还原时能回到隐藏前的普通/最大化状态。</summary>
    public static void Hide(Window window)
    {
        if (window != null)
        {
            window.Visibility = Visibility.Hidden;
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
