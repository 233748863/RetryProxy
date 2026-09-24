using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;

namespace RetryProxy.ViewModel.Pages;

public partial class LogPageViewModel : ViewModel
{
    /// <summary>手动上滚后自动恢复跟随的空闲时长。</summary>
    public static readonly TimeSpan ScrollResumeDelay = TimeSpan.FromSeconds(5);

    private readonly WorkspaceService _workspaceService;
    private readonly List<long> _rowGlobals = new();
    private long _nextGlobal;
    private string? _lastRouteName;

    private LogBuffer Buffer => _workspaceService.Logs;

    [ObservableProperty]
    private BatchObservableCollection<string> _rows = [];

    [ObservableProperty]
    private LogLevelFilter _filter = LogLevelFilter.All;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _onlySelected;

    [ObservableProperty]
    private LogSource? _sourceFilter;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private int _shown;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private string _counter = "0 / 0";

    [ObservableProperty]
    private string? _selectedRouteName;

    [ObservableProperty]
    private bool _hasSelectedRoute;

    public bool ShowRouteFilter => HasSelectedRoute && SourceFilter is null or LogSource.ChannelProxy or LogSource.ChannelKeepAlive;

    [ObservableProperty]
    private string _onlySelectedLabel = string.Empty;

    [ObservableProperty]
    private string _emptyText = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _autoScrollToolTip = $"手动上滚后暂停跟随，连续 {ScrollResumeDelay.TotalSeconds:F0} 秒无操作自动恢复；关闭后保持手动浏览";

    /// <summary>有新行追加到列表尾部（用于跟随滚动）。</summary>
    public event Action? RowsAppended;

    public LogPageViewModel(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
        _workspaceService.LogsChanged += OnLogsChanged;
        _workspaceService.Refreshed += OnRefreshed;
        OnRefreshed();
        Rebuild();
    }

    public bool IsFilter(LogLevelFilter filter) => Filter == filter;

    private string? RouteMarker => OnlySelected && ShowRouteFilter ? SelectedRouteName : null;

    private string LowercaseQuery => Query.Trim().ToLowerInvariant();

    private void OnRefreshed()
    {
        var name = _workspaceService.Workspace.SelectedRouteRef()?.Name;
        SelectedRouteName = string.IsNullOrEmpty(name) ? null : name;
        HasSelectedRoute = SelectedRouteName is not null;
        OnlySelectedLabel = SelectedRouteName is null ? string.Empty : $"仅通道：{SelectedRouteName}";
        if (OnlySelected && _lastRouteName != SelectedRouteName)
        {
            Rebuild();
        }

        _lastRouteName = SelectedRouteName;
    }

    private void OnLogsChanged()
    {
        if (_nextGlobal < Buffer.Dropped || Buffer.Count == 0 && Rows.Count > 0)
        {
            Rebuild();
            return;
        }

        // 缓冲整批裁剪后，列表里落在被丢弃区间的行也要移除。
        var removed = 0;
        while (_rowGlobals.Count > removed && _rowGlobals[removed] < Buffer.Dropped)
        {
            removed++;
        }

        if (removed > 0)
        {
            _rowGlobals.RemoveRange(0, removed);
            Rows.RemoveFirst(removed);
        }

        var appendedRows = new List<string>();
        var appendedGlobals = new List<long>();
        for (var index = (int)(_nextGlobal - Buffer.Dropped); index < Buffer.Count; index++)
        {
            var line = Buffer[index];
            if (LogLine.Matches(line, Filter, LowercaseQuery, RouteMarker, SourceFilter))
            {
                appendedRows.Add(line);
                appendedGlobals.Add(Buffer.Dropped + index);
            }
        }

        if (appendedRows.Count > 0)
        {
            Rows.AddRange(appendedRows);
            _rowGlobals.AddRange(appendedGlobals);
        }

        _nextGlobal = Buffer.Dropped + Buffer.Count;
        UpdateCounters();
        if (appendedRows.Count > 0)
        {
            RowsAppended?.Invoke();
        }
    }

    private void Rebuild()
    {
        var rows = new List<string>();
        var globals = new List<long>();
        var query = LowercaseQuery;
        var route = RouteMarker;
        for (var index = 0; index < Buffer.Count; index++)
        {
            var line = Buffer[index];
            if (LogLine.Matches(line, Filter, query, route, SourceFilter))
            {
                rows.Add(line);
                globals.Add(Buffer.Dropped + index);
            }
        }

        Rows.ReplaceAll(rows);
        _rowGlobals.Clear();
        _rowGlobals.AddRange(globals);
        _nextGlobal = Buffer.Dropped + Buffer.Count;
        UpdateCounters();
        RowsAppended?.Invoke();
    }

    private void UpdateCounters()
    {
        Shown = Rows.Count;
        Total = Buffer.Count;
        Counter = $"{Shown} / {Total}";
        IsEmpty = Rows.Count == 0;
        EmptyText = Total == 0 ? "暂无日志，启用通道或开始一键准备后会在这里显示运行状态" : "没有匹配当前筛选条件的日志";
    }

    partial void OnFilterChanged(LogLevelFilter value)
    {
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(IsInfo));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsError));
        Rebuild();
    }

    partial void OnQueryChanged(string value) => Rebuild();

    partial void OnOnlySelectedChanged(bool value) => Rebuild();

    partial void OnHasSelectedRouteChanged(bool value) => OnPropertyChanged(nameof(ShowRouteFilter));

    partial void OnSourceFilterChanged(LogSource? value)
    {
        OnPropertyChanged(nameof(IsAllSources));
        OnPropertyChanged(nameof(IsChannelProxy));
        OnPropertyChanged(nameof(IsChannelKeepAlive));
        OnPropertyChanged(nameof(IsPreparation));
        OnPropertyChanged(nameof(IsSystem));
        OnPropertyChanged(nameof(ShowRouteFilter));
        if (!ShowRouteFilter)
        {
            OnlySelected = false;
        }

        Rebuild();
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

    [RelayCommand]
    private void OnSetFilter(string name)
    {
        Filter = name switch
        {
            "info" => LogLevelFilter.Info,
            "warning" => LogLevelFilter.Warning,
            "error" => LogLevelFilter.Error,
            _ => LogLevelFilter.All,
        };
    }

    [RelayCommand]
    private void OnToggleOnlySelected()
    {
        OnlySelected = !OnlySelected;
    }

    [RelayCommand]
    private void OnSetSource(string name)
    {
        SourceFilter = name switch
        {
            "proxy" => LogSource.ChannelProxy,
            "keepalive" => LogSource.ChannelKeepAlive,
            "preparation" => LogSource.Preparation,
            "system" => LogSource.System,
            _ => null,
        };
    }

    [RelayCommand]
    private void OnClear()
    {
        _workspaceService.ClearLogs();
    }

    [RelayCommand]
    private void OnOpenDirectory()
    {
        var directory = _workspaceService.Workspace.Logger.DirectoryPath;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (Exception)
        {
            _workspaceService.Workspace.Notice = $"日志目录：{directory}";
        }
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
