using CommunityToolkit.Mvvm.ComponentModel;
using RetryProxy.Core.Diagnostics;
using RetryProxy.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RetryProxy.View.Drawers;

public partial class RequestDiagnosticDrawer : DrawerPage
{
    private const int PageSize = 100;
    private readonly IDiagnosticRepository _repository;
    private readonly DateOnly _date;
    private readonly string _requestId;
    private readonly DispatcherTimer _refreshTimer;
    private readonly ObservableCollection<RequestDiagnosticEventRow> _events = new();
    private DiagnosticSummary? _summary;
    private CancellationTokenSource? _queryCancellation;
    private string? _warning;
    private int _offset;
    private int _notificationQueued;
    private long _version;
    private bool _active;
    private bool _dirty;
    private bool _hasMore;

    public override string TitleKey => "请求详情";
    public override bool IsReadOnly => true;
    public override bool HasChanges => false;

    public RequestDiagnosticDrawer(IDiagnosticRepository repository, DiagnosticSummary summary)
    {
        _repository = repository;
        _summary = summary;
        _date = summary.Request.Date;
        _requestId = summary.Request.RequestId;
        InitializeComponent();
        EventsList.ItemsSource = _events;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += RefreshTick;
        Loaded += (_, _) => Activate();
        Unloaded += (_, _) => Deactivate();
        Render();
    }

    private void Activate()
    {
        if (_active || Lifetime.IsCancellationRequested) return;
        _active = true;
        _repository.Changed += RepositoryChanged;
        Schedule();
    }

    private void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _repository.Changed -= RepositoryChanged;
        _refreshTimer.Stop();
        _dirty = false;
        _version++;
        _queryCancellation?.Cancel();
    }

    public override void CancelPendingOperations()
    {
        Deactivate();
        base.CancelPendingOperations();
    }

    private void RepositoryChanged()
    {
        if (Interlocked.Exchange(ref _notificationQueued, 1) != 0 || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _notificationQueued, 0);
            if (_active) Schedule();
        });
    }

    private void Schedule()
    {
        if (!_active) return;
        _dirty = true;
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    private async void RefreshTick(object? sender, EventArgs args)
    {
        _refreshTimer.Stop();
        if (!_active || !_dirty || _queryCancellation is not null) return;
        _dirty = false;
        await ReadAsync();
    }

    private async Task ReadAsync()
    {
        var version = _version;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _queryCancellation = cancellation;
        IsBusy = true;
        RenderButtons();
        try
        {
            var detail = await _repository.ReadDetailAsync(_date, _requestId, _offset, PageSize, cancellation.Token);
            if (!_active || version != _version || cancellation.IsCancellationRequested) return;
            _summary = detail.Summary;
            _warning = detail.Warning ?? _repository.Warning;
            if (_summary is null) _warning ??= "该请求记录已过期或未找到";
            var oldRows = _events.ToDictionary(row => row.Event.Sequence);
            var rows = detail.Events.OrderBy(item => item.Sequence).Select(item =>
            {
                var row = oldRows.GetValueOrDefault(item.Sequence) ?? new RequestDiagnosticEventRow(item);
                row.Refresh(item);
                return row;
            }).ToList();
            CollectionSync.Update(_events, rows);
            _hasMore = detail.HasMore;
            Render();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_active && version == _version)
            {
                _warning = "读取诊断失败，请重新筛选或稍后重试";
                Render();
            }
        }
        finally
        {
            if (ReferenceEquals(_queryCancellation, cancellation)) _queryCancellation = null;
            IsBusy = false;
            if (_active)
            {
                RenderButtons();
                if (_dirty) _refreshTimer.Start();
            }
        }
    }

    protected override void OnLanguageChanged()
    {
        if (OverviewText is null) return;
        foreach (var row in _events) row.Refresh(row.Event);
        CopyNotice.Visibility = Visibility.Collapsed;
        Render();
    }

    private void Render()
    {
        OverviewText.Text = _summary is null ? DrawerText.T("该请求记录已过期或未找到")
            : RequestDiagnosticText.Overview(_summary, DrawerText.T);
        WarningText.Text = _warning is null ? string.Empty : DrawerText.T(_warning);
        WarningText.Visibility = string.IsNullOrEmpty(_warning) ? Visibility.Collapsed : Visibility.Visible;
        CopySummaryButton.IsEnabled = _summary is not null;
        RenderButtons();
    }

    private void RenderButtons()
    {
        PreviousButton.IsEnabled = !IsBusy && _offset > 0;
        NextButton.IsEnabled = !IsBusy && _hasMore;
        LoadingText.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = !IsBusy && _events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = string.Format(CultureInfo.CurrentCulture,
            DrawerText.T("事件第 {0} 页 · 本页 {1} 条 · 每页 100 条"), _offset / PageSize + 1, _events.Count);
    }

    private void PreviousClicked(object sender, RoutedEventArgs args)
    {
        if (IsBusy || _offset == 0) return;
        _offset = Math.Max(0, _offset - PageSize);
        _version++;
        Schedule();
    }

    private void NextClicked(object sender, RoutedEventArgs args)
    {
        if (IsBusy || !_hasMore) return;
        _offset += PageSize;
        _version++;
        Schedule();
    }

    private void CopyIdClicked(object sender, RoutedEventArgs args) => Copy(DiagnosticSafety.Identifier(_requestId));
    private void CopySummaryClicked(object sender, RoutedEventArgs args)
    {
        if (_summary is not null) Copy(RequestDiagnosticText.SafeSummary(_summary, DrawerText.T));
    }

    private void Copy(string? text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) { SetError("没有可复制的诊断内容"); return; }
            Clipboard.SetText(text);
            SetError(null);
            CopyNotice.Text = DrawerText.T("已复制");
            CopyNotice.Visibility = Visibility.Visible;
        }
        catch (Exception) { SetError("复制失败，请稍后重试"); }
    }
}

public partial class RequestDiagnosticEventRow : ObservableObject
{
    public RequestDiagnosticEventRow(DiagnosticEvent item) { Event = item; Refresh(item); }
    public DiagnosticEvent Event { get; private set; }
    [ObservableProperty] private string _heading = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    public override string ToString() => $"{Heading} {Body}";
    public void Refresh(DiagnosticEvent item)
    {
        Event = item;
        Heading = RequestDiagnosticText.EventHeading(item, DrawerText.T);
        Body = RequestDiagnosticText.EventBody(item.Entry, DrawerText.T);
    }
}
