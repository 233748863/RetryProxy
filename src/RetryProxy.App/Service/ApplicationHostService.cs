using Microsoft.Extensions.Hosting;
using RetryProxy.Service.Interface;
using RetryProxy.View;
using RetryProxy.View.Pages;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui;

namespace RetryProxy.Service;

/// <summary>
/// Managed host of the application.
/// </summary>
public class ApplicationHostService(IServiceProvider serviceProvider) : IHostedService
{
    private INavigationWindow? _navigationWindow;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await HandleActivationAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }

    /// <summary>
    /// Creates main window during activation.
    /// </summary>
    private async Task HandleActivationAsync()
    {
        if (!Application.Current.Windows.OfType<MainWindow>().Any())
        {
            _navigationWindow = (serviceProvider.GetService(typeof(INavigationWindow)) as INavigationWindow)!;
            var startMinimized = (serviceProvider.GetService(typeof(IConfigService)) as IConfigService)?.Get().CommonConfig.StartMinimized == true;
            if (startMinimized && _navigationWindow is Window window)
            {
                // 先以最小化状态 Show 让托盘图标与页面完成加载，再藏起来；双击托盘或菜单“显示窗口”会还原。
                window.WindowState = WindowState.Minimized;
                _navigationWindow.ShowWindow();
                window.Hide();
            }
            else
            {
                _navigationWindow.ShowWindow();
            }

            _ = _navigationWindow.Navigate(typeof(HomePage));
            (serviceProvider.GetService(typeof(WorkspaceService)) as WorkspaceService)?.Start();
        }

        await Task.CompletedTask;
    }
}
