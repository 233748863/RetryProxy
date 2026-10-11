using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Core.Diagnostics;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RetryProxy.ViewModel.Pages;

public partial class LogPageViewModel
{
    private IReadOnlyList<DiagnosticSummary>? _requestSnapshot;
    [ObservableProperty] private string _requestFilter = "all";
    public bool IsAllRequests => RequestFilter == "all";
    public bool IsFailedRequests => RequestFilter == "failed";
    public bool IsRetriedRequests => RequestFilter == "retried";
    public bool IsPendingRequests => RequestFilter == "pending";

    private ModelPricing CurrentPrices()
    {
        var config = _configService.Get().OtherConfig;
        return new(config.StatisticsPublicPrices, config.StatisticsModelPrices);
    }

    private async Task ReloadRequestsAsync(int version, CancellationToken token)
    {
        var date = DateOnly.FromDateTime(SelectedDate ?? LatestDate);
        var today = _workspaceService.Diagnostics.Today;
        var client = OnlySelected ? _workspaceService.Workspace.SelectedClient : (ClientType?)null;
        var search = Query.Trim();
        var filter = RequestFilter;
        var snapshot = _requestSnapshot;
        var replace = snapshot is null || AutoScroll && _pageIndex == 0;
        var offset = _pageIndex * RequestPageSize;
        var page = await _workspaceService.Diagnostics.ReadSummariesAsync(date, token);
        if (!Current(version, token)) return;
        var prepared = await Task.Run(() =>
        {
            var latest = page.Items.Where(summary => Matches(summary, client, search, filter))
                .OrderByDescending(summary => summary.Request.StartedAt)
                .ThenBy(summary => summary.Request.RequestId, StringComparer.Ordinal)
                .ThenBy(summary => summary.Request.Client).ToArray();
            // 手动阅读期间固定成员与排序，只更新这些请求已发生的变化。
            var all = page.Items.ToDictionary(RequestLogRow.IdentityOf);
            var kept = replace ? latest : snapshot!.Select(summary => all.GetValueOrDefault(RequestLogRow.IdentityOf(summary)) ?? summary).ToArray();
            var oldIds = (snapshot ?? []).Select(RequestLogRow.IdentityOf).ToHashSet();
            var pending = replace ? 0 : latest.Count(summary => !oldIds.Contains(RequestLogRow.IdentityOf(summary)));
            var visible = kept.Skip(offset).Take(RequestPageSize).ToArray();
            var usage = RequestLogUsageReader.Read(_workspaceService.Data, date, today,
                visible.Select(summary => summary.Request.RequestId).Distinct().ToArray(), token);
            return (Kept: kept, Pending: pending, Visible: visible, Usage: usage);
        }, token);
        if (!Current(version, token)) return;
        var previous = RequestRows.ToDictionary(row => row.Identity);
        var prices = CurrentPrices();
        ApplyRows(() =>
        {
            var rows = prepared.Visible.Select(summary =>
            {
                var row = previous.GetValueOrDefault(RequestLogRow.IdentityOf(summary)) ?? new RequestLogRow(summary);
                row.Update(summary, FindUsage(summary, prepared.Usage.Records), prices);
                return row;
            }).ToArray();
            CollectionSync.Update(RequestRows, rows);
        });
        _requestSnapshot = prepared.Kept;
        Total = prepared.Kept.Length;
        PendingCount = prepared.Pending;
        Warning = string.Join(" · ", new[] { T(page.Warning ?? string.Empty) }.Concat(prepared.Usage.Warnings).Where(text => !string.IsNullOrEmpty(text)));
    }

