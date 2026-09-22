using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using RetryProxy.Service.Interface;
using RetryProxy.View.Converters;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RetryProxy.ViewModel.Pages;

public partial class SettingsPageViewModel : ViewModel
{
    private readonly IConfigService _configService;
    private readonly WorkspaceService _workspaceService;
    private bool _syncingAutoStart;

    public AllConfig Config { get; }

    /// <summary>
    /// 可选界面语言。zh-Hans 为源文案，其余需存在 User/I18n/{lang}.json。
    /// </summary>
    [ObservableProperty]
    private FrozenDictionary<string, string> _languageDict =
        new[] { "zh-Hans", "en" }.ToFrozenDictionary(c => c, CultureInfoNameToKVPConverter.GetDisplayName);

    /// <summary>开机自动启动 = 注册表 Run 项是否存在。</summary>
    [ObservableProperty]
    private bool _autoStartEnabled;

    /// <summary>运行日志目录（retry-proxy.log 所在）。</summary>
    [ObservableProperty]
    private string _logDirectory = string.Empty;

    /// <summary>每日统计目录（daily-statistics）。</summary>
    [ObservableProperty]
    private string _statisticsDirectory = string.Empty;

    public SettingsPageViewModel(IConfigService configService, WorkspaceService workspaceService)
    {
        _configService = configService;
        _workspaceService = workspaceService;
        Config = configService.Get();
        LogDirectory = workspaceService.Workspace.Logger.DirectoryPath;
        StatisticsDirectory = Path.Combine(LogDirectory, "daily-statistics");
        _syncingAutoStart = true;
        AutoStartEnabled = AutoStart.IsEnabled();
        _syncingAutoStart = false;
    }

    public override void OnNavigatedTo()
    {
        // 用户可能在外部改过 Run 项，每次进入设置页重读一次。
        _syncingAutoStart = true;
        AutoStartEnabled = AutoStart.IsEnabled();
        _syncingAutoStart = false;
    }

    [RelayCommand]
    private void OnUiLanguageSelectionChanged(object? value)
    {
        if (value is not KeyValuePair<string, string> language)
        {
            return;
        }

        Config.OtherConfig.UiCultureInfoName = language.Key;
        I18nService.Instance.ChangeLanguage(language.Key);
        _configService.Save();
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        if (_syncingAutoStart)
        {
            return;
        }

        if (AutoStart.SetEnabled(value) is { } error)
        {
            _workspaceService.Workspace.Notice = $"开机自动启动设置失败：{error}";
            _syncingAutoStart = true;
            AutoStartEnabled = AutoStart.IsEnabled();
            _syncingAutoStart = false;
        }
    }

    [RelayCommand]
    private void OnOpenLogDirectory() => OpenDirectory(LogDirectory, "日志目录");

    [RelayCommand]
    private void OnOpenStatisticsDirectory() => OpenDirectory(StatisticsDirectory, "统计目录");

    private void OpenDirectory(string directory, string label)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (Exception)
        {
            _workspaceService.Workspace.Notice = $"{label}：{directory}";
        }
    }
}
