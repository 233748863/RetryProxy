using CommunityToolkit.Mvvm.ComponentModel;
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

/// <summary>最近 7 天里的一天；HasData 为 false 表示那天没有统计文件。</summary>
public sealed record TrendDay(string DateLabel, string RequestsText, string RateText, double BarHeight, Brush Brush, string ToolTip, bool IsToday, bool HasData);

/// <summary>按模型或按 Key 拆分的一行。</summary>
public sealed record UsageRow(string Name, string Requests, string Rate, Brush RateBrush, string Detail, string ToolTip);

/// <summary>统计页"概况"标签：今日瓦片、缓存摘要、最近 7 天趋势、按模型与按 Key 拆分。</summary>
public partial class OverviewPageViewModel : ViewModel
{
    private const int TrendDays = 7;
    private const double TrendBarMaxHeight = 44.0;

    private readonly WorkspaceService _workspaceService;
    private string? _trendRouteId;
    private DateTime _trendDate;
    private IReadOnlyList<DailySummary> _pastDays = [];

    private ProxyWorkspace Workspace => _workspaceService.Workspace;

    [ObservableProperty]
    private bool _hasChannel;

    [ObservableProperty]
    private string _emptyHint = string.Empty;

    [ObservableProperty]
    private string _totalRequests = "0";

    [ObservableProperty]
    private string _successfulRequests = "0";

    [ObservableProperty]
    private string _retryCount = "0";

    [ObservableProperty]
    private string _failedRequests = "0";

    [ObservableProperty]
    private string _activeRequests = "0";

    [ObservableProperty]
    private string _hitRateText = string.Empty;

    [ObservableProperty]
    private Brush _hitRateBrush = Brushes.Gray;

    [ObservableProperty]
    private double _hitRateProgress;

    [ObservableProperty]
    private string _hitRateHelp = string.Empty;

    [ObservableProperty]
    private string _creationText = string.Empty;

    [ObservableProperty]
    private string _creationHelp = string.Empty;

    [ObservableProperty]
    private string _latestText = string.Empty;

    [ObservableProperty]
    private Brush _latestBrush = Brushes.Gray;

    [ObservableProperty]
    private string _latestHelp = string.Empty;

    [ObservableProperty]
    private string _latestDetail = string.Empty;

    [ObservableProperty]
    private string _zeroHitText = string.Empty;

    [ObservableProperty]
    private Brush _zeroHitBrush = Brushes.Gray;

    [ObservableProperty]
    private string _zeroHitHelp = string.Empty;

    [ObservableProperty]
    private string _measuredText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<TrendDay> _trend = [];

    [ObservableProperty]
    private bool _trendIsEmpty = true;

    [ObservableProperty]
    private string _trendSummary = string.Empty;

    [ObservableProperty]
    private ObservableCollection<UsageRow> _modelRows = [];

    [ObservableProperty]
    private bool _hasModelRows;

    [ObservableProperty]
    private ObservableCollection<UsageRow> _keyRows = [];

    [ObservableProperty]
    private bool _hasKeyRows;

    public string RateHelp => CacheText.RateHelp;

    public string UsageHelp => CacheText.UsageHelp;

    public OverviewPageViewModel(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
        _workspaceService.Refreshed += Refresh;
        Refresh();
    }

    public override void OnNavigatedTo() => Refresh();

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

    private void Refresh()
    {
        var route = Workspace.SelectedRouteRef();
        HasChannel = route is not null;
        EmptyHint = ClientPageText.Translate("当前客户端尚无通道，请前往供应商页配置");
        if (route is null)
        {
            Clear();
            return;
        }

        var snapshot = Workspace.Services.TryGetValue(route.Id, out var service) ? service.Metrics.Snapshot() : new MetricsSnapshot();
        RenderTiles(snapshot);
        RenderCache(snapshot.Cache);
        RenderTrend(route.Id, snapshot);
        RenderBreakdowns(snapshot.Cache);
    }

