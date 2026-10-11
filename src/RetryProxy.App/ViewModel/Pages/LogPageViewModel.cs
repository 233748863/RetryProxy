using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Workspace;
using RetryProxy.Helpers.Ui;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using RetryProxy.Service.Interface;
using RetryProxy.View.Pages;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui;

namespace RetryProxy.ViewModel.Pages;

public partial class LogPageViewModel : ViewModel
{
    public const int PageSize = 200;
    public const int RequestPageSize = 100;
    private readonly WorkspaceService _workspaceService;
    private readonly IConfigService _configService;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private RuntimeLogStore Store => _workspaceService.Workspace.Logger.Store;
    private CancellationTokenSource? _queryCancellation;
    private bool _active;
    private bool _dirty;
    private bool _hasResult;
    private bool _followToday = true;
    private bool _resetRows = true;
    private bool _resetScroll = true;
    private bool _applyingRows;
    private DateOnly _observedToday;
    private int _notificationQueued;
    private int _version;
    private int _pageIndex;
    private long _snapshot;
    private long _afterId;
    private string? _lastRouteName;
    private ClientType? _lastClient;
    private string _requestSearch = string.Empty;
    private string _eventSearch = string.Empty;

    public ObservableCollection<LogEventRow> Rows { get; } = [];
    public ObservableCollection<RequestLogRow> RequestRows { get; } = [];
    public event Action<bool>? RowsUpdating;
    public event Action<bool>? RowsUpdated;

    [ObservableProperty] private bool _isRequestView = true;
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private LogLevelFilter _filter = LogLevelFilter.All;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private bool _onlySelected;
    [ObservableProperty] private LogSource? _sourceFilter;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private long _total;
    [ObservableProperty] private string _counter = string.Empty;
    [ObservableProperty] private string? _selectedRouteName;
    [ObservableProperty] private string _onlySelectedLabel = string.Empty;
    [ObservableProperty] private string _emptyText = string.Empty;
    [ObservableProperty] private DateTime? _selectedDate;
    [ObservableProperty] private DateTime _earliestDate;
    [ObservableProperty] private DateTime _latestDate;
    [ObservableProperty] private string _pageText = string.Empty;
    [ObservableProperty] private string _warning = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private LogEventRow? _selectedEvent;
    [ObservableProperty] private RequestLogRow? _selectedRequest;

    public bool IsEventView => !IsRequestView;
    public bool ShowClientFilter => IsRequestView || SourceFilter != LogSource.System;
    public bool HasWarning => Warning.Length > 0;
    public bool CanUseRows => _active && _hasResult;
    public bool CanExport => CanUseRows && Total > 0;
    public bool ShowLoading => IsLoading && !_hasResult;
    public bool IsEmpty => _hasResult && (IsRequestView ? RequestRows.Count : Rows.Count) == 0;
    public bool CanNewer => CanUseRows && _pageIndex > 0;
    public bool CanOlder => CanUseRows && ((long)_pageIndex + 1) * CurrentPageSize < Total;
    public bool CanCopyRequest => CanUseRows && SelectedEvent?.Entry.RequestId is { Length: > 0 };
    public bool CanOpenDiagnostic => CanUseRows && SelectedEvent?.Entry is
        { Source: LogSource.ChannelProxy, RequestId.Length: > 0, RequestDate: { } date }
        && date >= _workspaceService.Diagnostics.Today.AddDays(-6) && date <= _workspaceService.Diagnostics.Today;
    public bool ShowFollowHint => !AutoScroll && SelectedDate?.Date == LatestDate;
    public string FollowHint => PendingCount > 0
        ? T(IsRequestView ? "有 {0} 条新请求" : "有 {0} 条新事件", PendingCount) : T("已暂停跟随，保持当前阅读位置");
    public string SearchHint => T(IsRequestView ? "搜索模型、供应商、请求编号…" : "搜索日志内容、完整请求编号…");
    public string CounterUnit => T(IsRequestView ? "条请求" : "条事件");
    private int CurrentPageSize => IsRequestView ? RequestPageSize : PageSize;
    private static string T(string text, params object[] args) => ClientPageText.Translate(text, args);

