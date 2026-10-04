using CommunityToolkit.Mvvm.ComponentModel;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace RetryProxy.ViewModel.Pages;

/// <summary>在途请求一行。</summary>
public sealed record ActiveRequestRow(string RequestId, string Attempt, string Phase, Brush PhaseBrush, string Target);

/// <summary>统计页"在途"标签：当前真正处理中的请求，不掺历史记录。</summary>
public partial class ActiveRequestsViewModel : ViewModel
{
    private readonly WorkspaceService _workspaceService;

    private ProxyWorkspace Workspace => _workspaceService.Workspace;

    [ObservableProperty]
    private bool _hasChannel;

    [ObservableProperty]
    private ObservableCollection<ActiveRequestRow> _requests = [];

    [ObservableProperty]
    private bool _hasRequests;

    [ObservableProperty]
    private string _emptyHint = string.Empty;

    public ActiveRequestsViewModel(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
        _workspaceService.Refreshed += Refresh;
        Refresh();
    }

    public override void OnNavigatedTo() => Refresh();

    private static Brush ThemeBrush(string key)
    {
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    private void Refresh()
    {
        var route = Workspace.SelectedRouteRef();
        HasChannel = route is not null;
        EmptyHint = ClientPageText.Translate("当前客户端尚无通道，请前往供应商页配置");
        var snapshot = route is not null && Workspace.Services.TryGetValue(route.Id, out var service)
            ? service.Metrics.Snapshot()
            : new MetricsSnapshot();

        var rows = snapshot.Requests.Select(request => new ActiveRequestRow(
            request.RequestId,
            ClientPageText.Translate("第 {0} 次", request.Attempt),
            ClientPageText.Translate(request.Phase.Label()),
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
    }
}
