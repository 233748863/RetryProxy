using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetryProxy.Helpers;
using RetryProxy.Helpers.Extensions;
using RetryProxy.Helpers.Win32;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using RetryProxy.Service.Interface;
using RetryProxy.View;
using RetryProxy.View.Pages;
using RetryProxy.ViewModel;
using RetryProxy.ViewModel.Pages;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;
using Wpf.Ui.Violeta.Appearance;
using Wpf.Ui.Violeta.Controls;

namespace RetryProxy;

public partial class App : Application
{
    private const string SingleInstanceName = "LLM Retry Proxy_SingleInstance";

    // The .NET Generic Host provides dependency injection, configuration, logging, and other services.
    private static readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddDebug();
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddFilter("Microsoft", LogLevel.Warning);
        })
        .ConfigureServices((context, services) =>
        {
            // 提前初始化配置
            var configService = new ConfigService();
            services.AddSingleton<IConfigService>(sp => configService);
            var all = configService.Get();

            var i18nService = I18nService.Instance;
            i18nService.ChangeLanguage(all.OtherConfig.UiCultureInfoName);
            services.AddSingleton(i18nService);

            services.AddNavigationViewPageProvider();
            services.AddHostedService<ApplicationHostService>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<ISnackbarService, SnackbarService>();

            // Main window with navigation
            services.AddView<INavigationWindow, MainWindow, MainWindowViewModel>();
            services.AddSingleton<NotifyIconViewModel>();

            // Pages
            services.AddView<HomePage, HomePageViewModel>();
            services.AddView<CachePage, CachePageViewModel>();
            services.AddView<SettingsPage, SettingsPageViewModel>();
            services.AddView<AboutPage, AboutPageViewModel>();

            I18N.Culture = new CultureInfo("zh-Hans");
        })
        .Build();

    public static IServiceProvider ServiceProvider => _host.Services;

    public static ILogger<T> GetLogger<T>()
    {
        return _host.Services.GetService<ILogger<T>>()!;
    }

    /// <summary>
    /// Gets registered service.
    /// </summary>
    public static T? GetService<T>() where T : class
    {
        return _host.Services.GetService(typeof(T)) as T;
    }

    /// <summary>
    /// Gets registered service.
    /// </summary>
    public static object? GetService(Type type)
    {
        return _host.Services.GetService(type);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
        base.OnStartup(e);

        try
        {
            RuntimeHelper.CheckSingleInstance(SingleInstanceName);
            if (RuntimeHelper.IsDebug)
            {
                ConsoleHelper.AllocateConsole("LLM Retry Proxy Console");
            }

            RegisterEvents();
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ConsoleHelper.WriteError($"应用程序启动失败: {ex.Message}");

            var dialogShown = false;
            try
            {
                dialogShown = HandleException(ex, isTerminating: true);
            }
            catch (Exception ex2)
            {
                Debug.WriteLine(ex2);
            }

            if (!dialogShown)
            {
                try
                {
                    System.Windows.Forms.MessageBox.Show(
                        $"应用程序启动失败：{ex.Message}",
                        "LLM Retry Proxy 启动失败",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Error);
                }
                catch
                {
                    // 弹窗失败不影响退出。
                }
            }

            Shutdown();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        ConsoleHelper.WriteLine("LLM Retry Proxy 正在关闭...");

        TempManager.CleanUp();

        await _host.StopAsync();
        _host.Dispose();

        ConsoleHelper.FreeConsoleWindow();
    }

    private void RegisterEvents()
    {
        TaskScheduler.UnobservedTaskException += TaskSchedulerUnobservedTaskException;
        DispatcherUnhandledException += AppDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainUnhandledException;
    }

    private static void TaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            HandleException(e.Exception);
        }
        catch (Exception ex)
        {
            HandleException(ex);
        }
        finally
        {
            e.SetObserved();
        }
    }

    private static void CurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception exception)
            {
                HandleException(exception, isTerminating: e.IsTerminating);
            }
        }
        catch (Exception ex)
        {
            HandleException(ex, isTerminating: e.IsTerminating);
        }
    }

    private static void AppDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            HandleException(e.Exception);
        }
        catch (Exception ex)
        {
            HandleException(ex);
        }
        finally
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 处理未处理异常。返回 true 表示已尝试显示弹窗，false 表示未弹窗。
    /// </summary>
    private static bool HandleException(Exception e, bool isTerminating = false)
    {
        if (e.InnerException != null)
        {
            e = e.InnerException;
        }

        var logMessage = isTerminating ? "UnHandle Exception [FATAL]" : "UnHandle Exception";
        GetLogger<App>().LogError(e, logMessage);

        if (!isTerminating)
        {
            return false;
        }

        var dispatcher = Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                ShowExceptionDialog(e);
            }
            else
            {
                using var startedSignal = new System.Threading.ManualResetEventSlim(false);
                using var completedSignal = new System.Threading.ManualResetEventSlim(false);
                dispatcher.InvokeAsync(new Action(() =>
                {
                    try
                    {
                        startedSignal.Set();
                        ShowExceptionDialog(e);
                    }
                    finally
                    {
                        completedSignal.Set();
                    }
                }));

                if (!startedSignal.Wait(TimeSpan.FromSeconds(3)))
                {
                    GetLogger<App>().LogWarning("弹窗调度超时，异常已记录，进程即将退出。");
                    return false;
                }

                completedSignal.Wait();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowExceptionDialog(Exception e)
    {
        try
        {
            ExceptionReport.Show(e);
        }
        catch
        {
            System.Windows.Forms.MessageBox.Show(
                $"""
                 程序异常：{e.Source}
                 --
                 {e.StackTrace}
                 --
                 {e.Message}
                 """
            );
        }
    }
}
