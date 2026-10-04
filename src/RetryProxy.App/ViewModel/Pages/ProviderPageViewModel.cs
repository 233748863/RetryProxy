using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Client;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace RetryProxy.ViewModel.Pages;

public partial class ProviderPageViewModel : ViewModel
{
    private readonly WorkspaceService _service;
    private readonly DrawerService _drawers;
    private readonly Dialogs _dialogs;
    private readonly KeySwitchService _switches;
    private readonly ClientTakeoverService _clients;
    [ObservableProperty] private string _clientState = string.Empty;
    [ObservableProperty] private string _clientActionText = string.Empty;
    [ObservableProperty] private string _clientError = string.Empty;
    [ObservableProperty] private bool _showTakeover;
    [ObservableProperty] private bool _canTakeOver;
    private ProxyConfig? _displayedConfig;
    private ClientType? _displayedClient;
    private long _languageRevision = -1;
    private bool _syncing;
    internal ProxyWorkspace Workspace => _service.Workspace;
    internal PreparationWorkspace Preparations => _service.Preparations;
    public ObservableCollection<ProviderCardViewModel> Providers { get; } = [];
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private bool _showSearch;
    [ObservableProperty] private bool _hasProviders;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private string _stateReason = string.Empty;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _currentKey = string.Empty;
    [ObservableProperty] private string _today = string.Empty;
    [ObservableProperty] private bool _keepAliveEnabled;
    [ObservableProperty] private string _keepAliveHint = string.Empty;
    [ObservableProperty] private string? _keepAliveHintToolTip;
    [ObservableProperty] private bool _hasCurrentProvider;
    public event Action<string>? LocateRequested;
    internal static string T(string text) => I18nService.Instance.Translate(text);

    public ProviderPageViewModel(WorkspaceService service, DrawerService drawers, Dialogs dialogs, KeySwitchService switches, ClientTakeoverService clients)
    {
        _service = service;
        _drawers = drawers;
        _dialogs = dialogs;
        _switches = switches;
        _clients = clients;
        service.Refreshed += Refresh;
        service.Tick += RefreshPreparationStates;
        Refresh();
    }

    public override void OnNavigatedTo()
    {
        _service.SetHintTimerWanted(this, true);
        Refresh();
    }

    public override void OnNavigatedFrom()
    {
        _service.SetHintTimerWanted(this, false);
    }

    private void RefreshPreparationStates()
    {
        foreach (var provider in Providers) provider.RefreshPreparations();
    }

    private void Refresh()
    {
        var route = Workspace.SelectedRouteRef();
        var provider = route is null ? null : Workspace.Config.ProviderById(route.CurrentProviderId);
        var state = route is null ? ServiceState.Stopped : Workspace.RouteState(route.Id);
        StateText = T(UiText.StateLabel(state));
        StateReason = provider is null ? T("请先添加供应商和 Key")
            : state == ServiceState.Error ? Workspace.Services.GetValueOrDefault(route!.Id)?.StartupError ?? T("代理启动失败")
            : state == ServiceState.Stopped ? T("代理已手动停止或尚未启动") : string.Empty;
        CanStart = provider is not null && state is ServiceState.Stopped or ServiceState.Error;
        var localUrl = route?.LocalUrl ?? string.Empty;
        if (route is not null && provider is not null)
        {
            try { localUrl = Workspace.Config.RuntimeConfigFor(route.Id).LocalUrl; }
            catch (ConfigException) { }
        }
        Address = route?.ClientType == ClientType.Codex ? localUrl + "/v1" : localUrl;
        var key = route is null ? null : Workspace.Config.CurrentKeyOf(route);
        CurrentKey = provider is null ? T("未选择供应商") : key is null ? $"{provider.Name} · {T("尚无 Key")}" : $"{provider.Name} · {key.Name}";
        HasCurrentProvider = provider is not null;
        var connection = _service.Clients.Connection(Workspace.SelectedClient);
        ClientState = T(ClientTakeoverService.StatusText(connection.Status));
        ClientError = connection.Error.Length > 0 ? View.Drawers.DrawerText.Error(connection.Error)
            : T("环境变量、项目配置或其他客户端目录可能覆盖本机地址；已接管仅确认当前配置文件，不代表所有窗口已连接代理。");
        ShowTakeover = connection.Status != ClientConnectionStatus.TakenOver || !Workspace.Config.ClientTakeover[Workspace.SelectedClient].Enabled;
        CanTakeOver = !ClientConfigPaths.WritesBlocked && key is not null;
        ClientActionText = T(connection.Status is ClientConnectionStatus.Modified or ClientConnectionStatus.Unavailable ? "重新接管" : "一键接管");
        var metrics = route is not null && Workspace.Services.TryGetValue(route.Id, out var service) ? service.Metrics.Snapshot() : new MetricsSnapshot();
        Today = string.Format(T("通道 {0}/{1} 在运行 · 今日 {2} · 成功 {3} · 重试 {4} · 失败 {5}"),
            Workspace.RunningCount(), Workspace.Config.Routes.Count,
            metrics.TotalRequests, metrics.SuccessfulRequests, metrics.RetryCount, metrics.FailedRequests);
        KeepAliveHint = route is null ? string.Empty : Workspace.KeepAliveHint(route);
        var watchdog = route is not null && Workspace.RouteKeepAlives.TryGetValue(route.Id, out var alive) ? alive.Snapshot() : null;
        KeepAliveHintToolTip = watchdog?.PreparationLastError is { } reason
            ? string.Format(T("最近一次后台问答未完成：{0}"), reason)
            : null;
        _syncing = true;
        KeepAliveEnabled = route?.KeepaliveEnabled ?? false;
        _syncing = false;
        if (!ReferenceEquals(_displayedConfig, Workspace.Config) || _displayedClient != Workspace.SelectedClient
            || _languageRevision != I18nService.Instance.Revision)
        {
            _displayedConfig = Workspace.Config;
            _displayedClient = Workspace.SelectedClient;
            _languageRevision = I18nService.Instance.Revision;
            RefreshCards();
        }
        RefreshPreparationStates();
    }

