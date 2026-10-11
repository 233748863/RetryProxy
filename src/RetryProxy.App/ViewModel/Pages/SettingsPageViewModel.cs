using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using System.Threading.Tasks;
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
    private readonly ClientTakeoverService _clients;
    public IReadOnlyList<ClientConnectionRowViewModel> Clients { get; }
    private bool _syncingAutoStart;

    public AllConfig Config { get; }

    private bool _syncingRetention;
    [ObservableProperty] private int _runtimeLogRetentionDays;
    [ObservableProperty] private string _cleanupResult = string.Empty;


    /// <summary>
    /// 可选界面语言。zh-Hans 为源文案，其余需存在 User/I18n/{lang}.json。
    /// </summary>
    [ObservableProperty]
    private FrozenDictionary<string, string> _languageDict =
        new[] { "zh-Hans", "en" }.ToFrozenDictionary(c => c, CultureInfoNameToKVPConverter.GetDisplayName);

    /// <summary>开机自动启动 = 注册表 Run 项是否存在。</summary>
    [ObservableProperty]
    private bool _autoStartEnabled;

    /// <summary>运行日志、请求诊断和统计的日期数据库所在目录。</summary>
    [ObservableProperty]
    private string _logDirectory = string.Empty;

    public SettingsPageViewModel(IConfigService configService, WorkspaceService workspaceService, ClientTakeoverService clients)
    {
        _configService = configService;
        _workspaceService = workspaceService;
        _clients = clients;
        Clients = [new(ClientType.Claude, workspaceService, clients), new(ClientType.Codex, workspaceService, clients)];
        Config = configService.Get();
        _runtimeLogRetentionDays = RuntimeLogStore.NormalizeRetentionDays(Config.OtherConfig.RuntimeLogRetentionDays);
        LogDirectory = workspaceService.Workspace.Logger.DirectoryPath;
        _syncingAutoStart = true;
        AutoStartEnabled = AutoStart.IsEnabled();
        _syncingAutoStart = false;
    }

    public override void OnNavigatedTo()
    {
        _workspaceService.Clients.Detect();
        _workspaceService.Flush();
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

    partial void OnRuntimeLogRetentionDaysChanged(int value)
    {
        if (_syncingRetention) return;
        var previous = Config.OtherConfig.RuntimeLogRetentionDays;
        if (value < 1 || value > RuntimeLogStore.MaxRetentionDays)
        {
            _syncingRetention = true;
            RuntimeLogRetentionDays = RuntimeLogStore.NormalizeRetentionDays(previous);
            _syncingRetention = false;
            CleanupResult = ClientPageText.Translate("运行日志保留天数须为 1–3650 天");
            return;
        }
        try
        {
            Config.OtherConfig.RuntimeLogRetentionDays = value;
            _configService.SaveChecked();
            _workspaceService.Workspace.Logger.Store.RetentionDays = value;
            CleanupResult = ClientPageText.Translate("保留天数已保存，过期运行日志将自动清理");
        }
        catch
        {
            Config.OtherConfig.RuntimeLogRetentionDays = previous;
            _syncingRetention = true;
            RuntimeLogRetentionDays = RuntimeLogStore.NormalizeRetentionDays(previous);
            _syncingRetention = false;
            CleanupResult = ClientPageText.Translate("保留天数保存失败，已恢复原设置");
        }
    }

    [RelayCommand]
    private async Task OnCleanExpiredLogs()
    {
        CleanupResult = ClientPageText.Translate("正在清理过期日志…");
        try
        {
            var store = _workspaceService.Workspace.Logger.Store;
            await store.CleanupNowAsync();
            await Task.Run(() => _workspaceService.Data.Diagnostics.DeleteBeforeAsync(_workspaceService.Diagnostics.Today.AddDays(-6)));
            CleanupResult = ClientPageText.Translate(store.CleanupWarning is null ? "过期日志清理完成" : "部分日志未能清理，请稍后重试");
        }
        catch { CleanupResult = ClientPageText.Translate("部分日志未能清理，请稍后重试"); }
    }

    [RelayCommand]
    private System.Threading.Tasks.Task ClientSetup() => _clients.ShowSetupAsync();

    [RelayCommand]
    private void OnOpenLogDirectory() => OpenDirectory(LogDirectory, "日志与数据");

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
