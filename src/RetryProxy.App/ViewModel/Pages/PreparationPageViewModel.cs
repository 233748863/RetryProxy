using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using RetryProxy.View.Drawers;
using RetryProxy.View.Pages;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Wpf.Ui;

namespace RetryProxy.ViewModel.Pages;

public partial class PreparationPageViewModel : ViewModel
{
    private readonly WorkspaceService _workspaceService;
    private readonly DrawerService _drawers;
    private readonly LogPageViewModel _logs;
    private readonly INavigationService _navigationService;
    private readonly PreparationGroupViewModel _followingGroup = new();
    private readonly PreparationGroupViewModel _manualGroup = new();
    private readonly Dictionary<(ClientType Client, string ProviderId), PreparationGroupViewModel> _providerGroups = new();
    private PreparationWorkspace Preparations => _workspaceService.Preparations;
    private ProxyWorkspace Workspace => _workspaceService.Workspace;

    public ObservableCollection<PreparationTaskViewModel> Tasks { get; } = new();
    public ObservableCollection<PreparationGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    private bool _hasTasks;

    [ObservableProperty]
    private string _summary = string.Empty;

    public PreparationPageViewModel(WorkspaceService workspaceService, DrawerService drawers,
        LogPageViewModel logs, INavigationService navigationService)
    {
        _workspaceService = workspaceService;
        _drawers = drawers;
        _logs = logs;
        _navigationService = navigationService;
        _workspaceService.Refreshed += Refresh;
        _workspaceService.Tick += RefreshTaskStates;
        I18nService.Instance.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    public override void OnNavigatedTo()
    {
        _workspaceService.SetHintTimerWanted(this, true);
        Refresh();
    }

    public override void OnNavigatedFrom() => _workspaceService.SetHintTimerWanted(this, false);

    private void Refresh()
    {
        var client = Workspace.SelectedClient;
        var tasks = Preparations.Tasks.Where(task => task.ClientType == client).OrderBy(task => task.Number).ToList();
        var previousRows = Tasks.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var providers = Workspace.Config.ProvidersFor(client).ToDictionary(provider => provider.Id, StringComparer.Ordinal);
        var rows = new List<PreparationTaskViewModel>();
        var following = new List<PreparationTaskViewModel>();
        var manual = new List<PreparationTaskViewModel>();
        var providerRows = new Dictionary<string, List<PreparationTaskViewModel>>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            var item = previousRows.GetValueOrDefault(task.Id) ?? new PreparationTaskViewModel(task.Id);
            item.Refresh(task, providers.GetValueOrDefault(task.ProviderId)?.KeyById(task.KeyId)?.Name);
            rows.Add(item);
            // 跟随当前按来源归组，不按本次绑定的 Key 归组；切换当前 Key 只更新组标题。
            if (task.Mode == PrepareMode.LocalProvider) following.Add(item);
            else if (task.Mode == PrepareMode.CustomProvider) manual.Add(item);
            else
            {
                if (!providerRows.TryGetValue(task.ProviderId, out var groupRows))
                    providerRows[task.ProviderId] = groupRows = new();
                groupRows.Add(item);
            }
        }

        Synchronize(Tasks, rows);
        _followingGroup.Title = ClientPageText.Translate("跟随当前（现在：{0}）", ClientPageText.CurrentProviderKey(Workspace));
        _manualGroup.Title = ClientPageText.Translate("手动填写");
        Synchronize(_followingGroup.Tasks, following);
        Synchronize(_manualGroup.Tasks, manual);
        var groups = new List<PreparationGroupViewModel>();
        if (following.Count > 0) groups.Add(_followingGroup);
        // 分组对象由供应商 ID 稳定标识：同名供应商不合并，改名也不重建任务控件。
        var providerIds = providers.Keys.Where(providerRows.ContainsKey)
            .Concat(providerRows.Keys.Where(id => !providers.ContainsKey(id)));
        foreach (var providerId in providerIds)
        {
            var identity = (client, providerId);
            if (!_providerGroups.TryGetValue(identity, out var group))
                _providerGroups[identity] = group = new();
            var savedName = tasks.First(task => task.Mode == PrepareMode.ListProvider && task.ProviderId == providerId).ProviderName;
            group.Title = providers.GetValueOrDefault(providerId)?.Name
                ?? (savedName.Length > 0 ? savedName : ClientPageText.Translate("已删除的供应商"));
            Synchronize(group.Tasks, providerRows[providerId]);
            groups.Add(group);
        }
        if (manual.Count > 0) groups.Add(_manualGroup);
        Synchronize(Groups, groups);
        var activeGroups = Preparations.Tasks.Where(task => task.Mode == PrepareMode.ListProvider)
            .Select(task => (task.ClientType, task.ProviderId)).ToHashSet();
        foreach (var identity in _providerGroups.Keys.Where(identity => !activeGroups.Contains(identity)).ToArray())
            _providerGroups.Remove(identity);
        HasTasks = tasks.Count > 0;
        Summary = HasTasks
            ? ClientPageText.Translate("共 {0} 项准备 · {1} 项运行中", tasks.Count, tasks.Count(task => task.CanStop))
            : string.Empty;
    }

