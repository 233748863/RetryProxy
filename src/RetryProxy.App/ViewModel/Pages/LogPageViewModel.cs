using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Workspace;
using RetryProxy.Helpers.Ui;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using RetryProxy.View.Pages;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    public static readonly TimeSpan ScrollResumeDelay = TimeSpan.FromSeconds(5);
    public const int PageSize = 200;
    private readonly WorkspaceService _workspaceService;
    private readonly DispatcherTimer _timer;
    private RuntimeLogStore Store => _workspaceService.Workspace.Logger.Store;
    private IReadOnlyList<RuntimeLogEntry> _entries = [];
    private CancellationTokenSource? _queryCancellation;
    private bool _active;
    private bool _dirty;
    private bool _hasResult;
    private bool _followToday = true;
    private DateOnly _observedToday;
    private int _version;
    private int _pageIndex;
    private long _snapshot;
    private long _afterId;
    private string? _lastRouteName;
    private ClientType? _lastClient;

    [ObservableProperty] private BatchObservableCollection<string> _rows = [];
    [ObservableProperty] private LogLevelFilter _filter = LogLevelFilter.All;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private bool _onlySelected;
    [ObservableProperty] private LogSource? _sourceFilter;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private int _shown;
    [ObservableProperty] private long _total;
    [ObservableProperty] private string _counter = "0 / 0";
    [ObservableProperty] private string? _selectedRouteName;
    [ObservableProperty] private string _onlySelectedLabel = string.Empty;
    [ObservableProperty] private string _emptyText = string.Empty;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _autoScrollToolTip = string.Empty;
    [ObservableProperty] private DateTime? _selectedDate;
    [ObservableProperty] private DateTime _earliestDate;
    [ObservableProperty] private DateTime _latestDate;
    [ObservableProperty] private string _pageText = string.Empty;
    [ObservableProperty] private string _warning = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _selectedRowIndex = -1;

    public bool ShowClientFilter => SourceFilter != LogSource.System;
    public bool HasWarning => Warning.Length > 0;
    public bool CanUseRows => _active && _hasResult && !IsLoading;
    public bool CanNewer => CanUseRows && _pageIndex > 0;
    public bool CanOlder => CanUseRows && ((long)_pageIndex + 1) * PageSize < Total && _pageIndex < int.MaxValue / PageSize - 1;
    public bool CanCopyRequest => _active && _hasResult && SelectedEntry?.RequestId is { Length: > 0 };
    public bool CanOpenDiagnostic => _active && _hasResult && SelectedEntry is { Source: LogSource.ChannelProxy, RequestId.Length: > 0, RequestDate: { } date }
        && date >= _workspaceService.Diagnostics.Today.AddDays(-6) && date <= _workspaceService.Diagnostics.Today;
    private RuntimeLogEntry? SelectedEntry => SelectedRowIndex >= 0 && SelectedRowIndex < _entries.Count ? _entries[SelectedRowIndex] : null;
    public event Action? RowsAppended;

    public LogPageViewModel(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
        _observedToday = Store.Today;
        _selectedDate = _observedToday.ToDateTime(TimeOnly.MinValue);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += async (_, _) =>
        {
            UpdateDates();
            if (!_dirty || IsLoading) return;
            _dirty = false;
            await ReloadAsync();
        };
        workspaceService.LogsChanged += () =>
        {
            if (_active && _pageIndex == 0 && SelectedDate?.Date == Store.Today.ToDateTime(TimeOnly.MinValue)) Schedule();
        };
        workspaceService.Refreshed += OnRefreshed;
        I18nService.Instance.PropertyChanged += (_, _) => { OnRefreshed(); UpdateCounters(); };
        UpdateDates();
        OnRefreshed();
        UpdateCounters();
    }

    public override void OnNavigatedTo()
    {
        if (_active) return;
        _active = true;
        UpdateDates();
        _timer.Start();
        Schedule();
    }

    public override void OnNavigatedFrom()
    {
        _active = false;
        _timer.Stop();
        _version++;
        _queryCancellation?.Cancel();
        IsLoading = false;
    }

    public bool IsFilter(LogLevelFilter filter) => Filter == filter;
    public void OpenPreparation(int taskNumber)
    {
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
        OnlySelectedLabel = ClientPageText.Translate("仅当前客户端：{0}", workspace.SelectedClient.Label());
        AutoScrollToolTip = ClientPageText.Translate("手动上滚后暂停跟随，连续 {0:F0} 秒无操作自动恢复；关闭后保持手动浏览", ScrollResumeDelay.TotalSeconds);
        if (OnlySelected && (_lastRouteName != SelectedRouteName || _lastClient != workspace.SelectedClient)) Schedule(resetPage: true);
        _lastRouteName = SelectedRouteName;
        _lastClient = workspace.SelectedClient;
    }

    private void UpdateDates()
    {
        var today = Store.Today;
        EarliestDate = DateOnly.FromDayNumber(Math.Max(0, today.DayNumber - Store.RetentionDays + 1)).ToDateTime(TimeOnly.MinValue);
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
            _pageIndex = 0;
            _version++;
            _queryCancellation?.Cancel();
            IsLoading = false;
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
        SnapshotId = _pageIndex > 0 ? _snapshot : null,
        AfterId = _afterId,
    };

    private async Task ReloadAsync()
    {
        var version = _version;
        _queryCancellation?.Dispose();
        _queryCancellation = new CancellationTokenSource();
        var token = _queryCancellation.Token;
        var query = CaptureQuery();
        IsLoading = true;
        try
        {
            var page = await Store.QueryAsync(query, token);
            if (version != _version || !_active) return;
            var selectedId = SelectedEntry?.Id;
            _snapshot = page.SnapshotId;
            _entries = page.Entries;
            Rows.ReplaceAll(_entries.Select(entry => entry.Line));
            SelectedRowIndex = selectedId is { } id ? _entries.ToList().FindIndex(entry => entry.Id == id) : -1;
            _hasResult = true;
            Total = page.Total;
            Warning = page.Warning ?? string.Empty;
            if (_pageIndex == 0 && query.Date == Store.Today) RowsAppended?.Invoke();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (version != _version || !_active) return;
            _hasResult = false;
            _entries = [];
            Rows.Clear();
            Total = 0;
            Warning = ClientPageText.Translate("读取运行日志失败，请稍后重试");
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
                UpdateCounters();
            }
        }
    }

    private void UpdateCounters()
    {
        Shown = Rows.Count;
        Counter = $"{Shown} / {Total}";
        IsEmpty = !IsLoading && Shown == 0;
        EmptyText = ClientPageText.Translate("当前日期没有符合筛选条件的运行日志");
        PageText = ClientPageText.Translate("第 {0} / {1} 页", _pageIndex + 1, Math.Max(1, (Total + PageSize - 1) / PageSize));
        OnPropertyChanged(nameof(CanNewer));
        OnPropertyChanged(nameof(CanOlder));
        OnPropertyChanged(nameof(CanUseRows));
        OnPropertyChanged(nameof(CanCopyRequest));
        OnPropertyChanged(nameof(CanOpenDiagnostic));
    }

    partial void OnSelectedDateChanged(DateTime? value)
    {
        if (value is null) { SelectedDate = LatestDate; return; }
        _followToday = DateOnly.FromDateTime(value.Value) == Store.Today;
        _afterId = 0;
        if (!_followToday) AutoScroll = false;
        Schedule(resetPage: true);
    }
    partial void OnQueryChanged(string value) => Schedule(resetPage: true);
    partial void OnOnlySelectedChanged(bool value) => Schedule(resetPage: true);
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnIsLoadingChanged(bool value) => UpdateCounters();
    partial void OnSelectedRowIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CanCopyRequest));
        OnPropertyChanged(nameof(CanOpenDiagnostic));
    }
    partial void OnFilterChanged(LogLevelFilter value)
    {
        foreach (var property in new[] { nameof(IsAll), nameof(IsInfo), nameof(IsWarning), nameof(IsError) }) OnPropertyChanged(property);
        Schedule(resetPage: true);
    }
    partial void OnSourceFilterChanged(LogSource? value)
    {
        foreach (var property in new[] { nameof(IsAllSources), nameof(IsChannelProxy), nameof(IsChannelKeepAlive), nameof(IsPreparation), nameof(IsSystem), nameof(ShowClientFilter) }) OnPropertyChanged(property);
        if (!ShowClientFilter) OnlySelected = false;
        Schedule(resetPage: true);
    }

    public bool IsAllSources => SourceFilter is null;
    public bool IsChannelProxy => SourceFilter == LogSource.ChannelProxy;
    public bool IsChannelKeepAlive => SourceFilter == LogSource.ChannelKeepAlive;
    public bool IsPreparation => SourceFilter == LogSource.Preparation;
    public bool IsSystem => SourceFilter == LogSource.System;
    public bool IsAll => Filter == LogLevelFilter.All;
    public bool IsInfo => Filter == LogLevelFilter.Info;
    public bool IsWarning => Filter == LogLevelFilter.Warning;
    public bool IsError => Filter == LogLevelFilter.Error;

    [RelayCommand] private void OnSetFilter(string name) => Filter = name switch
    {
        "info" => LogLevelFilter.Info, "warning" => LogLevelFilter.Warning, "error" => LogLevelFilter.Error, _ => LogLevelFilter.All,
    };
    [RelayCommand] private void OnToggleOnlySelected() => OnlySelected = !OnlySelected;
    [RelayCommand] private void OnSetSource(string name) => SourceFilter = name switch
    {
        "proxy" => LogSource.ChannelProxy, "keepalive" => LogSource.ChannelKeepAlive,
        "preparation" => LogSource.Preparation, "system" => LogSource.System, _ => null,
    };
    [RelayCommand] private void OnToday()
    {
        SelectedDate = Store.Today.ToDateTime(TimeOnly.MinValue);
        _afterId = 0;
        AutoScroll = true;
        _followToday = true;
        Schedule(resetPage: true);
    }
    [RelayCommand] private void OnRefresh() => Schedule(resetPage: true);
    [RelayCommand] private void OnOlder()
    {
        if (!CanOlder) return;
        _hasResult = false;
        _pageIndex++;
        AutoScroll = false;
        Schedule();
    }
    [RelayCommand] private void OnNewer()
    {
        if (!CanNewer) return;
        _hasResult = false;
        _pageIndex--;
        Schedule();
    }
    [RelayCommand] private void OnClear()
    {
        if (!CanUseRows) return;
        _afterId = _snapshot;
        _entries = [];
        Rows.Clear();
        SelectedRowIndex = -1;
        Total = 0;
        _workspaceService.ClearLogs();
        Schedule(resetPage: true);
    }
    [RelayCommand] private void OnCopyRequest()
    {
        if (!CanCopyRequest || SelectedEntry?.RequestId is not { } id) return;
        ClipboardSafe.SetText(id, success => _workspaceService.Workspace.Notice = ClientPageText.Translate(success ? "请求编号已复制" : "复制失败，请重试"));
    }
    [RelayCommand] private async Task OnOpenDiagnostic()
    {
        if (!CanOpenDiagnostic || SelectedEntry is not { RequestId: { } id, RequestDate: { } date }) return;
        var version = _version;
        try
        {
            var detail = await _workspaceService.Diagnostics.ReadDetailAsync(date, id);
            if (!_active || version != _version) return;
            if (detail.Summary is null)
            {
                _workspaceService.Workspace.Notice = ClientPageText.Translate("该请求没有可用的诊断记录");
                return;
            }
            var client = detail.Summary.Request.Client;
            var workspace = _workspaceService.Workspace;
            if (workspace.SelectedClient != client) workspace.SelectClient(client);
            _workspaceService.Flush();
            if (workspace.SelectedClient != client) return;
            if (App.GetService<INavigationService>()?.Navigate(typeof(RequestDiagnosticsPage)) == true)
                App.GetService<RequestDiagnosticsPageViewModel>()?.OpenRequest(date, id);
        }
        catch { _workspaceService.Workspace.Notice = ClientPageText.Translate("该请求没有可用的诊断记录"); }
    }
    [RelayCommand] private async Task OnExport()
    {
        if (!CanUseRows) return;
        var query = CaptureQuery() with { SnapshotId = _snapshot };
        var dialog = new SaveFileDialog
        {
            FileName = $"运行日志-{query.Date:yyyy-MM-dd}.log", Filter = "日志文本 (*.log)|*.log|文本文件 (*.txt)|*.txt",
            DefaultExt = ".log", AddExtension = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        var path = Path.GetFullPath(dialog.FileName);
        if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase))
        {
            Warning = ClientPageText.Translate("请选择日志文本文件，不能覆盖数据库");
            return;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                await Store.ExportAsync(query, output);
            File.Move(temporary, path, overwrite: true);
            _workspaceService.Workspace.Notice = ClientPageText.Translate("运行日志已导出");
        }
        catch { Warning = ClientPageText.Translate("运行日志导出失败，请检查目标文件是否可写"); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    [RelayCommand] private void OnOpenDirectory()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", Store.LogDirectory) { UseShellExecute = true }); }
        catch { _workspaceService.Workspace.Notice = $"日志目录：{Store.LogDirectory}"; }
    }

    public sealed class BatchObservableCollection<T> : ObservableCollection<T>
    {
        public void AddRange(IEnumerable<T> items)
        {
            if (items is IReadOnlyList<T> { Count: 1 } single)
            {
                Add(single[0]);
                return;
            }

            CheckReentrancy();
            var changed = false;
            foreach (var item in items)
            {
                Items.Add(item);
                changed = true;
            }

            if (changed)
            {
                RaiseReset();
            }
        }

        public void RemoveFirst(int count)
        {
            if (count <= 0)
            {
                return;
            }

            CheckReentrancy();
            if (Items is List<T> list)
            {
                list.RemoveRange(0, Math.Min(count, list.Count));
            }
            else
            {
                for (var index = 0; index < count && Items.Count > 0; index++)
                {
                    Items.RemoveAt(0);
                }
            }

            RaiseReset();
        }

        public void ReplaceAll(IEnumerable<T> items)
        {
            CheckReentrancy();
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            RaiseReset();
        }

        private void RaiseReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
