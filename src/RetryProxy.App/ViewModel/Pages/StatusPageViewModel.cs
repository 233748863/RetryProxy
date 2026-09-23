using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RetryProxy.ViewModel.Pages;

/// <summary>请求明细一行。</summary>
public sealed record ActiveRequestRow(string RequestId, string Attempt, string Phase, Brush PhaseBrush, string Target);

public partial class StatusPageViewModel : ViewModel
{
    private const string DateHelpBase = "按本机日期统计，午夜自动切换。请求按编号去重，重试单独计数；跨日仍未完成的请求计入新一天。\n处理中只显示当前实际请求；历史未完成表示日志没有成功或失败的结束记录，计入总请求，单独列出。\n首次升级按现有日志恢复，已被覆盖的旧日志无法补回，缺失用量保持未获取。";

    private static readonly TimeSpan CopiedLabelDuration = TimeSpan.FromMilliseconds(1600);

    private readonly WorkspaceService _workspaceService;
    private int _copyVersion;

    private ProxyWorkspace Workspace => _workspaceService.Workspace;

    [ObservableProperty]
    private string _runningSummary = string.Empty;

    [ObservableProperty]
    private Brush _runningBrush = Brushes.Gray;

    [ObservableProperty]
    private string _versionText = $"v{Global.Version}";

    [ObservableProperty]
    private string _selectedChannelLabel = string.Empty;

    [ObservableProperty]
    private bool _hasChannel;

    [ObservableProperty]
    private string _emptyHint = string.Empty;

    [ObservableProperty]
    private string _stateLabel = string.Empty;

    [ObservableProperty]
    private InfoBadgeSeverity _stateSeverity = InfoBadgeSeverity.Informational;

    [ObservableProperty]
    private string _hint = string.Empty;

    [ObservableProperty]
    private string? _hintToolTip;

    [ObservableProperty]
    private string _localUrl = string.Empty;

    [ObservableProperty]
    private string _copyLabel = "本地监听";

    [ObservableProperty]
    private Brush _copyLabelBrush = Brushes.Gray;

    [ObservableProperty]
    private string _dateCaption = string.Empty;

    [ObservableProperty]
    private Brush _dateCaptionBrush = Brushes.Gray;

    [ObservableProperty]
    private string _dateHelp = string.Empty;

    [ObservableProperty]
    private string _totalRequests = "0";

    [ObservableProperty]
    private string _successfulRequests = "0";

    [ObservableProperty]
    private string _retryCount = "0";

    [ObservableProperty]
    private string _failedRequests = "0";

    [ObservableProperty]
    private string _activeRequests = "0";

    [ObservableProperty]
    private string _hitRateText = string.Empty;

    [ObservableProperty]
    private Brush _hitRateBrush = Brushes.Gray;

    [ObservableProperty]
    private double _hitRateProgress;

    [ObservableProperty]
    private string _hitRateHelp = string.Empty;

    [ObservableProperty]
    private string _creationText = string.Empty;

    [ObservableProperty]
    private string _creationHelp = string.Empty;

    [ObservableProperty]
    private string _latestText = string.Empty;

    [ObservableProperty]
    private Brush _latestBrush = Brushes.Gray;

    [ObservableProperty]
    private string _latestHelp = string.Empty;

    [ObservableProperty]
    private string _latestDetail = string.Empty;

    [ObservableProperty]
    private string _zeroHitText = string.Empty;

    [ObservableProperty]
    private Brush _zeroHitBrush = Brushes.Gray;

    [ObservableProperty]
    private string _zeroHitHelp = string.Empty;

    [ObservableProperty]
    private string _measuredText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ActiveRequestRow> _requests = [];

    [ObservableProperty]
    private bool _hasRequests;

    [ObservableProperty]
    private string _maxRetriesText = string.Empty;

    [ObservableProperty]
    private string _timeoutsText = string.Empty;

    [ObservableProperty]
    private string _backoffText = string.Empty;

    public string RateHelp => CacheText.RateHelp;

