using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RetryProxy.Core.Config;
using RetryProxy.Helpers;
using RetryProxy.Helpers.Ui;
using RetryProxy.Service.Interface;
using RetryProxy.View;
using System.ComponentModel;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RetryProxy.ViewModel;

public partial class MainWindowViewModel : ObservableObject, IViewModel
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IConfigService _configService;
    private bool _isFirstActivation = true;

    public string Title => "LLM Retry Proxy";

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private WindowState _windowState = WindowState.Normal;

    [ObservableProperty]
    private WindowBackdropType _currentBackdropType = WindowBackdropType.Auto;

    [ObservableProperty]
    private bool _isWin11Later;

    public AllConfig Config { get; }

    public MainWindowViewModel(ILogger<MainWindowViewModel> logger, IConfigService configService)
    {
        _logger = logger;
        _configService = configService;
        Config = _configService.Get();
        IsWin11Later = OsVersionHelper.IsWindows11_22523_OrGreater;
    }

    [RelayCommand]
    private void OnHide()
    {
        IsVisible = false;
    }

    [RelayCommand]
    private void OnSwitchBackdrop()
    {
        var current = Config.CommonConfig.CurrentThemeType;
        ThemeType next;
        if (!OsVersionHelper.IsWindows11_22523_OrGreater)
        {
            next = current is ThemeType.DarkNone or ThemeType.DarkMica or ThemeType.DarkAcrylic
                ? ThemeType.LightNone
                : ThemeType.DarkNone;
        }
        else
        {
            next = current switch
            {
                ThemeType.DarkMica => ThemeType.DarkAcrylic,
                ThemeType.DarkAcrylic => ThemeType.LightMica,
                ThemeType.LightMica => ThemeType.LightAcrylic,
                ThemeType.LightAcrylic => ThemeType.DarkMica,
                ThemeType.DarkNone => ThemeType.LightNone,
                ThemeType.LightNone => ThemeType.DarkMica,
                _ => ThemeType.DarkMica,
            };
        }

        Config.CommonConfig.CurrentThemeType = next;
        ApplyTheme(next);
        _configService.Save();
    }

    private void ApplyTheme(ThemeType themeType)
    {
        if (!OsVersionHelper.IsWindows11_22523_OrGreater
            && themeType is not (ThemeType.DarkNone or ThemeType.LightNone))
        {
            var fallback = themeType is ThemeType.LightMica or ThemeType.LightAcrylic
                ? ThemeType.LightNone
                : ThemeType.DarkNone;
            _logger.LogInformation("当前系统不支持 Mica/Acrylic，主题 {Theme} 回退为 {Fallback}", themeType, fallback);
            themeType = fallback;
            Config.CommonConfig.CurrentThemeType = fallback;
            _configService.Save();
        }

        var isLight = themeType is ThemeType.LightNone or ThemeType.LightMica or ThemeType.LightAcrylic;
        ApplicationThemeManager.Apply(isLight ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.None, false);

        CurrentBackdropType = themeType switch
        {
            ThemeType.DarkMica or ThemeType.LightMica => WindowBackdropType.Mica,
            ThemeType.DarkAcrylic or ThemeType.LightAcrylic => WindowBackdropType.Acrylic,
            _ => WindowBackdropType.None,
        };

        if (Application.Current.MainWindow is { } mainWindow)
        {
            WindowHelper.ApplyThemeToWindow(mainWindow, themeType);
            if (mainWindow is MainWindow retryProxyWindow)
            {
                retryProxyWindow.QueueNavigationRefresh();
            }
        }
    }

    [RelayCommand]
    private void OnClosing(CancelEventArgs e)
    {
        if (Config.CommonConfig.ExitToTray)
        {
            e.Cancel = true;
            OnHide();
        }
    }

    [RelayCommand]
    private void OnLoaded()
    {
        ApplyTheme(Config.CommonConfig.CurrentThemeType);

        if (Config.CommonConfig.RunForVersion != Global.Version)
        {
            Config.CommonConfig.RunForVersion = Global.Version;
        }

        if (Config.CommonConfig.IsFirstRun)
        {
            Config.CommonConfig.IsFirstRun = false;
        }

        _configService.Save();
    }

    [RelayCommand]
    private void OnActivated()
    {
        if (_isFirstActivation)
        {
            _isFirstActivation = false;
        }
    }
}
