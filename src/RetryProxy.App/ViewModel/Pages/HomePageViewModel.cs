using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using System;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RetryProxy.ViewModel.Pages;

/// <summary>
/// 首页：横幅下方是运行状态卡（代理状态、当前供应商·Key、今日统计瓦片），
/// 数据与统计页同源，随 WorkspaceService.Refreshed 刷新；再往下是六个快捷入口。
/// </summary>
public partial class HomePageViewModel : ViewModel
{
    private readonly INavigationService _navigationService;
    private readonly WorkspaceService _workspaceService;

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
    private string _successRateText = "—";

    [ObservableProperty]
    private string _successRateHelp = string.Empty;

    public HomePageViewModel(INavigationService navigationService, WorkspaceService workspaceService)
    {
        _navigationService = navigationService;
        _workspaceService = workspaceService;
        _workspaceService.Refreshed += Refresh;
        Refresh();
    }

    public override void OnNavigatedTo() => Refresh();

    /// <summary>今日最终成功率：成功 ÷（成功 + 失败）；还没有结束的请求时显示破折号。</summary>
    public static string FormatSuccessRate(ulong successful, ulong failed)
    {
        var completed = successful + failed;
        if (completed == 0 || completed < successful)
        {
            // completed < successful 说明 ulong 求和溢出，按无数据处理。
            return "—";
        }

        return ClientPageText.Translate(CacheText.RateText(100.0 * successful / completed));
    }

    [RelayCommand]
    private void OnNavigate(Type pageType)
    {
        // 共用侧栏导航，例如点击首页“统计”时同步选中统计页，并沿用切页保护。
        _navigationService.Navigate(pageType);
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

        var snapshot = route is not null && Workspace.Services.TryGetValue(route.Id, out var service)
            ? service.Metrics.Snapshot()
            : new MetricsSnapshot();
        TotalRequests = snapshot.TotalRequests.ToString();
        SuccessfulRequests = snapshot.SuccessfulRequests.ToString();
        RetryCount = snapshot.RetryCount.ToString();
        FailedRequests = snapshot.FailedRequests.ToString();
        ActiveRequests = snapshot.ActiveRequests.ToString();
        SuccessRateText = FormatSuccessRate(snapshot.SuccessfulRequests, snapshot.FailedRequests);
        SuccessRateHelp = ClientPageText.Translate("最终成功率：成功 ÷（成功 + 失败）；处理中的请求不计入。");
    }
}
