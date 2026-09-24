using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace RetryProxy.ViewModel.Pages;

public partial class PreparationPageViewModel : ViewModel
{
    private readonly WorkspaceService _workspaceService;
    private readonly Dialogs _dialogs;
    private PreparationWorkspace Preparations => _workspaceService.Preparations;

    public ObservableCollection<PreparationTaskViewModel> Tasks { get; } = new();

    [ObservableProperty]
    private bool _hasTasks;

    [ObservableProperty]
    private string _summary = string.Empty;

    public PreparationPageViewModel(WorkspaceService workspaceService, Dialogs dialogs)
    {
        _workspaceService = workspaceService;
        _dialogs = dialogs;
        _workspaceService.Refreshed += Refresh;
        _workspaceService.Tick += Refresh;
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
        for (var index = Tasks.Count - 1; index >= 0; index--)
        {
            if (Preparations.Find(Tasks[index].Id) is null)
            {
                Tasks.RemoveAt(index);
            }
        }
        foreach (var task in Preparations.Tasks.OrderBy(task => task.Number))
        {
            var item = Tasks.FirstOrDefault(item => item.Id == task.Id);
            if (item is null)
            {
                item = new PreparationTaskViewModel(task.Id);
                Tasks.Add(item);
            }
            item.Refresh(task);
        }
        HasTasks = Tasks.Count > 0;
        Summary = HasTasks ? $"共 {Tasks.Count} 项准备 · {Preparations.Tasks.Count(task => task.CanStop)} 项运行中" : string.Empty;
    }

    [RelayCommand]
    private async Task OnAddPreparation() => await _dialogs.ShowPrepareOptionsAsync(Preparations.OpenPrepareDialog());

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

    public void Refresh(PreparationTask task)
    {
        Title = task.Title;
        ProviderUrl = task.ProviderUrl;
        Settings = $"模型 {task.Model} · 每 {task.IdleMinutes} 分钟保活";
        Status = task.Status;
        Hint = task.Hint;
        var snapshot = task.Snapshot;
        Statistics = $"成功 {snapshot?.Totals.Completed ?? 0} 轮 · 失败 {snapshot?.Totals.Failed ?? 0} 轮 · 中断 {snapshot?.Totals.Interrupted ?? 0} 轮";
        Error = task.LastError ?? string.Empty;
        HasError = Error.Length > 0;
        CanStart = task.CanStart;
        CanStop = task.CanStop;
        IsPreparing = task.IsPreparing;
        StopText = IsPreparing ? "终止准备" : "停止保活";
    }
}