    public StatusPageViewModel(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
        _workspaceService.Refreshed += Refresh;
        _workspaceService.Tick += Refresh;
        Refresh();
    }

    public override void OnNavigatedTo()
    {
        _workspaceService.SetHintTimerWanted(this, true);
        Refresh();
    }

    public override void OnNavigatedFrom()
    {
        _workspaceService.SetHintTimerWanted(this, false);
    }

    private static Brush ThemeBrush(string key)
    {
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    private static Brush RateBrush(double? rate) => rate switch
    {
        > 0.0 => ThemeBrush("SystemFillColorSuccessBrush"),
        { } => ThemeBrush("SystemFillColorCautionBrush"),
        null => ThemeBrush("TextFillColorSecondaryBrush"),
    };

    private void Refresh()
    {
        var running = Workspace.RunningCount();
        RunningSummary = $"{running} / {Workspace.Config.Routes.Count} 个通道在运行";
        RunningBrush = running > 0 ? ThemeBrush("SystemFillColorSuccessBrush") : ThemeBrush("TextFillColorSecondaryBrush");

        var route = Workspace.SelectedRouteRef();
        HasChannel = route is not null;
        EmptyHint = "请在首页选择或创建通道";
        SelectedChannelLabel = route is null ? string.Empty : $"{route.Name} · {route.ListenPort}";
        var state = route is null ? ServiceState.Stopped : Workspace.RouteState(route.Id);
        StateLabel = UiText.StateLabel(state);
        StateSeverity = state switch
        {
            ServiceState.Running => InfoBadgeSeverity.Success,
            ServiceState.Starting or ServiceState.Stopping => InfoBadgeSeverity.Caution,
            ServiceState.Error => InfoBadgeSeverity.Critical,
            _ => InfoBadgeSeverity.Informational,
        };
        if (route is null)
        {
            ClearRouteDetails();
            return;
        }

        RefreshKeepAlive(route);
        RefreshStatistics(route);
    }

    private void ClearRouteDetails()
    {
        Hint = string.Empty;
        HintToolTip = null;
        LocalUrl = string.Empty;
        DateCaption = string.Empty;
        Requests.Clear();
        HasRequests = false;
    }

    private void RefreshKeepAlive(ProxyRoute route)
    {
        var snapshot = Workspace.RouteKeepAlives.TryGetValue(route.Id, out var watchdog) ? watchdog.Snapshot() : null;
        Hint = Workspace.KeepAliveHint(route);
        HintToolTip = snapshot?.PreparationLastError is { } reason
            ? $"最近一次准备未完成：{reason}\n将持续重试，可在首页点击“终止准备”取消。"
            : null;
    }

    private void RefreshStatistics(ProxyRoute route)
    {
        try
        {
            LocalUrl = Workspace.Config.RuntimeConfigFor(route.Id).LocalUrl;
        }
        catch (ConfigException)
        {
            LocalUrl = route.LocalUrl;
        }

        var snapshot = Workspace.Services.TryGetValue(route.Id, out var service) ? service.Metrics.Snapshot() : new MetricsSnapshot();
        var caption = $"今日 {snapshot.StatisticsDate} · 重启保留";
        var help = DateHelpBase;
        if (snapshot.HistoricalUnfinishedRequests > 0)
        {
            caption += $" · 历史未完成 {snapshot.HistoricalUnfinishedRequests}";
        }

        if (snapshot.RestoredFromLegacyLogs)
        {
            help += "\n今日数据包含旧日志恢复记录。";
        }

        if (snapshot.StatisticsWarning is { } warning)
        {
            caption = $"今日 {snapshot.StatisticsDate} · 统计日志异常";
            help = $"{warning}\n{help}";
            DateCaptionBrush = ThemeBrush("SystemFillColorCautionBrush");
        }
        else
        {
            DateCaptionBrush = ThemeBrush("TextFillColorSecondaryBrush");
        }

        DateCaption = caption;
        DateHelp = help;
        TotalRequests = snapshot.TotalRequests.ToString();
        SuccessfulRequests = snapshot.SuccessfulRequests.ToString();
        RetryCount = snapshot.RetryCount.ToString();
        FailedRequests = snapshot.FailedRequests.ToString();
        ActiveRequests = snapshot.ActiveRequests.ToString();

        var cache = snapshot.Cache;
        var rate = cache.HitRatePercent();
        HitRateText = CacheText.RateText(rate);
        HitRateBrush = RateBrush(rate);
        HitRateProgress = rate ?? 0.0;
        HitRateHelp = $"已复用 {cache.CachedTokens} / 总输入 {cache.InputTokens} token\n{CacheText.RateHelp}";
        CreationText = $"已记录写入 {CacheText.CreationTotalText(cache)} token";
        CreationHelp = CacheText.CreationHelp(cache);
        var latest = cache.RecentRequests.Count > 0 ? cache.RecentRequests[^1] : null;
        LatestText = latest is null ? "等待请求" : CacheText.RequestRateText(latest);
        LatestBrush = RateBrush(latest?.HitRatePercent());
        LatestHelp = latest is null ? "等待本通道的成功请求" : CacheText.RequestHint(latest);
        LatestDetail = CacheText.LatestDetail(latest);
        ZeroHitText = $"{CacheText.CountText(cache.ZeroHitRequests)} 次";
        ZeroHitBrush = cache.ZeroHitRequests > 0 ? RateBrush(0.0) : ThemeBrush("TextFillColorPrimaryBrush");
        ZeroHitHelp = $"{cache.ZeroHitRequests} 次请求的缓存读取量为 0，共 {cache.ZeroHitInputTokens} token 输入。\n{CacheText.UsageHelp}";
        MeasuredText = $"有效 {CacheText.CountText(cache.MeasuredRequests)} 次 · 未计入 {CacheText.CountText(cache.UnmeasuredRequests)} 次";

        var rows = snapshot.Requests.Select(request => new ActiveRequestRow(
            request.RequestId,
            $"第 {request.Attempt} 次",
            request.Phase.Label(),
            request.Phase switch
            {
                RequestPhase.WaitingRetry => ThemeBrush("SystemFillColorCautionBrush"),
                RequestPhase.ReceivingResponse => ThemeBrush("AccentTextFillColorPrimaryBrush"),
                _ => ThemeBrush("TextFillColorSecondaryBrush"),
            },
            $"{request.Method} {request.Path}")).ToList();
        if (Requests.Count != rows.Count || !Requests.Zip(rows).All(pair => pair.First == pair.Second))
        {
            Requests.Clear();
            foreach (var row in rows)
            {
                Requests.Add(row);
            }
        }

        HasRequests = Requests.Count > 0;
        MaxRetriesText = $"{route.MaxRetries} 次";
        TimeoutsText = $"{UiText.TrimFloat(route.TimeoutSeconds)} / {UiText.TrimFloat(route.TotalTimeoutSeconds)} 秒";
        BackoffText = $"{UiText.TrimFloat(route.BaseDelaySeconds)} – {UiText.TrimFloat(route.MaxDelaySeconds)} 秒";
    }

    [RelayCommand]
    private void OnEnableAll()
    {
        Workspace.StartAll();
        _workspaceService.Flush();
    }

    [RelayCommand]
    private void OnDisableAll()
    {
        Workspace.StopAll();
        _workspaceService.Flush();
    }

    [RelayCommand]
    private async Task OnCopyLocalUrl()
    {
        if (LocalUrl.Length == 0)
        {
            return;
        }

        Clipboard.SetText(LocalUrl);
        var version = ++_copyVersion;
        CopyLabel = "已复制";
        CopyLabelBrush = ThemeBrush("SystemFillColorSuccessBrush");
        await Task.Delay(CopiedLabelDuration);
        if (version == _copyVersion)
        {
            CopyLabel = "本地监听";
            CopyLabelBrush = ThemeBrush("TextFillColorSecondaryBrush");
        }
    }
}
