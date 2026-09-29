using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
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
    private readonly Dialogs _dialogs;
    private readonly LogPageViewModel _logs;
    private readonly INavigationService _navigationService;
    private readonly PreparationGroupViewModel _followingGroup = new();
    private readonly PreparationGroupViewModel _manualGroup = new();
    private PreparationWorkspace Preparations => _workspaceService.Preparations;
    private ProxyWorkspace Workspace => _workspaceService.Workspace;

    public ObservableCollection<PreparationTaskViewModel> Tasks { get; } = new();
    public ObservableCollection<PreparationGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    private bool _hasTasks;

    [ObservableProperty]
    private string _summary = string.Empty;

    public PreparationPageViewModel(WorkspaceService workspaceService, Dialogs dialogs,
        LogPageViewModel logs, INavigationService navigationService)
    {
        _workspaceService = workspaceService;
        _dialogs = dialogs;
        _logs = logs;
        _navigationService = navigationService;
        _workspaceService.Refreshed += Refresh;
        _workspaceService.Tick += Refresh;
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
        var tasks = Preparations.Tasks.Where(task => task.ClientType == Workspace.SelectedClient)
            .OrderBy(task => task.Number).ToList();
        var rows = new List<PreparationTaskViewModel>();
        var following = new List<PreparationTaskViewModel>();
        var manual = new List<PreparationTaskViewModel>();
        foreach (var task in tasks)
        {
            var item = Tasks.FirstOrDefault(item => item.Id == task.Id) ?? new PreparationTaskViewModel(task.Id);
            // 只读编辑副本来识别旧任务来源；跟随当前任务的组不随 Key 切换而改变。
            var mode = Preparations.OpenPrepareDialog(task.Id).Mode;
            item.Refresh(task, mode);
            rows.Add(item);
            (mode == PrepareMode.LocalProvider ? following : manual).Add(item);
        }

        Synchronize(Tasks, rows);
        _followingGroup.Title = ClientPageText.Translate("跟随当前（现在：{0}）", ClientPageText.CurrentProviderKey(Workspace));
        _manualGroup.Title = ClientPageText.Translate("手动填写");
        Synchronize(_followingGroup.Tasks, following);
        Synchronize(_manualGroup.Tasks, manual);
        var groups = new List<PreparationGroupViewModel>();
        if (following.Count > 0) groups.Add(_followingGroup);
        if (manual.Count > 0) groups.Add(_manualGroup);
        Synchronize(Groups, groups);
        HasTasks = tasks.Count > 0;
        Summary = HasTasks
            ? ClientPageText.Translate("共 {0} 项准备 · {1} 项运行中", tasks.Count, tasks.Count(task => task.CanStop))
            : string.Empty;
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
        await _dialogs.ShowPrepareOptionsAsync(options);
    }

    [RelayCommand]
    private async Task OnEditPreparation(string taskId)
    {
        if (Preparations.Find(taskId)?.CanStart == true)
        {
            await _dialogs.ShowPrepareOptionsAsync(Preparations.OpenPrepareDialog(taskId));
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
    [ObservableProperty] private string _stopText = string.Empty;

    public void Refresh(PreparationTask task, PrepareMode mode)
    {
        var host = Uri.TryCreate(task.ProviderUrl, UriKind.Absolute, out var address) ? address.Authority : task.ProviderUrl;
        Title = mode == PrepareMode.LocalProvider
            ? ClientPageText.Translate("准备 {0}", task.Number)
            : ClientPageText.Translate("准备 {0} · {1}", task.Number, host);
        ProviderUrl = task.ProviderUrl;
        var effort = task.ReasoningEffort == ReasoningEffort.Default
            ? ClientPageText.Translate("默认强度") : task.ReasoningEffort.AsStr();
        Settings = ClientPageText.Translate("{0} · {1} · {2} 分钟", task.Model, effort, task.IdleMinutes);
        var snapshot = task.Snapshot;
        IsPreparing = task.IsPreparing;
        Status = IsPreparing && snapshot is { PreparationAttempts: > 0 }
            ? ClientPageText.Translate("准备中 · 第 {0} 轮", snapshot.PreparationAttempts)
            : task.State == ServiceState.Running && !IsPreparing
                ? ClientPageText.Translate("已准备")
                : ClientPageText.Translate(task.Status);
        Hint = ClientPageText.Translate(task.Hint);
        Statistics = ClientPageText.Translate("本次运行：成功 {0} 轮 · 失败 {1} 轮 · 中断 {2} 轮",
            snapshot?.Totals.Completed ?? 0, snapshot?.Totals.Failed ?? 0, snapshot?.Totals.Interrupted ?? 0);
        Error = task.LastError ?? string.Empty;
        HasError = Error.Length > 0;
        CanStart = task.CanStart;
        CanStop = task.CanStop;
        StopText = ClientPageText.Translate(IsPreparing ? "终止准备" : "停止保活");
    }
}
