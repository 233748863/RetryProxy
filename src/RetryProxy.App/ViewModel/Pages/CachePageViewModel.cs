using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace RetryProxy.ViewModel.Pages;

/// <summary>走势图一个槽位；Request 为 null 表示空槽。</summary>
public sealed record TrendSlot(CacheRequest? Request, double? Rate, Brush Brush, string? ToolTip)
{
    public bool HasRequest => Request is not null;

    public bool IsMeasured => Rate is not null;

    public bool IsUnmeasured => Request is not null && Rate is null;

    public const double Height = 56.0;

    /// <summary>按 56px 高度换算的柱高，有命中率时最小 3px（与 Rust 一致）。</summary>
    public double BarHeight => Rate.HasValue ? Math.Max(Height * Rate.Value / 100.0, 3.0) : 0.0;
}

/// <summary>缓存明细表一行。</summary>
public sealed record CacheRow(
    string RequestId,
    string Time,
    string Model,
    string Rate,
    Brush RateBrush,
    string Usage,
    string UsageHelp,
    string Creation,
    string Hint);

public partial class CachePageViewModel : ViewModel
{
    private readonly WorkspaceService _workspaceService;
    private CacheSnapshot? _lastSnapshot;
    private CacheFilter _filter = CacheFilter.All;
    private bool _syncing;

    private ProxyWorkspace Workspace => _workspaceService.Workspace;

    [ObservableProperty]
    private ObservableCollection<PickerItem> _channels = [];

    [ObservableProperty]
    private PickerItem? _selectedChannel;

    [ObservableProperty]
    private bool _hasChannel;

    [ObservableProperty]
    private string _title = "缓存明细";

    [ObservableProperty]
    private string _dateHeadline = string.Empty;

    [ObservableProperty]
    private string _hitRateText = string.Empty;

    [ObservableProperty]
    private Brush _hitRateBrush = Brushes.Gray;

    [ObservableProperty]
    private string _tokensText = string.Empty;

    [ObservableProperty]
    private string _tokensHelp = string.Empty;

    [ObservableProperty]
    private string _creationText = string.Empty;

    [ObservableProperty]
    private string _creationHelp = string.Empty;

    [ObservableProperty]
    private string _recentSummary = string.Empty;

    [ObservableProperty]
    private ObservableCollection<TrendSlot> _trend = [];

    [ObservableProperty]
    private bool _trendIsEmpty = true;

    [ObservableProperty]
    private string _legendCounts = string.Empty;

    [ObservableProperty]
    private Brush _hitBrush = Brushes.Gray;

    [ObservableProperty]
    private Brush _zeroHitBrush = Brushes.Gray;

    [ObservableProperty]
    private bool _isAll = true;

    [ObservableProperty]
    private bool _isZeroHit;

    [ObservableProperty]
    private bool _isUnmeasured;

    [ObservableProperty]
    private ObservableCollection<CacheRow> _rows = [];

    [ObservableProperty]
    private bool _hasRows;

    [ObservableProperty]
    private string _emptyRowsText = string.Empty;

    [ObservableProperty]
    private string? _cacheKeyHelp;

    [ObservableProperty]
    private bool _hasCacheKeyHelp;

    public string RateHelp => CacheText.RateHelp;

    public string UsageHelp => CacheText.UsageHelp;

    public string ClaudeHelp => CacheText.ClaudeHelp;

    public CachePageViewModel(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
        _workspaceService.Refreshed += Refresh;
        Refresh();
    }

    public override void OnNavigatedTo()
    {
        // Rust 的 open() 每次打开都把筛选复位为“全部”。
        _filter = CacheFilter.All;
        Refresh(force: true);
    }