    public LogPageViewModel(WorkspaceService workspaceService, IConfigService configService)
    {
        _workspaceService = workspaceService;
        _configService = configService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _observedToday = Store.Today;
        _selectedDate = _observedToday.ToDateTime(TimeOnly.MinValue);
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(750) };
        _timer.Tick += RefreshTick;
        UpdateDates();
        OnRefreshed();
        UpdateCounters();
    }

    public override void OnNavigatedTo()
    {
        if (_active) return;
        _active = true;
        _workspaceService.LogsChanged += LogsChanged;
        _workspaceService.Refreshed += OnRefreshed;
        _workspaceService.Diagnostics.Changed += DiagnosticsChanged;
        I18nService.Instance.PropertyChanged += LanguageChanged;
        UpdateDates();
        RefreshText();
        _timer.Start();
        Schedule();
    }

    public override void OnNavigatedFrom()
    {
        _active = false;
        _workspaceService.LogsChanged -= LogsChanged;
        _workspaceService.Refreshed -= OnRefreshed;
        _workspaceService.Diagnostics.Changed -= DiagnosticsChanged;
        I18nService.Instance.PropertyChanged -= LanguageChanged;
        _timer.Stop();
        _version++;
        _queryCancellation?.Cancel();
        IsLoading = false;
    }

    private async void RefreshTick(object? sender, EventArgs args)
    {
        UpdateDates();
        if (!_dirty || _queryCancellation is not null) return;
        _dirty = false;
        await ReloadAsync();
    }

    private void LogsChanged()
    {
        if (_active && SelectedDate?.Date == LatestDate) Schedule();
    }

    private void DiagnosticsChanged()
    {
        if (Interlocked.Exchange(ref _notificationQueued, 1) != 0 || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _notificationQueued, 0);
            if (_active && IsRequestView && SelectedDate?.Date == LatestDate) Schedule();
        });
    }

    public void PauseFollowing()
    {
        if (_hasResult) AutoScroll = false;
    }

    public void OpenPreparation(int taskNumber)
    {
        IsRequestView = false;
        OnToday();
        OnlySelected = false;
        Filter = LogLevelFilter.All;
        SourceFilter = LogSource.Preparation;
        Query = "[准备 " + taskNumber.ToString(CultureInfo.InvariantCulture) + " · ";
    }

    private void OnRefreshed()
    {
        var workspace = _workspaceService.Workspace;
        SelectedRouteName = workspace.SelectedRouteRef()?.Name;
        OnlySelectedLabel = T("仅当前客户端：{0}", workspace.SelectedClient.Label());
        if (OnlySelected && (_lastRouteName != SelectedRouteName || _lastClient != workspace.SelectedClient)) Schedule(resetPage: true);
        _lastRouteName = SelectedRouteName;
        _lastClient = workspace.SelectedClient;
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(I18nService.Revision)) return;
        RefreshText();
        Schedule();
    }

    private void RefreshText()
    {
        OnRefreshed();
        OnPropertyChanged(nameof(SearchHint));
        OnPropertyChanged(nameof(CounterUnit));
        OnPropertyChanged(nameof(FollowHint));
        foreach (var row in Rows) row.RefreshLanguage();
    }

    private void UpdateDates()
    {
        var today = Store.Today;
        var days = IsRequestView ? 7 : Store.RetentionDays;
        EarliestDate = DateOnly.FromDayNumber(Math.Max(0, today.DayNumber - days + 1)).ToDateTime(TimeOnly.MinValue);
        LatestDate = today.ToDateTime(TimeOnly.MinValue);
        if (today != _observedToday && _followToday) SelectedDate = LatestDate;
        _observedToday = today;
        if (SelectedDate < EarliestDate || SelectedDate > LatestDate) SelectedDate = LatestDate;
    }

    private void Schedule(bool resetPage = false)
    {
        if (resetPage)
        {
            _hasResult = false;
            _resetRows = true;
            _resetScroll = true;
            _pageIndex = 0;
            _snapshot = 0;
            _requestSnapshot = null;
            SelectedEvent = null;
            SelectedRequest = null;
            PendingCount = 0;
            _version++;
            _queryCancellation?.Cancel();
        }
        _dirty = true;
        UpdateCounters();
    }

    private RuntimeLogQuery CaptureQuery() => new(DateOnly.FromDateTime(SelectedDate ?? LatestDate))
    {
        Source = SourceFilter,
        Level = Filter switch { LogLevelFilter.Info => "INFO", LogLevelFilter.Warning => "WARNING", LogLevelFilter.Error => "ERROR", _ => null },
        Search = Query,
        RouteName = OnlySelected && ShowClientFilter ? SelectedRouteName ?? string.Empty : null,
        PreparationSuffix = " · " + _workspaceService.Workspace.SelectedClient.Label(),
        Offset = _pageIndex * PageSize,
        PageSize = PageSize,
        SnapshotId = !_resetRows && (!AutoScroll || _pageIndex > 0) ? _snapshot : null,
        AfterId = _afterId,
    };

    private async Task ReloadAsync()
    {
        var version = _version;
        using var cancellation = new CancellationTokenSource();
        _queryCancellation = cancellation;
        var token = cancellation.Token;
        IsLoading = true;
        try
        {
            if (IsRequestView) await ReloadRequestsAsync(version, token);
            else
            {
                var query = CaptureQuery();
                var page = await Store.QueryAsync(query, token);
                var latest = !AutoScroll && query.Date == Store.Today
                    ? await Store.QueryAsync(query with { SnapshotId = null, Offset = 0, PageSize = 1 }, token) : page;
                if (!Current(version, token)) return;
                var previous = Rows.ToDictionary(row => (row.Entry.Date, row.Entry.Id));
                var rows = page.Entries.Reverse().Select(entry => previous.GetValueOrDefault((entry.Date, entry.Id)) ?? new LogEventRow(entry)).ToArray();
                ApplyRows(() => CollectionSync.Update(Rows, rows));
                _snapshot = page.SnapshotId;
                Total = page.Total;
                PendingCount = (int)Math.Min(int.MaxValue, Math.Max(0, latest.Total - page.Total));
                Warning = T(page.Warning ?? string.Empty);
            }
            if (!Current(version, token)) return;
            _hasResult = true;
            _resetRows = false;
            _resetScroll = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (Current(version, token)) Warning = T("读取日志失败，请稍后重试");
        }
        finally
        {
            if (ReferenceEquals(_queryCancellation, cancellation)) _queryCancellation = null;
            if (version == _version || _active) IsLoading = false;
            UpdateCounters();
        }
    }

    private bool Current(int version, CancellationToken token) => _active && version == _version && !token.IsCancellationRequested;

    private void ApplyRows(Action update)
    {
        _applyingRows = true;
        try
        {
            RowsUpdating?.Invoke(_resetScroll);
            update();
            RowsUpdated?.Invoke(_resetScroll);
        }
        finally { _applyingRows = false; }
    }

    private void UpdateCounters()
    {
        Counter = $"{(IsRequestView ? RequestRows.Count : Rows.Count)} / {Total}";
        EmptyText = T(IsRequestView ? "当前日期没有符合筛选条件的请求" : "当前日期没有符合筛选条件的运行事件");
        PageText = T("第 {0} / {1} 页", _pageIndex + 1, Math.Max(1, (Total + CurrentPageSize - 1) / CurrentPageSize));
        foreach (var name in new[] { nameof(CanNewer), nameof(CanOlder), nameof(CanUseRows), nameof(CanExport), nameof(CanCopyRequest),
            nameof(CanOpenDiagnostic), nameof(IsEmpty), nameof(ShowLoading), nameof(ShowFollowHint) }) OnPropertyChanged(name);
    }

    partial void OnSelectedDateChanged(DateTime? value)
    {
        if (value is null) { SelectedDate = LatestDate; return; }
        _followToday = DateOnly.FromDateTime(value.Value) == Store.Today;
        _afterId = 0;
        if (!_followToday) AutoScroll = false;
        Schedule(resetPage: true);
    }
    partial void OnIsRequestViewChanged(bool value)
    {
        if (value) { _eventSearch = Query; Query = _requestSearch; }
        else { _requestSearch = Query; Query = _eventSearch; }
        _afterId = 0;
        OnPropertyChanged(nameof(IsEventView));
        OnPropertyChanged(nameof(ShowClientFilter));
        OnPropertyChanged(nameof(SearchHint));
        OnPropertyChanged(nameof(CounterUnit));
        UpdateDates();
        Schedule(resetPage: true);
    }
    partial void OnAutoScrollChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFollowHint));
        OnPropertyChanged(nameof(FollowHint));
        if (value) Schedule(resetPage: true);
        else if (_queryCancellation is not null)
        {
            // 勾选/滚动发生在后台读取期间时，旧结果不能再插入新行。
            _version++;
            _queryCancellation.Cancel();
            Schedule();
        }
    }
    partial void OnPendingCountChanged(int value) => OnPropertyChanged(nameof(FollowHint));
    partial void OnQueryChanged(string value) => Schedule(resetPage: true);
    partial void OnOnlySelectedChanged(bool value) => Schedule(resetPage: true);
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnIsLoadingChanged(bool value) => UpdateCounters();
    partial void OnSelectedRequestChanged(RequestLogRow? value) { if (!_applyingRows && value is not null) PauseFollowing(); }
    partial void OnSelectedEventChanged(LogEventRow? value)
    {
        if (!_applyingRows && value is not null) PauseFollowing();
        OnPropertyChanged(nameof(CanCopyRequest));
        OnPropertyChanged(nameof(CanOpenDiagnostic));
    }
    partial void OnFilterChanged(LogLevelFilter value) => Schedule(resetPage: true);
    partial void OnSourceFilterChanged(LogSource? value)
    {
        OnPropertyChanged(nameof(ShowClientFilter));
        if (!ShowClientFilter) OnlySelected = false;
        Schedule(resetPage: true);
    }

    [RelayCommand] private void OnSetView(string name) => IsRequestView = name != "events";
    [RelayCommand] private void OnToggleOnlySelected() => OnlySelected = !OnlySelected;
    [RelayCommand] private void OnToday()
    {
        SelectedDate = Store.Today.ToDateTime(TimeOnly.MinValue);
        _afterId = 0;
        _followToday = true;
        OnLatest();
    }
    [RelayCommand] private void OnLatest() { AutoScroll = true; Schedule(resetPage: true); }
    [RelayCommand] private async Task OnRefresh()
    {
        if (!_active) return;
        Schedule();
        if (_queryCancellation is not null) return;
        _dirty = false;
        await ReloadAsync();
    }
    [RelayCommand] private void OnOlder()
    {
        if (!CanOlder) return;
        AutoScroll = false;
        _pageIndex++;
        PageChanged();
    }
    [RelayCommand] private void OnNewer()
    {
        if (!CanNewer) return;
        _pageIndex--;
        PageChanged();
    }
    private void PageChanged()
    {
        _version++;
        _queryCancellation?.Cancel();
        SelectedEvent = null;
        SelectedRequest = null;
        _resetScroll = true;
        Schedule();
    }
    [RelayCommand] private void OnClear()
    {
        if (!CanUseRows || IsRequestView) return;
        _afterId = _snapshot;
        Rows.Clear();
        SelectedEvent = null;
        Total = 0;
        _workspaceService.ClearLogs();
        Schedule(resetPage: true);
    }
    [RelayCommand] private void OnToggleEvent(LogEventRow? row)
    {
        if (row is null || !CanUseRows) return;
        SelectedEvent = row;
        row.IsExpanded = !row.IsExpanded;
    }
    [RelayCommand] private void OnCopyEvent()
    {
        if (CanUseRows && SelectedEvent is { } row) Copy(row.FullLine, "日志已复制");
    }
    [RelayCommand] private void OnCopyRequest()
    {
        if (CanCopyRequest) Copy(SelectedEvent!.Entry.RequestId!, "请求编号已复制");
    }
    private void Copy(string text, string successText) => ClipboardSafe.SetText(text,
        success => _workspaceService.Workspace.Notice = T(success ? successText : "复制失败，请重试"));

    [RelayCommand] private async Task OnOpenDiagnostic()
    {
        if (!CanOpenDiagnostic || SelectedEvent?.Entry is not { RequestId: { } id, RequestDate: { } date }) return;
        var version = _version;
        try
        {
            var detail = await _workspaceService.Diagnostics.ReadDetailAsync(date, id);
            if (!_active || version != _version) return;
            if (detail.Summary is not { } summary)
            { _workspaceService.Workspace.Notice = T("该请求没有可用的诊断记录"); return; }
            var workspace = _workspaceService.Workspace;
            if (workspace.SelectedClient != summary.Request.Client) workspace.SelectClient(summary.Request.Client);
            _workspaceService.Flush();
            if (workspace.SelectedClient != summary.Request.Client) return;
            if (App.GetService<INavigationService>()?.Navigate(typeof(RequestDiagnosticsPage)) == true)
                App.GetService<RequestDiagnosticsPageViewModel>()?.OpenRequest(date, id);
        }
        catch { _workspaceService.Workspace.Notice = T("该请求没有可用的诊断记录"); }
    }

    [RelayCommand] private async Task OnExport()
    {
        if (!CanExport) return;
        var requests = IsRequestView;
        var query = CaptureQuery() with { SnapshotId = _snapshot };
        var summaries = _requestSnapshot?.ToArray() ?? [];
        var prices = CurrentPrices();
        var dialog = new SaveFileDialog
        {
            FileName = $"{(requests ? "请求日志" : "运行事件")}-{query.Date:yyyy-MM-dd}.{(requests ? "csv" : "log")}",
            Filter = requests ? "CSV (*.csv)|*.csv" : "日志文本 (*.log)|*.log|文本文件 (*.txt)|*.txt",
            DefaultExt = requests ? ".csv" : ".log", AddExtension = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        var path = Path.GetFullPath(dialog.FileName);
        if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase))
        { Warning = T("请选择日志文本文件，不能覆盖数据库"); return; }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new StreamWriter(temporary, false, new UTF8Encoding(requests)))
            {
                if (requests) await ExportRequestsAsync(output, summaries, query.Date, prices);
                else await Store.ExportAsync(query, output);
            }
            File.Move(temporary, path, overwrite: true);
            _workspaceService.Workspace.Notice = T("日志已导出");
        }
        catch { Warning = T("日志导出失败，请检查目标文件是否可写"); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    [RelayCommand] private void OnOpenDirectory()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", Store.LogDirectory) { UseShellExecute = true }); }
        catch { _workspaceService.Workspace.Notice = T("日志目录：{0}", Store.LogDirectory); }
    }
}