    private void RefreshCards()
    {
        var all = Workspace.Config.ProvidersFor(Workspace.SelectedClient).ToList();
        HasProviders = all.Count > 0;
        ShowSearch = all.Count > 8;
        var query = ShowSearch ? Query.Trim() : string.Empty;
        var visible = all.Where(provider => query.Length == 0 || new[] { provider.Name, provider.BaseUrl, provider.Notes }
            .Concat(provider.Keys.SelectMany(key => new[] { key.Name, key.Notes })).Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        // 仅在配置/筛选变化时更新卡片，后台请求统计刷新不重建 50×5 个 Key 行。
        var existing = Providers.ToDictionary(item => item.Id);
        var rows = visible.Select(provider =>
        {
            var row = existing.GetValueOrDefault(provider.Id) ?? new ProviderCardViewModel(this, provider.Id);
            row.Refresh(provider);
            return row;
        }).ToList();
        CollectionSync.Update(Providers, rows);
        HasResults = Providers.Count > 0;
    }

    partial void OnQueryChanged(string value) => RefreshCards();
    partial void OnKeepAliveEnabledChanged(bool value)
    {
        if (_syncing || Workspace.SelectedRouteRef() is not { } route) return;
        Workspace.SetKeepAlive(route.Id, value, route.KeepaliveIdleMinutes, route.KeepaliveContextLimit, route.KeepaliveReasoningEffort);
        _service.Flush();
    }

    [RelayCommand] private Task TakeOverClient() => _clients.TakeOverAsync(Workspace.SelectedClient);
    [RelayCommand] private Task AddProvider() => _drawers.EditProviderAsync(null, Workspace.SelectedClient);
    [RelayCommand] private Task ProxySettings() => Workspace.SelectedRouteRef() is { } route ? _drawers.EditProxyAsync(route.Id) : Task.CompletedTask;
    [RelayCommand] private void StartProxy()
    {
        if (Workspace.SelectedRouteRef() is { } route) Workspace.StartRoute(route.Id);
        _service.Flush();
    }
    [RelayCommand] private void CopyAddress() => Copy(Address);
    [RelayCommand] private void Locate()
    {
        Query = string.Empty;
        if (Workspace.SelectedRouteRef() is { } route) LocateRequested?.Invoke(route.CurrentProviderId);
    }

    internal Task EditProvider(string id) => _drawers.EditProviderAsync(Workspace.Config.ProviderById(id), Workspace.SelectedClient);
    internal Task EditKey(string providerId, string? keyId) => _drawers.EditKeyAsync(providerId, keyId);
    internal void Switch(string providerId, string keyId)
    {
        if (Workspace.SelectedRouteRef() is { } route) _switches.Switch(route.Id, providerId, keyId);
    }
    internal void Duplicate(string id) => Apply(Workspace.DuplicateProvider(id));
    internal async Task DeleteProvider(string id)
    {
        var provider = Workspace.Config.ProviderById(id);
        if (provider is null) return;
        if (Workspace.Config.Routes.Any(route => route.CurrentProviderId == id)) { Apply(T("请先切换到其他供应商的 Key")); return; }
        if (await _dialogs.ConfirmDeleteAsync("删除供应商", string.Format(T("确认删除供应商“{0}”及其全部 Key？关联准备任务将停止并删除。"), provider.Name)))
        {
            var keys = Workspace.Config.ProviderById(id)?.Keys.Select(key => new PreparationKeyRef(id, key.Id)).ToList() ?? [];
            Apply(await _service.PreparationManagement.ChangeAsync(keys, () => Workspace.RemoveProvider(id)));
        }
    }
    internal async Task DeleteKey(string providerId, string keyId)
    {
        var provider = Workspace.Config.ProviderById(providerId);
        var key = provider?.KeyById(keyId);
        if (key is null) return;
        if (Workspace.Config.Routes.Any(route => route.CurrentProviderId == providerId && route.CurrentKeyId == keyId)) { Apply(T("请先切换到其他 Key")); return; }
        if (provider!.Keys.Count == 1) { Apply(T("每个供应商至少保留一个 Key，请改为删除供应商")); return; }
        if (await _dialogs.ConfirmDeleteAsync("删除 Key", string.Format(T("确认删除 Key“{0}”？关联准备任务将停止并删除。"), key.Name)))
            Apply(await _service.PreparationManagement.ChangeAsync([new PreparationKeyRef(providerId, keyId)],
                () => Workspace.DeleteKey(providerId, keyId)));
    }

    internal void PrepareAll(string providerId)
    {
        if (Workspace.Config.ProviderById(providerId) is not { } provider) return;
        _service.PrepareKeys(provider.ClientType, provider.Keys.Select(key => new PreparationKeyRef(providerId, key.Id)));
    }

    internal void TogglePreparation(string providerId, string keyId)
    {
        if (Workspace.Config.ProviderById(providerId) is not { } provider) return;
        var key = new PreparationKeyRef(providerId, keyId);
        if (Preparations.FindForKey(key) is { CanStop: true } task)
        {
            Preparations.Stop(task.Id);
            _service.Flush();
        }
        else _service.PrepareKeys(provider.ClientType, [key]);
    }

    internal void CopyKey(string providerId, string keyId) => Copy(Workspace.Config.ProviderById(providerId)?.KeyById(keyId)?.ApiKey ?? string.Empty);
    private void Copy(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (Exception) { Workspace.Notice = T("复制失败，请稍后重试"); }
    }
    internal void OpenWebsite(string id)
    {
        var url = Workspace.Config.ProviderById(id)?.WebsiteUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { Workspace.Notice = T("无法打开官网"); }
    }
    public void MoveProvider(string id, string target) => Apply(Workspace.MoveProvider(id, target));
    public void MoveKey(string providerId, string id, string target) => Apply(Workspace.MoveKey(providerId, id, target));
    private void Apply(string? error)
    {
        if (error is not null) Workspace.Notice = error;
        _service.Flush();
    }
}

public partial class ProviderCardViewModel : ObservableObject
{
    private readonly ProviderPageViewModel _owner;
    private ProviderEndpoint? _provider;
    public string Id { get; }
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _hasWebsite;
    [ObservableProperty] private bool _expanded;
    [ObservableProperty] private bool _canFold;
    [ObservableProperty] private string _foldSummary = string.Empty;
    [ObservableProperty] private string _foldActionText = string.Empty;
    private HashSet<string> _visibleIds = [];
    public string MenuId => "ProviderMenu_" + Id;
    public string FoldId => "Fold_" + Id;
    public string FoldSummaryId => "FoldSummary_" + Id;
    public ObservableCollection<ProviderKeyRowViewModel> Keys { get; } = [];
    public ProviderCardViewModel(ProviderPageViewModel owner, string id) { _owner = owner; Id = id; }
    public void Refresh(ProviderEndpoint provider)
    {
        _provider = provider;
        Name = provider.Name;
        Url = provider.BaseUrl;
        Host = Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Url;
        Model = provider.Models.Model;
        HasWebsite = Uri.TryCreate(provider.WebsiteUrl, UriKind.Absolute, out var site) && site.Scheme is "https" or "http";
        IsCurrent = _owner.Workspace.SelectedRouteRef()?.CurrentProviderId == Id;
        RefreshKeys();
    }
    partial void OnExpandedChanged(bool value) => RefreshKeys();
    private void RefreshKeys()
    {
        if (_provider is null) return;
        var current = _owner.Workspace.SelectedRouteRef();
        var selectedKey = IsCurrent ? current?.CurrentKeyId : null;
        // 收起时只留当前 Key；本卡不是当前供应商时留第一个 Key 作代表。
        var lead = _provider.Keys.Any(key => key.Id == selectedKey) ? selectedKey : _provider.Keys.Count > 0 ? _provider.Keys[0].Id : null;
        var visible = Expanded ? _provider.Keys.ToList() : _provider.Keys.Where(key => key.Id == lead).ToList();
        _visibleIds = visible.Select(key => key.Id).ToHashSet();
        UpdateFold();
        var existing = Keys.ToDictionary(row => row.Id);
        var rows = visible.Select(key =>
        {
            var row = existing.GetValueOrDefault(key.Id) ?? new ProviderKeyRowViewModel(_owner, Id, key, key.Id == selectedKey);
            row.Refresh(key, key.Id == selectedKey);
            return row;
        }).ToList();
        CollectionSync.Update(Keys, rows);
    }
    private void UpdateFold()
    {
        if (_provider is null) return;
        var hidden = _provider.Keys.Where(key => !_visibleIds.Contains(key.Id)).ToList();
        var prepared = hidden.Count(key => _owner.Preparations.FindForKey(new PreparationKeyRef(Id, key.Id)) is { IsReady: true });
        FoldSummary = Expanded ? string.Empty : string.Format(ProviderPageViewModel.T("还有 {0} 个 Key（{1} 个已准备）"), hidden.Count, prepared);
        FoldActionText = ProviderPageViewModel.T(Expanded ? "收起" : "展开");
        CanFold = Expanded ? _provider.Keys.Count > 1 : hidden.Count > 0;
    }
    public void RefreshPreparations()
    {
        foreach (var key in Keys) key.RefreshPreparation();
        UpdateFold();
    }
    public string PrepareAllId => "PrepareAll_" + Id;
    [RelayCommand] private void PrepareAll() => _owner.PrepareAll(Id);
    [RelayCommand] private void Fold() => Expanded = !Expanded;
    [RelayCommand] private Task Edit() => _owner.EditProvider(Id);
    [RelayCommand] private Task AddKey() => _owner.EditKey(Id, null);
    [RelayCommand] private void Duplicate() => _owner.Duplicate(Id);
    [RelayCommand] private Task Delete() => _owner.DeleteProvider(Id);
    [RelayCommand] private void Website() => _owner.OpenWebsite(Id);
}

public partial class ProviderKeyRowViewModel : ObservableObject
{
    private readonly ProviderPageViewModel _owner;
    public string ProviderId { get; }
    public string Id { get; }
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _maskedKey = string.Empty;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private string _preparationState = string.Empty;
    [ObservableProperty] private string _preparationHint = string.Empty;
    [ObservableProperty] private string _preparationAction = string.Empty;
    [ObservableProperty] private bool _preparationFailed;
    private bool _canPrepare = true;
    public string Marker => IsCurrent ? "●" : "○";
    public string SwitchText => ProviderPageViewModel.T(IsCurrent ? "使用中" : "切换");
    public string PrepareId => "PrepareKey_" + Id;
    public string PreparationStatusId => "PreparationStatus_" + Id;
    public IRelayCommand SwitchCommand { get; }
    public IRelayCommand PrepareCommand { get; }
    public IAsyncRelayCommand EditCommand { get; }
    public IRelayCommand CopyCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public ProviderKeyRowViewModel(ProviderPageViewModel owner, string providerId, ProviderKey key, bool current)
    {
        _owner = owner;
        ProviderId = providerId;
        Id = key.Id;
        SwitchCommand = new RelayCommand(() => owner.Switch(providerId, Id), () => !IsCurrent);
        PrepareCommand = new RelayCommand(() => owner.TogglePreparation(providerId, Id), () => _canPrepare);
        EditCommand = new AsyncRelayCommand(() => owner.EditKey(providerId, Id));
        CopyCommand = new RelayCommand(() => owner.CopyKey(providerId, Id));
        DeleteCommand = new AsyncRelayCommand(() => owner.DeleteKey(providerId, Id));
        Refresh(key, current);
    }

    public void Refresh(ProviderKey key, bool current)
    {
        Name = key.Name;
        MaskedKey = key.MaskedKey;
        IsCurrent = current;
        OnPropertyChanged(nameof(Marker));
        OnPropertyChanged(nameof(SwitchText));
        SwitchCommand.NotifyCanExecuteChanged();
        RefreshPreparation();
    }

    public void RefreshPreparation()
    {
        var task = _owner.Preparations.FindForKey(new PreparationKeyRef(ProviderId, Id));
        PreparationState = PreparationStatusText.Status(task, forKey: true);
        PreparationHint = task is null ? string.Empty : task.LastError ?? PreparationStatusText.Statistics(task);
        PreparationFailed = task is { CanStart: true, LastError: not null };
        PreparationAction = ProviderPageViewModel.T(task?.CanStop == true ? "停止" : "准备");
        var canPrepare = task is null || task.CanStart || task.CanStop;
        if (_canPrepare == canPrepare) return;
        _canPrepare = canPrepare;
        PrepareCommand.NotifyCanExecuteChanged();
    }
}