    private void Clear()
    {
        TotalRequests = SuccessfulRequests = RetryCount = FailedRequests = ActiveRequests = "0";
        HitRateText = string.Empty;
        CreationText = LatestText = LatestDetail = ZeroHitText = MeasuredText = string.Empty;
        Trend = [];
        TrendIsEmpty = true;
        TrendSummary = string.Empty;
        ModelRows = [];
        HasModelRows = false;
        KeyRows = [];
        HasKeyRows = false;
        _trendRouteId = null;
    }

    private void RenderTiles(MetricsSnapshot snapshot)
    {
        TotalRequests = snapshot.TotalRequests.ToString();
        SuccessfulRequests = snapshot.SuccessfulRequests.ToString();
        RetryCount = snapshot.RetryCount.ToString();
        FailedRequests = snapshot.FailedRequests.ToString();
        ActiveRequests = snapshot.ActiveRequests.ToString();
    }

    private void RenderCache(CacheSnapshot cache)
    {
        var rate = cache.HitRatePercent();
        HitRateText = CacheText.RateText(rate);
        HitRateBrush = RateBrush(rate);
        HitRateProgress = rate ?? 0.0;
        HitRateHelp = $"已复用 {cache.CachedTokens} / 总输入 {cache.InputTokens} token\n{CacheText.RateHelp}";
        CreationText = ClientPageText.Translate("已记录写入 {0} token", CacheText.CreationTotalText(cache));
        CreationHelp = CacheText.CreationHelp(cache);
        var latest = cache.RecentRequests.Count > 0 ? cache.RecentRequests[^1] : null;
        LatestText = latest is null ? ClientPageText.Translate("等待请求") : CacheText.RequestRateText(latest);
        LatestBrush = RateBrush(latest?.HitRatePercent());
        LatestHelp = latest is null ? ClientPageText.Translate("等待本通道的成功请求") : CacheText.RequestHint(latest);
        LatestDetail = CacheText.LatestDetail(latest);
        ZeroHitText = ClientPageText.Translate("{0} 次", CacheText.CountText(cache.ZeroHitRequests));
        ZeroHitBrush = cache.ZeroHitRequests > 0 ? RateBrush(0.0) : ThemeBrush("TextFillColorPrimaryBrush");
        ZeroHitHelp = $"{cache.ZeroHitRequests} 次请求的缓存读取量为 0，共 {cache.ZeroHitInputTokens} token 输入。\n{CacheText.UsageHelp}";
        MeasuredText = ClientPageText.Translate(
            "有效 {0} 次 · 未计入 {1} 次",
            CacheText.CountText(cache.MeasuredRequests),
            CacheText.CountText(cache.UnmeasuredRequests));
    }

    /// <summary>历史日期不会再变，只在通道或日期变化时读一次磁盘；今天用内存快照，保证与其它标签一致。</summary>
    private void RenderTrend(string routeId, MetricsSnapshot snapshot)
    {
        var todayDate = DateTime.Today;
        if (_trendRouteId != routeId || _trendDate != todayDate)
        {
            _trendRouteId = routeId;
            _trendDate = todayDate;
            _pastDays = LoadPastDays(routeId, todayDate);
        }

        var days = new List<DailySummary>(_pastDays) { TodaySummary(snapshot) };
        var peak = days.Max(day => day.TotalRequests);
        var rows = new List<TrendDay>(days.Count);
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var rate = day.HitRatePercent();
            var height = peak == 0 ? 0.0 : Math.Max(TrendBarMaxHeight * day.TotalRequests / peak, day.TotalRequests > 0 ? 3.0 : 0.0);
            rows.Add(new TrendDay(
                day.Date.ToString("MM-dd"),
                day.TotalRequests.ToString(),
                day.HasData ? CacheText.RateText(rate) : ClientPageText.Translate("无记录"),
                height,
                day.HasData ? RateBrush(rate) : ThemeBrush("ControlFillColorDefaultBrush"),
                TrendToolTip(day),
                index == days.Count - 1,
                day.HasData));
        }