    private static Brush ThemeBrush(string key)
    {
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    private static Brush RateBrush(double? rate) => rate switch
    {
        > 0.0 => ThemeBrush("SystemFillColorSuccessBrush"),
        { } => ThemeBrush("SystemFillColorCautionBrush"),
        null => ThemeBrush("TextFillColorSecondaryBrush"),
    };

    private void Refresh() => Refresh(force: false);

    private void Refresh(bool force)
    {
        _syncing = true;
        try
        {
            var channels = Workspace.Config.Routes
                .Select(route => new PickerItem(route.Id, $"{route.Name} · {route.ListenPort} · {UiText.StateLabel(Workspace.RouteState(route.Id))}"))
                .ToList();
            if (Channels.Count != channels.Count || !Channels.Zip(channels).All(pair => pair.First == pair.Second))
            {
                Channels.Clear();
                foreach (var item in channels)
                {
                    Channels.Add(item);
                }
            }

            SelectedChannel = Channels.FirstOrDefault(item => item.Key == Workspace.SelectedRoute);
            var route = Workspace.SelectedRouteRef();
            HasChannel = route is not null;
            if (route is null)
            {
                Title = "缓存明细";
                _lastSnapshot = null;
                Trend.Clear();
                Rows.Clear();
                HasRows = false;
                TrendIsEmpty = true;
                return;
            }

            Title = $"缓存明细 · {route.Name}";
            var cache = Workspace.Services.TryGetValue(route.Id, out var service) ? service.Metrics.Snapshot().Cache : new CacheSnapshot();
            if (!force && _lastSnapshot is { } previous && SameCache(previous, cache))
            {
                return;
            }

            _lastSnapshot = cache;
            Render(cache);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static bool SameCache(CacheSnapshot a, CacheSnapshot b)
    {
        return a.MeasuredRequests == b.MeasuredRequests
            && a.UnmeasuredRequests == b.UnmeasuredRequests
            && a.InputTokens == b.InputTokens
            && a.CachedTokens == b.CachedTokens
            && a.CacheCreationTokens == b.CacheCreationTokens
            && a.CacheCreationMeasuredRequests == b.CacheCreationMeasuredRequests
            && a.ZeroHitRequests == b.ZeroHitRequests
            && a.ZeroHitInputTokens == b.ZeroHitInputTokens
            && a.ClientKeyRequests == b.ClientKeyRequests
            && a.AddedKeyRequests == b.AddedKeyRequests
            && a.MissingSessionRequests == b.MissingSessionRequests
            && a.UnsupportedKeyRequests == b.UnsupportedKeyRequests
            && a.CompatibilityFallbacks == b.CompatibilityFallbacks
            && a.RecentRequests.SequenceEqual(b.RecentRequests);
    }

    private void Render(CacheSnapshot cache)
    {
        DateHeadline = CacheText.DateHeadline(DateTime.Now);
        var rate = cache.HitRatePercent();
        HitRateText = CacheText.RateText(rate);
        HitRateBrush = RateBrush(rate);
        TokensText = CacheText.TokensText(cache);
        TokensHelp = CacheText.TokensHelp(cache);
        CreationText = $"· 已记录写入 {CacheText.CreationTotalText(cache)} token";
        CreationHelp = CacheText.CreationHelp(cache);
        RecentSummary = CacheText.RecentSummary(cache);
        LegendCounts = CacheText.LegendCounts(cache);
        HitBrush = RateBrush(100.0);
        ZeroHitBrush = RateBrush(0.0);

        var slots = new List<TrendSlot>(CacheSnapshot.HistoryLimit);
        for (var index = 0; index < CacheSnapshot.HistoryLimit; index++)
        {
            var request = index < cache.RecentRequests.Count ? cache.RecentRequests[index] : null;
            var slotRate = request?.HitRatePercent();
            slots.Add(new TrendSlot(request, slotRate, request is null ? Brushes.Transparent : RateBrush(slotRate), request is null ? null : CacheText.RequestHint(request)));
        }

        Trend = new ObservableCollection<TrendSlot>(slots);
        TrendIsEmpty = cache.RecentRequests.Count == 0;

        IsAll = _filter == CacheFilter.All;
        IsZeroHit = _filter == CacheFilter.ZeroHit;
        IsUnmeasured = _filter == CacheFilter.Unmeasured;

        var rows = new List<CacheRow>();
        for (var index = cache.RecentRequests.Count - 1; index >= 0; index--)
        {
            var request = cache.RecentRequests[index];
            if (!_filter.Accepts(request))
            {
                continue;
            }

            var usage = CacheText.UsageCell(request);
            rows.Add(new CacheRow(
                request.RequestId,
                CacheText.RequestTime(request),
                request.Model,
                CacheText.RequestRateText(request),
                RateBrush(request.HitRatePercent()),
                usage,
                $"{usage} token\n{CacheText.UsageHelp}",
                CacheText.OptionalCount(request.CacheCreationTokens),
                CacheText.RequestHint(request)));
        }

        Rows = new ObservableCollection<CacheRow>(rows);
        HasRows = rows.Count > 0;
        EmptyRowsText = CacheText.EmptyRowsText(cache);
        CacheKeyHelp = CacheText.CacheKeyHelp(cache);
        HasCacheKeyHelp = CacheKeyHelp is not null;
    }

    partial void OnSelectedChannelChanged(PickerItem? value)
    {
        if (_syncing || value is null || value.Key == Workspace.SelectedRoute)
        {
            return;
        }

        Workspace.SelectRouteAcrossProviders(value.Key);
        _workspaceService.Flush();
    }

    [RelayCommand]
    private void OnSetFilter(string key)
    {
        _filter = CacheFilterExtensions.Parse(key);
        Refresh(force: true);
    }

    [RelayCommand]
    private void OnCopyRequestId(string requestId)
    {
        if (requestId.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(requestId);
        }
        catch (Exception)
        {
            Workspace.Notice = $"请求编号：{requestId}";
        }
    }
}
