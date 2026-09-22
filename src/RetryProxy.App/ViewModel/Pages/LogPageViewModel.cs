using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private ObservableCollection<string> _rows = [];

    [ObservableProperty]
    private LogLevelFilter _filter = LogLevelFilter.All;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _onlySelected;

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

    private string? RouteMarker => OnlySelected ? SelectedRouteName : null;

    private string LowercaseQuery => Query.Trim().ToLowerInvariant();

    private void OnRefreshed()
    {
        var name = _workspaceService.Workspace.SelectedRouteRef()?.Name;
        SelectedRouteName = string.IsNullOrEmpty(name) ? null : name;
        HasSelectedRoute = SelectedRouteName is not null;
        OnlySelectedLabel = SelectedRouteName is null ? string.Empty : $"仅 {SelectedRouteName}";
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
        while (_rowGlobals.Count > 0 && _rowGlobals[0] < Buffer.Dropped)
        {
            _rowGlobals.RemoveAt(0);
            Rows.RemoveAt(0);
        }

        var appended = false;
        for (var index = (int)(_nextGlobal - Buffer.Dropped); index < Buffer.Count; index++)
        {
            var line = Buffer[index];
            if (LogLine.Matches(line, Filter, LowercaseQuery, RouteMarker))
            {
                Rows.Add(line);
                _rowGlobals.Add(Buffer.Dropped + index);
                appended = true;
            }
        }

        _nextGlobal = Buffer.Dropped + Buffer.Count;
        UpdateCounters();
        if (appended)
        {
            RowsAppended?.Invoke();
        }
    }

    private void Rebuild()
    {
        Rows.Clear();
        _rowGlobals.Clear();
        var query = LowercaseQuery;
        var route = RouteMarker;
        for (var index = 0; index < Buffer.Count; index++)
        {
            var line = Buffer[index];
            if (LogLine.Matches(line, Filter, query, route))
            {
                Rows.Add(line);
                _rowGlobals.Add(Buffer.Dropped + index);
            }
        }

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
        EmptyText = Total == 0 ? "暂无日志，启用通道后会在这里显示运行状态" : "没有匹配当前筛选条件的日志";
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
}