        Trend = new ObservableCollection<TrendDay>(rows);
        TrendIsEmpty = days.All(day => !day.HasData);
        TrendSummary = TrendIsEmpty
            ? string.Empty
            : ClientPageText.Translate(
                "最近 {0} 天共 {1} 次请求 · 命中 {2}",
                days.Count,
                CacheText.CountText(days.Aggregate(0UL, (sum, day) => SaturatingSum(sum, day.TotalRequests))),
                CacheText.RateText(CombinedRate(days)));
    }

    private IReadOnlyList<DailySummary> LoadPastDays(string routeId, DateTime today)
    {
        if (!Workspace.Services.TryGetValue(routeId, out var service) || service.Data is not { } data)
        {
            return [];
        }

        try
        {
            var first = DateOnly.FromDateTime(today).AddDays(-(TrendDays - 1));
            return DailyHistory.ReadRange(data, routeId, first, TrendDays - 1);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DailySummary TodaySummary(MetricsSnapshot snapshot)
    {
        return new DailySummary(
            DateOnly.FromDateTime(DateTime.Today),
            true,
            snapshot.TotalRequests,
            snapshot.SuccessfulRequests,
            snapshot.FailedRequests,
            snapshot.RetryCount,
            snapshot.Cache.InputTokens,
            snapshot.Cache.CachedTokens);
    }

    private static double? CombinedRate(IReadOnlyList<DailySummary> days)
    {
        ulong input = 0;
        ulong cached = 0;
        foreach (var day in days)
        {
            input = SaturatingSum(input, day.InputTokens);
            cached = SaturatingSum(cached, day.CachedTokens);
        }

        return input > 0 ? 100.0 * cached / input : null;
    }

    private static ulong SaturatingSum(ulong left, ulong right)
    {
        var sum = left + right;
        return sum < left ? ulong.MaxValue : sum;
    }

    private static string TrendToolTip(DailySummary day)
    {
        if (!day.HasData)
        {
            return ClientPageText.Translate("{0} 没有统计记录", day.Date.ToString("MM-dd"));
        }

        return ClientPageText.Translate(
            "{0}：请求 {1} · 成功 {2} · 失败 {3} · 重试 {4} · 命中 {5}",
            day.Date.ToString("MM-dd"),
            day.TotalRequests,
            day.SuccessfulRequests,
            day.FailedRequests,
            day.RetryCount,
            CacheText.RateText(day.HitRatePercent()));
    }

    private void RenderBreakdowns(CacheSnapshot cache)
    {
        var models = BuildRows(cache.Models, ClientPageText.Translate("未记录模型"));
        var keys = BuildRows(cache.Keys, ClientPageText.Translate("未记录 Key"));
        ModelRows = new ObservableCollection<UsageRow>(models);
        HasModelRows = models.Count > 0;
        KeyRows = new ObservableCollection<UsageRow>(keys);
        HasKeyRows = keys.Count > 0;
    }

    private static List<UsageRow> BuildRows(Dictionary<string, UsageBreakdown> buckets, string emptyName)
    {
        return buckets.Values
            .OrderByDescending(bucket => bucket.InputTokens)
            .ThenByDescending(bucket => bucket.Requests)
            .ThenBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase)
            .Select(bucket =>
            {
                var rate = bucket.HitRatePercent();
                var name = bucket.Name.Length > 0 ? bucket.Name : emptyName;
                var usage = ClientPageText.Translate(
                    "已复用 {0} / 总输入 {1} token · 写入 {2}",
                    CacheText.CountText(bucket.CachedTokens),
                    CacheText.CountText(bucket.InputTokens),
                    CacheText.CountText(bucket.CacheCreationTokens));
                var measured = ClientPageText.Translate("有效 {0} 次", CacheText.CountText(bucket.MeasuredRequests));
                return new UsageRow(
                    name,
                    ClientPageText.Translate("{0} 次", CacheText.CountText(bucket.Requests)),
                    CacheText.RateText(rate),
                    RateBrush(rate),
                    usage,
                    $"{name}\n{usage}\n{measured}\n{CacheText.UsageHelp}");
            })
            .ToList();
    }
}
