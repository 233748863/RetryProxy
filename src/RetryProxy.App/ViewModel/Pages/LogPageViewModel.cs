using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace RetryProxy.ViewModel.Pages;

public partial class LogPageViewModel : ViewModel
{
    /// <summary>手动上滚后自动恢复跟随的空闲时长。</summary>
    public static readonly TimeSpan ScrollResumeDelay = TimeSpan.FromSeconds(5);

    private readonly WorkspaceService _workspaceService;
    private readonly List<long> _rowGlobals = new();
    private long _nextGlobal;
    private string? _lastRouteName;
    private ClientType? _lastClient;

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

    public bool ShowClientFilter => SourceFilter != LogSource.System;

    [ObservableProperty]
    private string _onlySelectedLabel = string.Empty;

    [ObservableProperty]
    private string _emptyText = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _autoScrollToolTip = string.Empty;

    /// <summary>有新行追加到列表尾部（用于跟随滚动）。</summary>
    public event Action? RowsAppended;

    public LogPageViewModel(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
        _workspaceService.LogsChanged += OnLogsChanged;
        _workspaceService.Refreshed += OnRefreshed;
        I18nService.Instance.PropertyChanged += (_, _) =>
        {
            OnRefreshed();
            UpdateCounters();
        };
        OnRefreshed();
        Rebuild();
    }

    public bool IsFilter(LogLevelFilter filter) => Filter == filter;

    /// <summary>跳转到某项准备的完整日志；保留可见筛选项，用户仍可修改来源、级别和搜索词。</summary>
    public void OpenPreparation(int taskNumber)
    {
        OnlySelected = false;
        Filter = LogLevelFilter.All;
        SourceFilter = LogSource.Preparation;
        // 日志标签是稳定的中文协议文本，不能翻译；末尾分隔符避免“准备 1”匹配“准备 10”。
        Query = "[准备 " + taskNumber.ToString(CultureInfo.InvariantCulture) + " · ";
    }

    private string LowercaseQuery => Query.Trim().ToLowerInvariant();

    private void OnRefreshed()
    {
        var workspace = _workspaceService.Workspace;
        var name = workspace.SelectedRouteRef()?.Name;
        SelectedRouteName = string.IsNullOrEmpty(name) ? null : name;
        OnlySelectedLabel = ClientPageText.Translate("仅当前客户端：{0}", workspace.SelectedClient.Label());
        AutoScrollToolTip = ClientPageText.Translate("手动上滚后暂停跟随，连续 {0:F0} 秒无操作自动恢复；关闭后保持手动浏览", ScrollResumeDelay.TotalSeconds);
        if (OnlySelected && (_lastRouteName != SelectedRouteName || _lastClient != workspace.SelectedClient))
        {
            Rebuild();
        }

        _lastRouteName = SelectedRouteName;
        _lastClient = workspace.SelectedClient;
    }

    private bool Matches(string line, string lowercaseQuery)
    {
        if (!LogLine.Matches(line, Filter, lowercaseQuery, null, SourceFilter)) return false;
        if (!OnlySelected || !ShowClientFilter) return true;

        var parts = LogLine.Split(line);
        if (parts.Source is LogSource.ChannelProxy or LogSource.ChannelKeepAlive)
        {
            return string.Equals(parts.RouteName, SelectedRouteName, StringComparison.Ordinal);
        }

        if (parts.Source == LogSource.Preparation)
        {
            // 独立准备使用“准备 N · 客户端”标签，不属于通道；同一个客户端筛选也应覆盖它。
            var suffix = " · " + _workspaceService.Workspace.SelectedClient.Label();
            return parts.Tags.Any(tag => tag.StartsWith("准备 ", StringComparison.Ordinal)
                && tag.EndsWith(suffix, StringComparison.Ordinal));
        }

        return false;
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
            if (Matches(line, LowercaseQuery))
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
        for (var index = 0; index < Buffer.Count; index++)
        {
            var line = Buffer[index];
            if (Matches(line, query))
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
        EmptyText = ClientPageText.Translate(Total == 0
            ? "暂无日志，代理运行或开始一键准备后会在这里显示运行状态"
            : "没有匹配当前筛选条件的日志");
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

    partial void OnSourceFilterChanged(LogSource? value)
    {
        OnPropertyChanged(nameof(IsAllSources));
        OnPropertyChanged(nameof(IsChannelProxy));
        OnPropertyChanged(nameof(IsChannelKeepAlive));
        OnPropertyChanged(nameof(IsPreparation));
        OnPropertyChanged(nameof(IsSystem));
        OnPropertyChanged(nameof(ShowClientFilter));
        if (!ShowClientFilter)
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
