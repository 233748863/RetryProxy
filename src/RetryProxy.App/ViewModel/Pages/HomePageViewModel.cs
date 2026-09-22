using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RetryProxy.Core.Config;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetryProxy.ViewModel.Pages;

public partial class HomePageViewModel : ViewModel
{
    private const string DefaultBannerImagePath = "pack://application:,,,/Resources/Images/banner.jpg";

    private readonly ILogger<HomePageViewModel> _logger;
    private readonly WorkspaceService _workspaceService;
    private readonly Dialogs _dialogs;
    private bool _syncing;

    private ProxyWorkspace Workspace => _workspaceService.Workspace;

    [ObservableProperty]
    private ImageSource? _bannerImageSource;

    [ObservableProperty]
    private bool _isProxyRunning;

    [ObservableProperty]
    private ObservableCollection<PickerItem> _providers = [];

    [ObservableProperty]
    private PickerItem? _selectedProvider;

    [ObservableProperty]
    private ObservableCollection<PickerItem> _channels = [];

    [ObservableProperty]
    private PickerItem? _selectedChannel;

    [ObservableProperty]
    private bool _hasChannel;

    [ObservableProperty]
    private bool _hasProvider;

    [ObservableProperty]
    private string _emptyHint = string.Empty;

    [ObservableProperty]
    private string _providerUsage = string.Empty;

    [ObservableProperty]
    private bool _canEditChannel = true;

    [ObservableProperty]
    private string _channelEditToolTip = string.Empty;

    [ObservableProperty]
    private string _listenPort = string.Empty;

    [ObservableProperty]
    private bool _keepAliveEnabled;

    [ObservableProperty]
    private string _listenAddress = string.Empty;

