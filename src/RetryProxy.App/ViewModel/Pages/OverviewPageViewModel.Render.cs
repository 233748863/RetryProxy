using CommunityToolkit.Mvvm.ComponentModel;
using RetryProxy.Core.Metrics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace RetryProxy.ViewModel.Pages;

public partial class OverviewPageViewModel
{
    [ObservableProperty] private Brush _accentBrush = StatisticsText.Color(0);

    private void RefreshLanguage()
    {
        var selection = (SelectedClient, SelectedProvider, SelectedModel, SelectedKey, SelectedStatus,
            SelectedMetric, SelectedDimension, SelectedHeatMetric);
        _syncing = true;
        try
        {
            ResetChoices();
            if (_report is not null) UpdateChoices(_report);
            (SelectedClient, SelectedProvider, SelectedModel, SelectedKey, SelectedStatus,
                SelectedMetric, SelectedDimension, SelectedHeatMetric) = selection;
            UpdatePriceChoices();
            UpdateEditingSource();
        }
        finally { _syncing = false; }
        _dirty = true;
        if (_visible) _ = ReloadAsync();
    }

    private void ResetChoices()
    {
        ClientChoices = [new("", StatisticsText.T("全部客户端"))];
        ProviderChoices = [new("", StatisticsText.T("全部供应商"))];
        ModelChoices = [new("", StatisticsText.T("全部模型"))];
        KeyChoices = [new("", StatisticsText.T("全部 Key"))];
        StatusChoices = [new("", StatisticsText.T("全部结果")), new("success", StatisticsText.T("成功")), new("failure", StatisticsText.T("失败"))];
        MetricChoices = [new("tokens", StatisticsText.T("用量")), new("cost", StatisticsText.T("费用")), new("requests", StatisticsText.T("请求")), new("speed", StatisticsText.T("速度"))];
        DimensionChoices = [new("model", StatisticsText.T("模型")), new("provider", StatisticsText.T("供应商")), new("client", StatisticsText.T("客户端")), new("key", "Key")];
        HeatMetricChoices = MetricChoices.Take(3).ToArray();
    }

    private void UpdateChoices(UsageReport report)
    {
        var wasSyncing = _syncing;
        _syncing = true;
        var client = SelectedClient ?? string.Empty;
        var provider = SelectedProvider ?? string.Empty;
        var model = SelectedModel ?? string.Empty;
        var key = SelectedKey ?? string.Empty;
        var clients = Choices(report.Clients, "全部客户端");
        var providers = Choices(report.Providers, "全部供应商");
        var models = Choices(report.Models, "全部模型");
        var keys = Choices(report.Keys, "全部 Key");
        if (!ClientChoices.SequenceEqual(clients)) ClientChoices = clients;
        if (!ProviderChoices.SequenceEqual(providers)) ProviderChoices = providers;
        if (!ModelChoices.SequenceEqual(models)) ModelChoices = models;
        if (!KeyChoices.SequenceEqual(keys)) KeyChoices = keys;
        SelectedClient = client; SelectedProvider = provider; SelectedModel = model; SelectedKey = key;
        _syncing = wasSyncing;
    }

    private static IReadOnlyList<StatisticsChoice> Choices(IEnumerable<UsageOption> source, string all) =>
        new[] { new StatisticsChoice(string.Empty, StatisticsText.T(all)) }
            .Concat(source.Select(o => new StatisticsChoice(o.Id, o.Id == UsageRecord.Unknown ? StatisticsText.T(o.Name) : o.Name))).ToArray();

