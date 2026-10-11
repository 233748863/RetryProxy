using CommunityToolkit.Mvvm.ComponentModel;
using RetryProxy.Core.Config;
using RetryProxy.Core.Diagnostics;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Workspace;
using RetryProxy.Service.I18n;
using System;
using System.Globalization;
using System.Linq;

namespace RetryProxy.ViewModel.Pages;

public sealed partial class RequestLogRow : ObservableObject
{
    public RequestLogRow(DiagnosticSummary summary) => Summary = summary;
    public DiagnosticSummary Summary { get; private set; }
    public RequestUsage? Usage { get; private set; }
    public PriceQuote Quote { get; private set; } = new(null, false, string.Empty);
    public static (DateOnly Date, ClientType Client, string Id, DateTimeOffset Started) IdentityOf(DiagnosticSummary summary)
        => (summary.Request.Date, summary.Request.Client, summary.Request.RequestId, summary.Request.StartedAt);
    public (DateOnly Date, ClientType Client, string Id, DateTimeOffset Started) Identity => IdentityOf(Summary);
    public string RequestId => Summary.Request.RequestId;
    public string Client => Summary.Request.Client == ClientType.Claude ? "Claude" : "Codex";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _time = string.Empty;
    [ObservableProperty] private string _automationText = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private string _provider = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _statusDetail = string.Empty;
    [ObservableProperty] private DiagnosticOutcome _tone;
    [ObservableProperty] private string _tokens = "—";
    [ObservableProperty] private string _cost = "—";
    [ObservableProperty] private string _duration = "—";
    [ObservableProperty] private string _firstContent = "—";
    [ObservableProperty] private string _usageDetail = string.Empty;
    [ObservableProperty] private string _timingDetail = string.Empty;
    [ObservableProperty] private string _requestDetail = string.Empty;
    [ObservableProperty] private string _modelDetail = string.Empty;
    [ObservableProperty] private string _failureText = string.Empty;
    [ObservableProperty] private string _costHelp = string.Empty;
    [ObservableProperty] private bool _hasFailure;

    public void Update(DiagnosticSummary summary, DailyRequest? record, ModelPricing prices)
    {
        Summary = summary;
        var translate = I18nService.Instance.Translate;
        string T(string text, params object[] args) => ClientPageText.Translate(text, args);
        string Value(string? text) => RequestDiagnosticText.Value(text, translate);
        string Exact(decimal? value) => value is { } n ? StatisticsText.Exact(n) : T("未记录");
        Usage = record?.Usage ?? RequestUsage.FromLegacy(record?.Cache);
        var normalized = Usage?.Normalize();
        var model = summary.LastTarget?.Model ?? string.Empty;
        var priceModel = !string.IsNullOrEmpty(Usage?.Model) ? Usage.Model : model;
        Quote = prices.Quote(record?.Attribution?.ProviderId ?? summary.LastTarget?.ProviderId ?? string.Empty, priceModel, Usage);
        Time = summary.Request.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Model = Value(model);
        Provider = RequestDiagnosticText.Target(summary.LastTarget, translate);
        Tone = summary.Delivery == DiagnosticDelivery.Interrupted ? DiagnosticOutcome.Failure : summary.Outcome;
        Status = summary.Delivery == DiagnosticDelivery.Interrupted ? T("交付中断")
            : summary.Outcome == DiagnosticOutcome.Unknown ? T("未记录") : RequestDiagnosticText.Outcome(summary.Outcome, translate);
        var http = summary.StatusCode is >= 100 and <= 599 ? summary.StatusCode.Value.ToString(CultureInfo.InvariantCulture) : "—";
        StatusDetail = summary.Incomplete ? T("记录不完整") : summary.RetryCount > 0 ? T("{0} · 重试 {1}", http, summary.RetryCount) : "HTTP " + http;
        Tokens = normalized?.HasUsage == true
            ? "↑ " + (!normalized.Invalid && normalized.Prompt is { } input ? (normalized.PromptIsLowerBound ? "≥" : "") + StatisticsText.Count(input) : "—")
                + "  ↓ " + (normalized.Output is { } output ? StatisticsText.Count(output) : "—") : "—";
        Cost = Quote.Amount is { } amount ? (Quote.IsLowerBound ? "≥" : "≈") + StatisticsText.Money(amount) : "—";
        Duration = DurationText(summary.TotalSeconds);
        FirstContent = T("首字 {0}", DurationText(summary.FirstContentSeconds));
        UsageDetail = T("普通输入 {0} · 缓存读取 {1} · 缓存写入 {2} · 输出 {3} · 思考 {4}（已含在输出）",
            Exact(normalized?.Input), Exact(normalized?.CacheRead), Exact(normalized?.CacheWrite), Exact(normalized?.Output), Exact(normalized?.Reasoning));
        var speed = summary.Outcome == DiagnosticOutcome.Success ? Usage?.Decode() : null;
        TimingDetail = T("总耗时 {0} · 首字 {1} · 输出速度 {2} · 实际发送 {3} 次 · 重试 {4} 次",
            Duration, DurationText(summary.FirstContentSeconds), speed is { } decoded
                ? ((double)decoded.Tokens / decoded.Seconds).ToString("0.#", CultureInfo.CurrentCulture) + " Token/s" : T("未记录"), summary.SendCount, summary.RetryCount);
        RequestDetail = summary.Request.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
            + " · " + summary.Request.Client.Label() + " · " + summary.Request.Method + " " + summary.Request.Endpoint;
        ModelDetail = T("出站模型：{0} · 响应模型：{1} · {2}", Model, Value(Usage?.Model), RequestDiagnosticText.Delivery(summary.Delivery, translate));
        FailureText = string.Join(" · ", new[] { summary.FailureReason, summary.Incomplete ? T("记录不完整") : null }.Where(text => !string.IsNullOrEmpty(text)));
        HasFailure = FailureText.Length > 0;
        CostHelp = T("按统计页的模型单价估算，单位为美元；缺少价格或用量时显示 —。")
            + (Quote.Amount is null ? string.Empty : T("\n计价模型：{0} · {1}", priceModel,
                Quote.Source is "model_override" or "provider_override" ? T("自定义单价") : T("模型公开价")));
        AutomationText = $"{Time} · {Client} · {Model} · {Provider} · {Status} · {Tokens} · {Cost} · {Duration} · {RequestId}";
    }

    private static string DurationText(double? value) => value is { } n && double.IsFinite(n) && n >= 0
        ? n.ToString("0.00", CultureInfo.CurrentCulture) + " s" : "—";
}

public sealed partial class LogEventRow : ObservableObject
{
    public LogEventRow(RuntimeLogEntry entry)
    {
        Entry = entry;
        Parts = LogLine.Split(entry.Line);
    }
    public RuntimeLogEntry Entry { get; }
    private LogLineParts Parts { get; }
    public string Time => DateTimeOffset.FromUnixTimeMilliseconds(Entry.TimestampMs).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string Source => ClientPageText.Translate(Entry.Source.Label());
    public string Route => Entry.RouteName;
    public string Body => Parts.Body;
    public string Level => Entry.Level;
    public string FullLine => Entry.RequestId is { Length: > 0 } id
        ? Entry.Line.Replace("[请求 " + RouteLogger.DisplayId(id) + "]", "[请求 " + id + "]", StringComparison.Ordinal) : Entry.Line;
    public string AutomationText => FullLine;
    [ObservableProperty] private bool _isExpanded;
    public void RefreshLanguage() => OnPropertyChanged(nameof(Source));
}