    private void RefreshTaskStates()
    {
        // 每秒只改现有行的状态属性，完全不改分组/行集合，保留焦点、悬停与菜单。
        foreach (var item in Tasks)
        {
            if (Preparations.Find(item.Id) is not { } task) continue;
            item.Refresh(task, Workspace.Config.ProviderById(task.ProviderId)?.KeyById(task.KeyId)?.Name);
        }
    }

    private static void Synchronize<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        // 新增第二项时也保留第一项的控件，避免焦点和自动化节点指向已被移除的旧容器。
        CollectionSync.Update(target, items);
    }

    [RelayCommand]
    private async Task OnAddPreparation()
    {
        var options = Preparations.OpenPrepareDialog();
        options.ClientType = Workspace.SelectedClient;
        await _drawers.EditPreparationAsync(options);
    }

    [RelayCommand]
    private async Task OnEditPreparation(string taskId)
    {
        if (Preparations.Find(taskId)?.CanStart == true)
        {
            await _drawers.EditPreparationAsync(Preparations.OpenPrepareDialog(taskId));
        }
    }

    [RelayCommand]
    private void OnStartPreparation(string taskId)
    {
        Preparations.Start(taskId);
        _workspaceService.Flush();
    }

    [RelayCommand]
    private void OnStopPreparation(string taskId)
    {
        Preparations.Stop(taskId);
        _workspaceService.Flush();
    }

    [RelayCommand]
    private void OnRemovePreparation(string taskId)
    {
        Preparations.Remove(taskId);
        _workspaceService.Flush();
    }

    [RelayCommand]
    private void OnViewPreparationLog(string taskId)
    {
        if (Preparations.Find(taskId) is not { } task) return;
        Workspace.SelectClient(task.ClientType);
        _workspaceService.Flush();
        _logs.OpenPreparation(task.Number);
        _navigationService.Navigate(typeof(LogPage));
    }
}

public partial class PreparationGroupViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    public ObservableCollection<PreparationTaskViewModel> Tasks { get; } = new();
}

public partial class PreparationTaskViewModel : ObservableObject
{
    public PreparationTaskViewModel(string id) => Id = id;

    public string Id { get; }
    public string StartAutomationId => "StartPreparation_" + Id;
    public string StopAutomationId => "StopPreparation_" + Id;
    public string MenuAutomationId => "PreparationTaskMenu_" + Id;
    public string StatusAutomationId => "PreparationTaskStatus_" + Id;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _providerUrl = string.Empty;
    [ObservableProperty] private string _settings = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _hint = string.Empty;
    [ObservableProperty] private string _statistics = string.Empty;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private bool _isPreparing;
    [ObservableProperty] private bool _isManual;
    [ObservableProperty] private string _stopText = string.Empty;

    public void Refresh(PreparationTask task, string? currentKeyName)
    {
        var host = Uri.TryCreate(task.ProviderUrl, UriKind.Absolute, out var address) ? address.Authority : task.ProviderUrl;
        IsManual = task.Mode == PrepareMode.CustomProvider;
        var keyName = currentKeyName ?? task.KeyName;
        Title = IsManual
            ? ClientPageText.Translate("准备 {0} · {1}", task.Number, host)
            : keyName.Length > 0 ? keyName : ClientPageText.Translate("准备 {0}", task.Number);
        ProviderUrl = task.ProviderUrl;
        var effort = task.ReasoningEffort == ReasoningEffort.Default
            ? ClientPageText.Translate("默认强度") : ClientPageText.Translate(task.ReasoningEffort.Label());
        var model = string.IsNullOrWhiteSpace(task.Model) ? ClientPageText.Translate("跟随 Key 模型") : task.Model;
        Settings = ClientPageText.Translate("{0} · {1} · {2} 分钟", model, effort, task.IdleMinutes);
        IsPreparing = task.IsPreparing;
        Error = task.LastError is { } error ? DrawerText.Error(error) : string.Empty;
        HasError = Error.Length > 0;
        Status = PreparationStatusText.Status(task);
        Statistics = PreparationStatusText.Statistics(task);
        Hint = string.Join(Environment.NewLine, new[] { Status, Statistics, Error }.Where(text => text.Length > 0));
        CanStart = task.CanStart;
        CanStop = task.CanStop;
        StopText = ClientPageText.Translate(IsPreparing ? "终止准备" : "停止保活");
    }
}
