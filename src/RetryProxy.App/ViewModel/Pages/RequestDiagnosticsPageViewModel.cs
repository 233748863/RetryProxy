using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Diagnostics;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace RetryProxy.ViewModel.Pages;

public partial class RequestDiagnosticsPageViewModel : ViewModel
{
    private const int PageSize = 100;
    private readonly WorkspaceService _workspaceService;
    private readonly DrawerService _drawers;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private IDiagnosticRepository Repository => _workspaceService.Diagnostics;
    private CancellationTokenSource? _queryCancellation;
    private DiagnosticFilters _filters = DiagnosticFilters.Empty;
    private string? _warningKey;
    private ClientType _client;
    private long _version;
    private int _notificationQueued;
    private int _offset;
    private bool _active;
    private bool _dirty;
    private bool _updatingChoices;
    private bool _resetScroll;

    public ObservableCollection<RequestDiagnosticRow> Rows { get; } = new();
    public ObservableCollection<DiagnosticDateOption> Dates { get; } = new();
    public ObservableCollection<DiagnosticFilterOption> Outcomes { get; } = new();
    public ObservableCollection<DiagnosticFilterOption> Providers { get; } = new();
    public ObservableCollection<DiagnosticFilterOption> Keys { get; } = new();
    public ObservableCollection<DiagnosticFilterOption> Models { get; } = new();
    public event Action<bool>? RowsUpdating;
    public event Action<bool>? RowsUpdated;

    [ObservableProperty] private DateOnly _selectedDate;
    [ObservableProperty] private DiagnosticFilterOption? _selectedOutcome;
    [ObservableProperty] private DiagnosticFilterOption? _selectedProvider;
    [ObservableProperty] private DiagnosticFilterOption? _selectedKey;
    [ObservableProperty] private DiagnosticFilterOption? _selectedModel;
    [ObservableProperty] private string _requestIdSearch = string.Empty;
    [ObservableProperty] private string _warning = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _pageText = string.Empty;

    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public bool IsEmpty => !IsBusy && Rows.Count == 0;
    public bool CanPrevious => !IsBusy && _offset > 0;
    public bool CanNext => !IsBusy && HasMore;

    public RequestDiagnosticsPageViewModel(WorkspaceService workspaceService, DrawerService drawers)
    {
        _workspaceService = workspaceService;
        _drawers = drawers;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += RefreshTick;
        _selectedDate = Repository.Today;
        _client = _workspaceService.Workspace.SelectedClient;
        UpdateDates();
        UpdateChoices();
        UpdatePageState();
    }

    public override void OnNavigatedTo() => Activate();
    public override void OnNavigatedFrom() => Deactivate();

    public void Activate()
    {
        if (_active) return;
        _active = true;
        Repository.Changed += RepositoryChanged;
        _workspaceService.Refreshed += WorkspaceChanged;
        I18nService.Instance.PropertyChanged += LanguageChanged;
        WorkspaceChanged();
        UpdateDates();
        Schedule(invalidate: true);
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        Repository.Changed -= RepositoryChanged;
        _workspaceService.Refreshed -= WorkspaceChanged;
        I18nService.Instance.PropertyChanged -= LanguageChanged;
        _refreshTimer.Stop();
        _dirty = false;
        _version++;
        _queryCancellation?.Cancel();
        IsBusy = false;
    }

    private static string T(string key) => I18nService.Instance.Translate(key);