    public HomePageViewModel(ILogger<HomePageViewModel> logger, WorkspaceService workspaceService, Dialogs dialogs)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _dialogs = dialogs;
        _workspaceService.Refreshed += Refresh;
        Refresh();
    }

    /// <summary>从工作区同步下拉框与开关；只有文案变化时才重建列表，避免下拉框闪动。</summary>
    private void Refresh()
    {
        _syncing = true;
        try
        {
            var providers = Workspace.Config.Providers
                .Select(provider => new PickerItem(provider.Name, $"{provider.Name} · {provider.BaseUrl} · {Workspace.ProviderUsage(provider.Name)} 通道"))
                .ToList();
            ReplaceIfChanged(Providers, providers);
            SelectedProvider = Providers.FirstOrDefault(item => item.Key == Workspace.SelectedProvider);

            var channels = Workspace.VisibleRoutes()
                .Select(route => new PickerItem(route.Id, $"{route.Name} · {route.ListenPort} · {UiText.StateLabel(Workspace.RouteState(route.Id))}"))
                .ToList();
            ReplaceIfChanged(Channels, channels);
            SelectedChannel = Channels.FirstOrDefault(item => item.Key == Workspace.SelectedRoute);

            HasProvider = Workspace.SelectedProvider.Length > 0;
            ProviderUsage = $"{Workspace.ProviderUsage(Workspace.SelectedProvider)} 个通道";
            var route = Workspace.SelectedRouteRef();
            HasChannel = route is not null;
            EmptyHint = HasProvider ? "该服务商暂无通道，点击「＋ 新增通道」创建" : "请先新增服务商，再为它创建通道";
            var state = route is null ? ServiceState.Stopped : Workspace.RouteState(route.Id);
            IsProxyRunning = state is ServiceState.Running or ServiceState.Starting;
            CanEditChannel = route is not null && state is ServiceState.Stopped or ServiceState.Error;
            ChannelEditToolTip = route is null
                ? string.Empty
                : CanEditChannel
                    ? $"最大重试 {route.MaxRetries} 次\n单次 / 总等待 {UiText.TrimFloat(route.TimeoutSeconds)} / {UiText.TrimFloat(route.TotalTimeoutSeconds)} 秒\n退避间隔 {UiText.TrimFloat(route.BaseDelaySeconds)} – {UiText.TrimFloat(route.MaxDelaySeconds)} 秒"
                    : "请先停用通道";
            ListenPort = route?.ListenPort.ToString() ?? string.Empty;
            KeepAliveEnabled = route?.KeepaliveEnabled ?? false;
            ListenAddress = route is null ? string.Empty : LocalUrlOf(route);
        }
        finally
        {
            _syncing = false;
        }
    }

    private string LocalUrlOf(ProxyRoute route)
    {
        try
        {
            return Workspace.Config.RuntimeConfigFor(route.Id).LocalUrl;
        }
        catch (ConfigException)
        {
            return route.LocalUrl;
        }
    }

    private static void ReplaceIfChanged(ObservableCollection<PickerItem> target, List<PickerItem> items)
    {
        if (target.Count == items.Count && target.Zip(items).All(pair => pair.First == pair.Second))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    partial void OnSelectedProviderChanged(PickerItem? value)
    {
        if (_syncing || value is null || value.Key == Workspace.SelectedProvider)
        {
            return;
        }

        Workspace.SelectProvider(value.Key);
        Refresh();
    }

    partial void OnSelectedChannelChanged(PickerItem? value)
    {
        if (_syncing || value is null || value.Key == Workspace.SelectedRoute)
        {
            return;
        }

        Workspace.SelectRoute(value.Key);
        Refresh();
    }

    partial void OnKeepAliveEnabledChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        var route = Workspace.SelectedRouteRef();
        if (route is not null && route.KeepaliveEnabled != value)
        {
            Workspace.ApplyKeepAliveInput(value, Workspace.KeepAliveMinutes, route.KeepaliveContextLimit);
            Refresh();
        }
    }

    /// <summary>首页端口框失焦后落地：通道运行中不允许改，其余走通道编辑器的校验。</summary>
    partial void OnListenPortChanged(string value)
    {
        if (_syncing)
        {
            return;
        }

        var route = Workspace.SelectedRouteRef();
        if (route is null || value.Trim() == route.ListenPort.ToString())
        {
            return;
        }

        if (Workspace.RouteState(route.Id) is not (ServiceState.Stopped or ServiceState.Error))
        {
            Workspace.Notice = "请先停用通道";
            Refresh();
            return;
        }

        var index = Workspace.Config.Routes.FindIndex(candidate => candidate.Id == route.Id);
        var editor = Workspace.OpenRouteEditor(index);
        if (editor is null)
        {
            Refresh();
            return;
        }

        editor.Port = value;
        if (Workspace.CommitRoute(editor) is { } error)
        {
            Workspace.Notice = error;
        }

        Refresh();
    }

    [RelayCommand]
    private void OnLoaded()
    {
        if (BannerImageSource is not null)
        {
            return;
        }

        try
        {
            BannerImageSource = new BitmapImage(new Uri(DefaultBannerImagePath, UriKind.Absolute));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "加载首页横幅图失败");
        }
    }

    [RelayCommand]
    private void OnStartProxy()
    {
        var route = Workspace.SelectedRouteRef();
        if (route is null)
        {
            Workspace.Notice = EmptyHint;
            return;
        }

        Workspace.StartRoute(route.Id);
        _workspaceService.Flush();
    }

    [RelayCommand]
    private void OnStopProxy()
    {
        var route = Workspace.SelectedRouteRef();
        if (route is null)
        {
            return;
        }

        Workspace.StopRoute(route.Id);
        _workspaceService.Flush();
    }

    [RelayCommand]
    private void OnPrepareKeepAlive()
    {
        Workspace.PrepareSelectedRoute();
        _workspaceService.Flush();
    }

    [RelayCommand]
    private void OnCopyListenAddress()
    {
        if (ListenAddress.Length > 0)
        {
            Clipboard.SetText(ListenAddress);
        }
    }

    [RelayCommand]
    private async Task OnAddProvider()
    {
        await _dialogs.ShowProviderEditorAsync(Workspace.OpenProviderEditor(null));
    }

    [RelayCommand]
    private async Task OnEditProvider()
    {
        if (Workspace.SelectedProviderIndex() is not { } index)
        {
            Workspace.Notice = "先选中一个服务商";
            return;
        }

        await _dialogs.ShowProviderEditorAsync(Workspace.OpenProviderEditor(index));
    }

    [RelayCommand]
    private async Task OnDeleteProvider()
    {
        if (Workspace.SelectedProviderIndex() is not { } index)
        {
            Workspace.Notice = "先选中一个服务商";
            return;
        }

        var name = Workspace.Config.Providers[index].Name;
        if (await _dialogs.ConfirmDeleteAsync("删除服务商", $"确认删除服务商“{name}”？仍被通道引用时无法删除。"))
        {
            Workspace.DeleteProvider(index);
            _workspaceService.Flush();
        }
    }

    [RelayCommand]
    private async Task OnAddChannel()
    {
        var editor = Workspace.OpenRouteEditor(null);
        if (editor is null)
        {
            return;
        }

        await _dialogs.ShowRouteEditorAsync(editor);
    }

    [RelayCommand]
    private async Task OnEditChannel()
    {
        var route = Workspace.SelectedRouteRef();
        if (route is null)
        {
            return;
        }

        if (Workspace.RouteState(route.Id) is not (ServiceState.Stopped or ServiceState.Error))
        {
            Workspace.Notice = "请先停用通道";
            return;
        }

        var index = Workspace.Config.Routes.FindIndex(candidate => candidate.Id == route.Id);
        var editor = Workspace.OpenRouteEditor(index);
        if (editor is null)
        {
            return;
        }

        await _dialogs.ShowRouteEditorAsync(editor);
    }

    [RelayCommand]
    private async Task OnDeleteChannel()
    {
        var route = Workspace.SelectedRouteRef();
        if (route is null)
        {
            return;
        }

        if (await _dialogs.ConfirmDeleteAsync("删除通道", $"确认删除通道“{route.Name}”？该通道的监听端口与重试设置会一并移除。"))
        {
            Workspace.DeleteRoute(route.Id);
            _workspaceService.Flush();
        }
    }

    [RelayCommand]
    private void OnGoToWikiUrl()
    {
        var docs = Global.Absolute("docs");
        if (Directory.Exists(docs))
        {
            Process.Start(new ProcessStartInfo(docs) { UseShellExecute = true });
        }
    }
}
