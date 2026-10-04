using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace RetryProxy.ViewModel.Pages;

/// <summary>统计页的三个子标签。</summary>
public enum StatisticsTab
{
    Overview,
    Cache,
    Active,
}

/// <summary>
/// 统计页：顶部是客户端选择和"当前供应商 · Key"上下文，下面用三个子标签分开概况、缓存和在途，
/// 避免原来把运行状态、缓存摘要和明细表堆在同一条长滚动里。
/// </summary>
public partial class StatisticsPageViewModel : ViewModel
{
    private const string DateHelpBase = "按本机日期统计，午夜自动切换。请求按编号去重，重试单独计数；跨日仍未完成的请求计入新一天。\n处理中只显示当前实际请求；历史未完成表示日志没有成功或失败的结束记录，计入总请求，单独列出。\n首次升级按现有日志恢复，已被覆盖的旧日志无法补回，缺失用量保持未获取。";

    private readonly WorkspaceService _workspaceService;

    public OverviewPageViewModel Overview { get; }

    public CachePageViewModel Cache { get; }

    public ActiveRequestsViewModel Active { get; }

    private ProxyWorkspace Workspace => _workspaceService.Workspace;

    [ObservableProperty]
    private bool _hasChannel;

    [ObservableProperty]
    private string _emptyHint = string.Empty;

    [ObservableProperty]
    private string _currentProviderKey = string.Empty;

    [ObservableProperty]
    private string _stateLabel = string.Empty;

    [ObservableProperty]
    private InfoBadgeSeverity _stateSeverity = InfoBadgeSeverity.Informational;

    [ObservableProperty]
    private string _dateCaption = string.Empty;

    [ObservableProperty]
    private string _dateHelp = string.Empty;

    [ObservableProperty]
    private Brush _dateCaptionBrush = Brushes.Gray;

    [ObservableProperty]
    private bool _isOverview = true;

    [ObservableProperty]
    private bool _isCache;

    [ObservableProperty]
    private bool _isActive;

    public StatisticsPageViewModel(
        WorkspaceService workspaceService,
        OverviewPageViewModel overview,
        CachePageViewModel cache,
        ActiveRequestsViewModel active)
    {
        _workspaceService = workspaceService;
        Overview = overview;
        Cache = cache;
        Active = active;
        _workspaceService.Refreshed += Refresh;
        I18nService.Instance.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    public override void OnNavigatedTo()
    {
        Overview.OnNavigatedTo();
        Cache.OnNavigatedTo();
        Active.OnNavigatedTo();
        Refresh();
    }

    public override void OnNavigatedFrom()
    {
        Overview.OnNavigatedFrom();
        Cache.OnNavigatedFrom();
        Active.OnNavigatedFrom();
    }

    private static Brush ThemeBrush(string key)
    {
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    private void Refresh()
    {
        var route = Workspace.SelectedRouteRef();
        HasChannel = route is not null;
        EmptyHint = ClientPageText.Translate("当前客户端尚无通道，请前往供应商页配置");
        CurrentProviderKey = ClientPageText.CurrentProviderKey(Workspace);

        var state = route is null ? ServiceState.Stopped : Workspace.RouteState(route.Id);
        StateLabel = ClientPageText.Translate(UiText.StateLabel(state));
        StateSeverity = state switch
        {
            ServiceState.Running => InfoBadgeSeverity.Success,
            ServiceState.Starting or ServiceState.Stopping => InfoBadgeSeverity.Caution,
            ServiceState.Error => InfoBadgeSeverity.Critical,
            _ => InfoBadgeSeverity.Informational,
        };

        if (route is null)
        {
            DateCaption = string.Empty;
            DateHelp = string.Empty;
            return;
        }

        var snapshot = Workspace.Services.TryGetValue(route.Id, out var service) ? service.Metrics.Snapshot() : new MetricsSnapshot();
        var caption = ClientPageText.Translate("今日 {0} · 重启保留", snapshot.StatisticsDate);
        var help = DateHelpBase;
        if (snapshot.HistoricalUnfinishedRequests > 0)
        {
            caption += ClientPageText.Translate(" · 历史未完成 {0}", snapshot.HistoricalUnfinishedRequests);
        }

        if (snapshot.RestoredFromLegacyLogs)
        {
            help += "\n今日数据包含旧日志恢复记录。";
        }

        if (snapshot.StatisticsWarning is { } warning)
        {
            caption = ClientPageText.Translate("今日 {0} · 统计日志异常", snapshot.StatisticsDate);
            help = $"{warning}\n{help}";
            DateCaptionBrush = ThemeBrush("SystemFillColorCautionBrush");
        }
        else
        {
            DateCaptionBrush = ThemeBrush("TextFillColorSecondaryBrush");
        }

        DateCaption = caption;
        DateHelp = help;
    }

    [RelayCommand]
    private void OnSelectTab(string key)
    {
        var tab = key switch
        {
            "cache" => StatisticsTab.Cache,
            "active" => StatisticsTab.Active,
            _ => StatisticsTab.Overview,
        };

        IsOverview = tab == StatisticsTab.Overview;
        IsCache = tab == StatisticsTab.Cache;
        IsActive = tab == StatisticsTab.Active;
    }
}

/// <summary>客户端页面的动态文案；先翻译模板，再填入名称或数字，密钥内容不进入界面文案。</summary>
internal static class ClientPageText
{
    public static string Translate(string key, params object[] arguments)
    {
        var template = I18nService.Instance.Translate(key);
        return arguments.Length == 0 ? template : string.Format(CultureInfo.CurrentUICulture, template, arguments);
    }

    public static string CurrentProviderKey(ProxyWorkspace workspace)
    {
        var route = workspace.SelectedRouteRef();
        var provider = route is null ? null : workspace.Config.ProviderById(route.CurrentProviderId);
        if (provider is null)
        {
            return Translate("未选择供应商");
        }

        var key = provider.KeyById(route!.CurrentKeyId);
        return Translate("{0} · {1}", provider.Name, key?.Name ?? Translate("未选择 Key"));
    }
}
