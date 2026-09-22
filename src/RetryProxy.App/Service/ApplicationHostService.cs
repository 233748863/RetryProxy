using Microsoft.Extensions.Hosting;
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
            _navigationWindow.ShowWindow();
            _ = _navigationWindow.Navigate(typeof(HomePage));
            (serviceProvider.GetService(typeof(WorkspaceService)) as WorkspaceService)?.Start();
        }

        await Task.CompletedTask;
    }
}