    private void RepositoryChanged()
    {
        if (Interlocked.Exchange(ref _notificationQueued, 1) != 0 || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _notificationQueued, 0);
            if (!_active) return;
            UpdateDates();
            Schedule();
        });
    }

    private void WorkspaceChanged()
    {
        var client = _workspaceService.Workspace.SelectedClient;
        if (_client != client)
        {
            _client = client;
            _filters = DiagnosticFilters.Empty;
            _updatingChoices = true;
            SelectedProvider = SelectedKey = SelectedModel = SelectedOutcome = null;
            _updatingChoices = false;
            UpdateChoices();
            FilterChanged();
        }
        else UpdateChoices(); // 当前配置仅用于标记历史身份是否已删除；不触发磁盘查询。
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(I18nService.Revision)) return;
        UpdateDates();
        UpdateChoices();
        foreach (var row in Rows) row.Refresh(row.Summary);
        Warning = _warningKey is null ? string.Empty : T(_warningKey);
        UpdatePageState();
    }

    partial void OnSelectedDateChanged(DateOnly value) => FilterChanged();
    partial void OnSelectedOutcomeChanged(DiagnosticFilterOption? value) => FilterChanged();
    partial void OnSelectedProviderChanged(DiagnosticFilterOption? value)
    {
        if (_updatingChoices) return;
        _updatingChoices = true;
        SelectedKey = null;
        _updatingChoices = false;
        UpdateChoices();
        FilterChanged();
    }
    partial void OnSelectedKeyChanged(DiagnosticFilterOption? value) => FilterChanged();
    partial void OnSelectedModelChanged(DiagnosticFilterOption? value) => FilterChanged();
    partial void OnRequestIdSearchChanged(string value) => FilterChanged();
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnIsBusyChanged(bool value) => UpdatePageState();
    partial void OnHasMoreChanged(bool value) => UpdatePageState();

    private void FilterChanged()
    {
        if (_updatingChoices) return;
        _offset = 0;
        _resetScroll = true;
        Schedule(invalidate: true);
        UpdatePageState();
    }

    private void Schedule(bool invalidate = false)
    {
        if (invalidate)
        {
            _version++;
            _queryCancellation?.Cancel();
        }
        if (!_active) return;
        _dirty = true;
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    private async void RefreshTick(object? sender, EventArgs args)
    {
        _refreshTimer.Stop();
        // 记录变化不取消正在完成的查询；合并为之后的一次刷新，避免高流量时查询永远不能完成。
        if (!_active || !_dirty || _queryCancellation is not null) return;
        _dirty = false;
        await QueryAsync();
    }

    private async Task QueryAsync()
    {
        var version = _version;
        using var cancellation = new CancellationTokenSource();
        _queryCancellation = cancellation;
        IsBusy = true;
        try
        {
            var query = new DiagnosticQuery(SelectedDate, _client)
            {
                Outcome = Enum.TryParse<DiagnosticOutcome>(SelectedOutcome?.Id, out var outcome) ? outcome : null,
                // 未限定供应商时，Key 仍携带所属供应商 ID，避免把不同发送的身份拼成一组。
                ProviderId = SelectedProvider?.Id ?? SelectedKey?.ProviderId,
                KeyId = SelectedKey?.Id,
                Model = SelectedModel?.Id,
                RequestId = string.IsNullOrWhiteSpace(RequestIdSearch) ? null : RequestIdSearch.Trim(),
                Offset = _offset,
                PageSize = PageSize,
            };
            var page = await Repository.QueryAsync(query, cancellation.Token);
            if (!_active || version != _version || cancellation.IsCancellationRequested) return;
            _filters = page.Filters;
            UpdateChoices();
            _warningKey = page.Warning ?? Repository.Warning;
            Warning = _warningKey is null ? string.Empty : T(_warningKey);
            var previous = Rows.ToDictionary(row => row.RequestId, StringComparer.Ordinal);
            var rows = page.Items.Select(summary =>
            {
                var row = previous.GetValueOrDefault(summary.Request.RequestId) ?? new RequestDiagnosticRow(summary);
                row.Refresh(summary);
                return row;
            }).ToList();
            var resetScroll = _resetScroll;
            RowsUpdating?.Invoke(resetScroll);
            CollectionSync.Update(Rows, rows);
            RowsUpdated?.Invoke(resetScroll);
            _resetScroll = false;
            HasMore = page.HasMore;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_active && version == _version)
            {
                _warningKey = "读取诊断失败，请重新筛选或稍后重试";
                Warning = T(_warningKey);
            }
        }
        finally
        {
            if (ReferenceEquals(_queryCancellation, cancellation)) _queryCancellation = null;
            IsBusy = false;
            UpdatePageState();
            if (_active && _dirty) _refreshTimer.Start();
        }
    }

    private void UpdateDates()
    {
        var today = Repository.Today;
        var oldDate = SelectedDate;
        _updatingChoices = true;
        var dates = Enumerable.Range(0, 7).Select(index =>
        {
            var date = today.AddDays(-index);
            var label = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new DiagnosticDateOption(date, index == 0 ? $"{T("今天")} · {label}" : label);
        }).ToList();
        if (!Dates.SequenceEqual(dates)) CollectionSync.Update(Dates, dates);
        SelectedDate = oldDate < today.AddDays(-6) || oldDate > today ? today : oldDate;
        _updatingChoices = false;
        if (SelectedDate != oldDate) FilterChanged();
    }

    private void UpdateChoices()
    {
        _updatingChoices = true;
        try
        {
            SelectedOutcome = SyncOptions(Outcomes, Enum.GetValues<DiagnosticOutcome>()
                .Select(value => new DiagnosticFilterOption(value.ToString(), RequestDiagnosticText.Outcome(value, T))), SelectedOutcome);
            var config = _workspaceService.Workspace.Config;
            SelectedProvider = SyncOptions(Providers, _filters.Providers.Select(choice => new DiagnosticFilterOption(choice.Id,
                HistoricalName(choice.Name, config.ProviderById(choice.Id) is null))), SelectedProvider);
            SelectedKey = SyncOptions(Keys, _filters.Keys.Where(choice => SelectedProvider?.Id is null || choice.ProviderId == SelectedProvider.Id)
                .Select(choice =>
                {
                    var provider = _filters.Providers.FirstOrDefault(item => item.Id == choice.ProviderId);
                    var label = $"{provider?.Name ?? T(RequestDiagnosticText.Missing)} · {choice.Name}";
                    var deleted = choice.ProviderId is null || config.ProviderById(choice.ProviderId)?.KeyById(choice.Id) is null;
                    return new DiagnosticFilterOption(choice.Id, HistoricalName(label, deleted), choice.ProviderId);
                }), SelectedKey);
            SelectedModel = SyncOptions(Models, _filters.Models.Select(model => new DiagnosticFilterOption(model, model)), SelectedModel);
        }
        finally { _updatingChoices = false; }
    }

    private static string HistoricalName(string name, bool deleted) => deleted
        ? string.Format(CultureInfo.CurrentCulture, T("{0}（已删除）"), name) : name;

    private static DiagnosticFilterOption SyncOptions(ObservableCollection<DiagnosticFilterOption> target,
        IEnumerable<DiagnosticFilterOption> values, DiagnosticFilterOption? selected)
    {
        var incoming = new[] { new DiagnosticFilterOption(null, T("全部")) }.Concat(values).ToList();
        // 保留选中历史身份；查询返回空页时也不能静默放宽用户筛选。
        if (selected?.Id is not null && !incoming.Any(item => item.Id == selected.Id && item.ProviderId == selected.ProviderId)) incoming.Add(selected);
        var stable = incoming.Select(item =>
        {
            var old = target.FirstOrDefault(option => option.Id == item.Id && option.ProviderId == item.ProviderId);
            if (old is null) return item;
            old.Label = item.Label;
            return old;
        }).ToList();
        CollectionSync.Update(target, stable);
        return stable.FirstOrDefault(item => item.Id == selected?.Id && item.ProviderId == selected?.ProviderId) ?? stable[0];
    }

    private void UpdatePageState()
    {
        PageText = string.Format(CultureInfo.CurrentCulture, T("第 {0} 页 · 本页 {1} 条 · 每页 100 条"), _offset / PageSize + 1, Rows.Count);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void PreviousPage()
    {
        _offset = Math.Max(0, _offset - PageSize);
        _resetScroll = true;
        Schedule(invalidate: true);
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void NextPage()
    {
        _offset += PageSize;
        _resetScroll = true;
        Schedule(invalidate: true);
    }

    // 抽屉关闭前 ShowAsync 尚未结束；保留请求行可用，宿主才能把焦点还给它。
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenDetailAsync(RequestDiagnosticRow? row) => row is null ? Task.CompletedTask
        : _drawers.ShowRequestDiagnosticAsync(row.Summary);
}

public sealed record DiagnosticDateOption(DateOnly Date, string Label)
{
    public override string ToString() => Label;
}

public partial class DiagnosticFilterOption(string? id, string label, string? providerId = null) : ObservableObject
{
    public string? Id { get; } = id;
    public string? ProviderId { get; } = providerId;
    [ObservableProperty] private string _label = label;
    public override string ToString() => Label;
}

public partial class RequestDiagnosticRow : ObservableObject
{
    public RequestDiagnosticRow(DiagnosticSummary summary) { Summary = summary; Refresh(summary); }
    public DiagnosticSummary Summary { get; private set; }
    public string RequestId => Summary.Request.RequestId;
    [ObservableProperty] private string _time = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private string _target = string.Empty;
    [ObservableProperty] private string _result = string.Empty;
    [ObservableProperty] private string _duration = string.Empty;
    [ObservableProperty] private string _completeness = string.Empty;
    public bool IsIncomplete => Summary.Incomplete;
    public override string ToString() => $"{Time} · {Model} · {Target} · {Result} · {Duration}";

    public void Refresh(DiagnosticSummary summary)
    {
        Summary = summary;
        var translate = I18nService.Instance.Translate;
        Time = summary.Request.StartedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Model = RequestDiagnosticText.Value(summary.LastTarget?.Model, translate);
        Target = RequestDiagnosticText.Target(summary.LastTarget, translate);
        Result = RequestDiagnosticText.Result(summary, translate);
        Duration = RequestDiagnosticText.Seconds(summary.TotalSeconds, translate);
        Completeness = RequestDiagnosticText.Completeness(summary, translate);
        OnPropertyChanged(nameof(IsIncomplete));
    }
}