    private void RenderTotals(UsageTotals t)
    {
        AccentBrush = StatisticsText.Color(0);
        TotalRequests = t.Requests.ToString(CultureInfo.CurrentUICulture);
        RequestsDetail = StatisticsText.T("成功 {0} · 失败 {1} · 重试 {2}", t.Successes, t.Failures, StatisticsText.Count(t.Retries));
        RequestsHelp = StatisticsText.T("成功率 {0}。请求以结束日期归属，重试单独计数；保活、模型列表查询与未结束请求不计入分析。", StatisticsText.Rate(t.SuccessRate));
        TotalTokens = t.UsageReported == 0 ? (t.Requests == 0 ? "0" : "—") : (t.IncompleteUsage > 0 ? "≥" : string.Empty) + StatisticsText.Count(t.Tokens);
        TokensDetail = t.IncompleteUsage > 0 ? StatisticsText.T("{0} 次用量不全 · 已知部分", t.IncompleteUsage) : StatisticsText.T("含缓存读取与写入");
        TokensHelp = StatisticsText.T("已记录 {0} Token", StatisticsText.Exact(t.Tokens)) + "\n" + StatisticsText.UsageHelp;
        CacheRate = StatisticsText.Rate(t.HitRate, t.CacheUpperBound > 0);
        CacheProgress = t.HitRate ?? 0;
        CacheDetail = StatisticsText.T("有效 {0} 次 · 未计入 {1} 次", t.CacheMeasured, t.Requests - t.CacheMeasured);
        CacheRateHelp = StatisticsText.T("已复用 {0} / 总输入 {1} Token", StatisticsText.Exact(t.MeasuredRead), StatisticsText.Exact(t.MeasuredPrompt)) + "\n" + StatisticsText.CacheHelp;
        TotalCost = t.Priced == 0 ? "—" : (t.PartiallyPriced > 0 || t.Unpriced > 0 ? "≥" : "≈") + StatisticsText.Money(t.Cost);
        CostDetail = StatisticsText.T("已计价 {0}/{1} 次 · USD", t.Priced, t.Requests);
        CostHelp = StatisticsText.T("已知费用 {0}；{1} 次缺单价或用量，未计入；{2} 次仅计已知部分。", StatisticsText.Amount(t.Cost), t.Unpriced, t.PartiallyPriced) + "\n" + StatisticsText.CostHelp;
        StatisticsTokenTile Part(string name, decimal value, long measured, string detail) => new(
            name, StatisticsText.T(name), measured == 0 ? "—" : (measured < t.Requests ? "≥" : string.Empty) + StatisticsText.Count(value),
            StatisticsText.T(detail), StatisticsText.T("{0} Token · {1}/{2} 次有记录", StatisticsText.Exact(value), measured, t.Requests));
        var tiles = new[]
        {
            Part("普通输入", t.Input, t.InputReported, "不含缓存"), Part("输出用量", t.Output, t.OutputReported, "包含思考"),
            Part("缓存读取", t.CacheRead, t.ReadReported, "已复用的输入"), Part("缓存写入", t.CacheWrite, t.WriteReported, "新写入的输入"),
            Part("思考用量", t.Reasoning, t.ReasoningReported, "已包含在输出中"),
            new StatisticsTokenTile("输出速度", StatisticsText.T("输出速度"), t.Speed is { } speed ? StatisticsText.T("{0} /秒", speed.ToString("0.#", CultureInfo.CurrentUICulture)) : "—",
                StatisticsText.T("首内容 {0}", StatisticsText.Seconds(t.FirstContent)),
                StatisticsText.T("{0} 次回复有速度记录", t.SpeedMeasured) + "\n" + StatisticsText.SpeedHelp),
        };
        UpdateRows(TokenTiles, tiles, tile => tile.Id, (row, next) => row.Update(next));
    }

    private static UsageMetric MetricOf(string value) => value switch
    {
        "cost" => UsageMetric.Cost, "requests" => UsageMetric.Requests, "speed" => UsageMetric.Speed, _ => UsageMetric.Tokens,
    };