    internal static bool Matches(DiagnosticSummary summary, ClientType? client, string search, string filter)
    {
        if (client is not null && summary.Request.Client != client) return false;
        if (filter == "failed" && summary.Outcome != DiagnosticOutcome.Failure && summary.Delivery != DiagnosticDelivery.Interrupted) return false;
        if (filter == "retried" && summary.RetryCount == 0) return false;
        if (filter == "pending" && summary.Outcome != DiagnosticOutcome.Pending) return false;
        return search.Length == 0 || new[] { summary.Request.RequestId, summary.Request.Endpoint, summary.Request.Method,
            summary.Request.Client.Label(), summary.LastTarget?.ProviderName, summary.LastTarget?.KeyName, summary.LastTarget?.Model }
            .Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    internal static DailyRequest? FindUsage(DiagnosticSummary summary, IReadOnlyList<StoredUsageRecord> records)
    {
        var matches = records.Where(record => record.Request.RequestId == summary.Request.RequestId &&
            (record.Request.Attribution?.Client is null || record.Request.Attribution.Client == summary.Request.Client)).ToArray();
        // 同一完整编号如果存在多个通道归属，保持缺失，不能猜测或合并用量。
        return matches.Length == 1 ? matches[0].Request : null;
    }

    partial void OnRequestFilterChanged(string value)
    {
        foreach (var name in new[] { nameof(IsAllRequests), nameof(IsFailedRequests), nameof(IsRetriedRequests), nameof(IsPendingRequests) }) OnPropertyChanged(name);
        Schedule(resetPage: true);
    }
    [RelayCommand] private void OnSetRequestFilter(string name) => RequestFilter = name;
    [RelayCommand] private void OnToggleRequest(RequestLogRow? row)
    {
        if (row is null || !CanUseRows) return;
        SelectedRequest = row;
        row.IsExpanded = !row.IsExpanded;
    }
    [RelayCommand] private void OnCopyRequestRow(RequestLogRow? row)
    {
        if (row is not null && CanUseRows) Copy(row.RequestId, "请求编号已复制");
    }
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task OnOpenRequestDiagnostic(RequestLogRow? row)
    {
        if (row is null || !CanUseRows) return;
        PauseFollowing();
        if (App.GetService<DrawerService>() is { } drawers) await drawers.ShowRequestDiagnosticAsync(row.Summary);
    }
    [RelayCommand] private void OnOpenRequestEvents(RequestLogRow? row)
    {
        if (row is null || !CanUseRows) return;
        var request = row.Summary.Request;
        IsRequestView = false;
        OnlySelected = false;
        Filter = LogLevelFilter.All;
        SourceFilter = LogSource.ChannelProxy;
        SelectedDate = request.Date.ToDateTime(TimeOnly.MinValue);
        Query = request.RequestId;
        AutoScroll = false;
        Schedule(resetPage: true);
    }

    internal Task ExportRequestsAsync(TextWriter writer, IReadOnlyList<DiagnosticSummary> summaries, DateOnly date, ModelPricing prices)
    {
        var today = _workspaceService.Diagnostics.Today;
        return Task.Run(() =>
        {
            WriteCsvRow(writer, ["开始时间", "客户端", "出站模型", "供应商", "Key 名称", "结果", "HTTP 状态", "客户端交付", "重试次数",
                "普通输入", "缓存读取", "缓存写入", "输出", "思考（包含在输出中）", "用量完整", "估算费用(USD)", "费用为下限", "首内容等待(秒)", "总耗时(秒)", "请求编号"]);
            foreach (var batch in summaries.Chunk(RequestPageSize))
            {
                var usage = RequestLogUsageReader.Read(_workspaceService.Data, date, today, batch.Select(summary => summary.Request.RequestId).Distinct().ToArray());
                if (usage.Warnings.Count > 0) throw new IOException(string.Join(" · ", usage.Warnings));
                foreach (var summary in batch)
                {
                    var row = new RequestLogRow(summary);
                    row.Update(summary, FindUsage(summary, usage.Records), prices);
                    var tokens = row.Usage?.Normalize();
                    WriteCsvRow(writer, [summary.Request.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                        summary.Request.Client.Label(), summary.LastTarget?.Model ?? string.Empty, summary.LastTarget?.ProviderName ?? string.Empty,
                        summary.LastTarget?.KeyName ?? string.Empty, RequestDiagnosticText.Outcome(summary.Outcome), summary.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        RequestDiagnosticText.Delivery(summary.Delivery), summary.RetryCount.ToString(CultureInfo.InvariantCulture),
                        Number(tokens?.Input), Number(tokens?.CacheRead), Number(tokens?.CacheWrite), Number(tokens?.Output), Number(tokens?.Reasoning), tokens?.Complete == true ? "是" : "否",
                        Number(row.Quote.Amount), row.Quote.IsLowerBound ? "是" : "否", Seconds(summary.FirstContentSeconds), Seconds(summary.TotalSeconds), summary.Request.RequestId]);
                }
            }
        });
    }

    private static string Number(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Seconds(double? value) => value is { } n && double.IsFinite(n) && n >= 0 ? n.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty;
    private static void WriteCsvRow(TextWriter writer, IEnumerable<string> values) => writer.WriteLine(string.Join(",", values.Select(value =>
    {
        var start = value.AsSpan().TrimStart();
        if (start.Length > 0 && start[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    })));
}