    private void RenderChart(UsageReport report)
    {
        var metric = MetricOf(SelectedMetric);
        var sorted = report.Groups.OrderByDescending(g => g.Totals.Value(metric)).ThenByDescending(g => g.Totals.Tokens).ThenBy(g => g.Identity.Name, StringComparer.Ordinal).ToArray();
        var top = sorted.Take(7).Select((g, i) => new StatisticsCategory(g.Identity.Id, g.Identity.Name, StatisticsText.Color(i))).ToList();
        var rest = sorted.Skip(7).Select(g => g.Identity.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (rest.Count > 0 && metric != UsageMetric.Speed) top.Add(new StatisticsCategory("\u001fother", StatisticsText.T("其他"), StatisticsText.Color(7)));
        CollectionSync.Update(Categories, top);
        var points = new List<StatisticsChartPoint>(report.Series.Count);
        foreach (var bucket in report.Series)
        {
            var parts = new List<StatisticsChartPart>();
            foreach (var cat in top)
            {
                var value = cat.Id == "\u001fother" ? bucket.Parts.Where(p => rest.Contains(p.Key)).Sum(p => p.Value.Value(metric))
                    : bucket.Parts.TryGetValue(cat.Id, out var totals) ? totals.Value(metric) : 0;
                parts.Add(new StatisticsChartPart(value, cat.Brush));
            }
            var when = report.Bucket == "hour" ? bucket.Start.ToString("yyyy-MM-dd HH:00")
                : report.Bucket is "week" or "month" ? $"{bucket.Start:yyyy-MM-dd} – {bucket.LastDate:yyyy-MM-dd}" : bucket.Start.ToString("yyyy-MM-dd");
            var label = report.Bucket == "hour" ? bucket.Start.ToString("HH:00") : report.Bucket == "month" ? bucket.Start.ToString("yy-MM") : bucket.Start.ToString("MM-dd");
            var previous = Chart?.Points.ElementAtOrDefault(points.Count);
            // 列表默认按引用比较，先复用相同分段，才能识别未变化的整张图表。
            var stableParts = previous is not null && previous.Parts.SequenceEqual(parts) ? previous.Parts : parts;
            var point = new StatisticsChartPoint(DateOnly.FromDateTime(bucket.Start) < report.FirstDate ? report.FirstDate : DateOnly.FromDateTime(bucket.Start),
                bucket.LastDate, label, when + "\n" + TotalsHint(bucket.Totals), bucket.Totals.Value(metric), stableParts);
            points.Add(previous is not null && previous == point ? previous : point);
        }
        var empty = report.Totals.Requests == 0 ? "所选时段暂无已结束请求" : metric switch
        {
            UsageMetric.Cost => report.Totals.Priced > 0 ? "所选请求的已知估算费用为 $0.00" : "没有可计价的请求，请检查模型单价与用量",
            UsageMetric.Speed => "没有可测量的流式回复",
            _ => report.Totals.UsageReported > 0 ? "所选请求的已知用量为 0 Token" : "请求未报告用量",
        };
        var emptyText = StatisticsText.T(empty);
        if (Chart is not { } current || current.Metric != metric || current.EmptyText != emptyText || !current.Points.SequenceEqual(points))
            Chart = new StatisticsChartData(points, metric, emptyText);
        var scale = report.Bucket switch { "hour" => "按小时", "week" => "按周", "month" => "按月", _ => "按天" };
        var caption = StatisticsText.T(scale) + " · " + (metric == UsageMetric.Speed
            ? StatisticsText.T("柱高为总体速度，彩线为分组速度") : StatisticsText.T("悬停查看数值，点击柱子筛选时段"));
        if (report.UnplacedHours > 0) caption += StatisticsText.T(" · {0} 次未记录时间，计入总数", report.UnplacedHours);
        ChartCaption = caption;
        var maximum = sorted.Length == 0 ? 0 : sorted.Max(g => g.Totals.Value(metric));
        var denominator = metric == UsageMetric.Speed ? maximum : report.Totals.Value(metric);
        var ranks = sorted.Select(g =>
        {
            var color = top.FirstOrDefault(c => c.Id == g.Identity.Id)?.Brush ?? StatisticsText.Color(7);
            var val = g.Totals.Value(metric);
            var detail = metric == UsageMetric.Speed ? StatisticsText.T("{0} 次有效测速 · 首内容 {1}", g.Totals.SpeedMeasured, StatisticsText.Seconds(g.Totals.FirstContent))
                : StatisticsText.T("{0} 次 · 命中 {1}", g.Totals.Requests, StatisticsText.Rate(g.Totals.HitRate, g.Totals.CacheUpperBound > 0));
            var label = MetricLabel(metric, g.Totals);
            return new StatisticsRankRow(g.Identity.Id, g.Identity.Name, color, label, detail,
                g.Identity.Name + "\n" + TotalsHint(g.Totals), denominator > 0 ? 100 * val / denominator : 0);
        }).ToArray();
        UpdateRows(RankRows, ranks, row => row.Id, (row, next) => row.Update(next));
    }

    private static string TotalsHint(UsageTotals totals) => StatisticsText.T("请求 {0} · 成功 {1} · 失败 {2} · 重试 {3}",
        totals.Requests, totals.Successes, totals.Failures, StatisticsText.Exact(totals.Retries)) + "\n"
        + StatisticsText.T("已知用量 {0} Token · 用量不全 {1} 次", StatisticsText.Exact(totals.Tokens), totals.IncompleteUsage) + "\n"
        + StatisticsText.T("命中 {0} · 费用 {1} · 未计价 {2} 次", StatisticsText.Rate(totals.HitRate, totals.CacheUpperBound > 0),
            CostLabel(totals, exact: true), totals.Unpriced)
        + (totals.PartiallyPriced > 0 ? "\n" + StatisticsText.T("{0} 次费用仅计已知部分", totals.PartiallyPriced) : string.Empty);

    private static string CostLabel(UsageTotals totals, bool exact = false) => totals.Priced == 0 ? "—"
        : (totals.PartiallyPriced > 0 || totals.Unpriced > 0 ? "≥" : "≈")
          + (exact ? StatisticsText.Amount(totals.Cost) : StatisticsText.Money(totals.Cost));

    private static string MetricLabel(UsageMetric metric, UsageTotals totals) => metric switch
    {
        UsageMetric.Cost => CostLabel(totals),
        UsageMetric.Speed when totals.SpeedMeasured == 0 => "—",
        UsageMetric.Tokens when totals.UsageReported == 0 && totals.Requests > 0 => "—",
        UsageMetric.Tokens => (totals.IncompleteUsage > 0 ? "≥" : string.Empty) + StatisticsText.Count(totals.Tokens),
        _ => StatisticsText.Metric(metric, totals.Value(metric)),
    };

    private void RenderHeatmap(UsageReport report)
    {
        var metric = MetricOf(SelectedHeatMetric);
        var first = UsageReport.HeatmapFirst(report.Query.Today);
        var peak = report.Heatmap.Count == 0 ? 0 : report.Heatmap.Values.Max(t => t.Value(metric));
        var cells = new List<StatisticsHeatDay>(371);
        var empty = StatisticsText.ThemeBrush("ControlStrokeColorDefaultBrush");
        for (var weekday = 0; weekday < 7; weekday++)
        {
            for (var week = 0; week < 53; week++)
            {
                var date = first.AddDays(week * 7 + weekday);
                report.Heatmap.TryGetValue(date, out var totals);
                var value = totals?.Value(metric) ?? 0;
                Brush color = empty;
                if (value > 0 && peak > 0)
                {
                    color = StatisticsText.HeatColor((int)Math.Ceiling(4 * value / peak));
                }
                var hint = date.ToString("yyyy-MM-dd") + "\n" + (totals is null ? StatisticsText.T("没有已结束请求") : TotalsHint(totals));
                cells.Add(new StatisticsHeatDay(date.ToDateTime(TimeOnly.MinValue), date <= report.Query.Today, color, hint,
                    report.FirstDate == report.Query.LastDate && date == report.FirstDate));
            }
        }
        UpdateRows(HeatDays, cells, day => day.Date, (day, next) => day.Update(next));
        var months = new List<string>(53);
        var previous = -1;
        for (var week = 0; week < 53; week++)
        {
            var date = first.AddDays(week * 7);
            months.Add(date.Month != previous ? date.ToString("MMM", CultureInfo.CurrentUICulture) : string.Empty);
            previous = date.Month;
        }
        if (!HeatMonths.SequenceEqual(months)) HeatMonths = months;
        HeatCaption = StatisticsText.T("最近 53 周 · 活跃 {0} 天 · 点击查看当天", report.Heatmap.Count);
    }

    private void RenderRequestPage()
    {
        if (_report is not { } report) return;
        _page = Math.Clamp(_page, 0, Math.Max(0, (report.Records.Count - 1) / PageSize));
        var rows = report.Records.Skip(_page * PageSize).Take(PageSize).Select(r =>
        {
            var tokens = r.Tokens;
            var usage = tokens is { HasUsage: true } ? (tokens.Complete ? string.Empty : "≥") + StatisticsText.Count(tokens.KnownTokens) : "—";
            var cost = r.Quote.Amount is { } amount ? (r.Quote.IsLowerBound ? "≥" : "≈") + StatisticsText.Money(amount) : "—";
            var status = StatisticsText.T(r.Outcome == RequestOutcome.Success ? "成功" : "失败");
            var details = StatisticsText.T("普通输入 {0} · 输出 {1} · 读取 {2} · 写入 {3} · 思考 {4}",
                Part(tokens?.Input), Part(tokens?.Output), Part(tokens?.CacheRead), Part(tokens?.CacheWrite), Part(tokens?.Reasoning));
            var hint = $"{r.Date:yyyy-MM-dd} {r.CompletedAt:HH:mm:ss}\n{r.Group(UsageDimension.Model).Name}\n{r.Group(UsageDimension.Provider).Name} · {r.KeyName}\n{details}\n"
                + StatisticsText.T("首内容 {0} · 总耗时 {1} · 重试 {2}", StatisticsText.Seconds(r.Usage?.FirstContentSeconds), StatisticsText.Seconds(r.Usage?.DurationSeconds), r.Retries)
                + "\n" + (r.Quote.Amount is { } exact ? (r.Quote.IsLowerBound ? "≥" : "≈") + StatisticsText.Amount(exact) : StatisticsText.T("缺少单价或计费用量"));
            return new StatisticsRequestRow(r.RouteId, r.RequestId, r.Date.ToString("MM-dd") + "\n" + (r.CompletedAt?.ToString("HH:mm:ss") ?? "—"),
                r.Group(UsageDimension.Model).Name, r.Group(UsageDimension.Provider).Name,
                r.Group(UsageDimension.Client).Name + " · " + (r.KeyName.Length > 0 ? r.KeyName : StatisticsText.T("未记录 Key")),
                status, StatisticsText.ThemeBrush(r.Outcome == RequestOutcome.Success ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"),
                usage, StatisticsText.Rate(tokens?.HitRate, tokens?.PromptIsLowerBound == true), cost, StatisticsText.Seconds(r.Usage?.DurationSeconds), hint);
        }).ToArray();
        UpdateRows(RequestRows, rows, row => row.Identity, (row, next) => row.Update(next));
        var pages = Math.Max(1, (report.Records.Count + PageSize - 1) / PageSize);
        PageCaption = StatisticsText.T("第 {0}/{1} 页 · 共 {2} 条 · 每页 {3} 条", _page + 1, pages, report.Records.Count, PageSize);
        UpdateCommands();
    }

    private static void UpdateRows<T, TKey>(ObservableCollection<T> target, IReadOnlyList<T> incoming,
        Func<T, TKey> identity, Action<T, T> update) where TKey : notnull
    {
        var existing = target.ToDictionary(identity);
        var rows = incoming.Select(next =>
        {
            if (!existing.TryGetValue(identity(next), out var row)) return next;
            update(row, next);
            return row;
        }).ToArray();
        CollectionSync.Update(target, rows);
    }

    private static string Part(decimal? value) => value is { } number ? StatisticsText.Exact(number) : StatisticsText.T("未获取");
}
