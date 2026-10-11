using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Metrics;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using RetryProxy.Service.Interface;

namespace RetryProxy.ViewModel.Pages;

public partial class ConversationPageViewModel : ViewModel
{
    private const int GroupPageSize = 20, RequestPageSize = 20;
    private readonly WorkspaceService _workspace;
    private readonly IConfigService _config;
    private readonly UsageHistory _history = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cancellation;
    private ConversationReport? _report;
    private bool _visible, _syncing, _dirty;
    private long _generation;
    private int _groupPage, _requestPage;
    private DateTime _updatedAt;

    [ObservableProperty] private string _period = "today";
    [ObservableProperty] private string _client = string.Empty;
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _notice = string.Empty;
    [ObservableProperty] private string _updated = string.Empty;
    [ObservableProperty] private string _conversationCount = "0";
    [ObservableProperty] private string _requestCount = "0";
    [ObservableProperty] private string _cost = "—";
    [ObservableProperty] private string _unassigned = string.Empty;
    [ObservableProperty] private string _groupPageCaption = string.Empty;
    [ObservableProperty] private string _requestPageCaption = string.Empty;
    [ObservableProperty] private ConversationRow? _selectedConversation;
    [ObservableProperty] private ConversationRequestRow? _selectedRequest;
    [ObservableProperty] private string _contextCaption = string.Empty;
    [ObservableProperty] private string _contextHelp = string.Empty;
    [ObservableProperty] private string _windowCaption = string.Empty;
    [ObservableProperty] private string _cacheCaption = string.Empty;
    [ObservableProperty] private double _windowProgress;
    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _periods = [];
    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _clients = [];

    public ObservableCollection<ConversationRow> Conversations { get; } = [];
    public ObservableCollection<ConversationRequestRow> Requests { get; } = [];
    public ObservableCollection<ContextPartRow> Parts { get; } = [];
    public bool HasNotice => Notice.Length > 0;
    public bool HasConversations => Conversations.Count > 0;
    public bool HasSelectedRequest => SelectedRequest is not null;
    public bool CanPreviousGroup => !IsLoading && _groupPage > 0;
    public bool CanNextGroup => !IsLoading && _report is { } r && (_groupPage + 1) * GroupPageSize < r.Groups.Count;
    public bool CanPreviousRequest => _requestPage > 0;
    public bool CanNextRequest => SelectedConversation is { } r && (_requestPage + 1) * RequestPageSize < r.Group.Requests.Count;

    public ConversationPageViewModel(WorkspaceService workspace, IConfigService config)
    {
        _workspace = workspace;
        _config = config;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => { if (AutoRefresh && _dirty && !IsLoading) _ = ReloadAsync(); };
        _workspace.Refreshed += () => { if (_visible) _dirty = true; };
        I18nService.Instance.PropertyChanged += (_, _) => { Choices(); if (_report is not null) Render(); };
        Choices();
    }

    private void Choices()
    {
        _syncing = true;
        var period = Period; var client = Client;
        Periods = new[] { ("today", "今天"), ("week", "最近 7 天"), ("month", "最近 30 天"), ("all", "全部") }
            .Select(p => new StatisticsChoice(p.Item1, StatisticsText.T(p.Item2))).ToArray();
        Clients = [new("", StatisticsText.T("全部客户端")), new("Codex", "Codex"), new("Claude Code", "Claude Code")];
        Period = period; Client = client;
        _syncing = false;
    }

    public override void OnNavigatedTo()
    {
        if (_visible) return;
        _visible = true;
        _timer.Start();
        _ = ReloadAsync();
    }

    public override void OnNavigatedFrom()
    {
        _visible = false;
        _timer.Stop();
        _generation++;
        _cancellation?.Cancel();
        IsLoading = false;
    }

    partial void OnPeriodChanged(string value) => FilterChanged();
    partial void OnClientChanged(string value) => FilterChanged();
    partial void OnAutoRefreshChanged(bool value) { if (value && _visible) _ = ReloadAsync(); }
    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnIsLoadingChanged(bool value) => Commands();

    private void FilterChanged()
    {
        if (_syncing) return;
        _groupPage = _requestPage = 0;
        if (_visible) _ = ReloadAsync();
    }

    [RelayCommand] private Task Refresh() => ReloadAsync(force: true);

    private async Task ReloadAsync(bool force = false)
    {
        if (!_visible) return;
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var token = cancellation.Token;
        var generation = ++_generation;
        IsLoading = true;
        _dirty = false;
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            DateOnly? first = Period switch { "all" => null, "week" => today.AddDays(-6), "month" => today.AddDays(-29), _ => today };
            var client = Client ?? string.Empty;
            var clients = _workspace.Workspace.Config.Routes.ToDictionary(r => r.Id, r => r.ClientType);
            var options = _config.Get().OtherConfig;
            var prices = new ModelPricing(options.StatisticsPublicPrices, options.StatisticsModelPrices);
            var result = await Task.Run(async () =>
            {
                await _readGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (force) _history.Invalidate();
                    var history = _history.Read(_workspace.Data, token);
                    return (Report: ConversationReport.Build(history.Records, clients, prices, first, today, client, token), history.Warnings);
                }
                finally { _readGate.Release(); }
            }, token);
            if (token.IsCancellationRequested || generation != _generation || !_visible) return;
            _report = result.Report;
            Notice = string.Join("\n", result.Warnings);
            _updatedAt = DateTime.Now;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (generation == _generation && _visible) Notice = StatisticsText.T("会话读取失败：{0}", error.Message);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            if (generation == _generation) IsLoading = false;
            cancellation.Dispose();
        }
    }

    private void Render()
    {
        if (_report is null) return;
        Updated = StatisticsText.T("更新于 {0} · 仅统计筛选期间已结束的请求", _updatedAt.ToString("HH:mm:ss"));
        ConversationCount = StatisticsText.Exact(_report.Conversations);
        RequestCount = StatisticsText.Exact(_report.Totals.Requests);
        Cost = ConversationRow.Price(_report.Totals);
        Unassigned = StatisticsText.T("{0} 条请求未识别会话；历史缺失信息不补算", _report.Unassigned);
        _groupPage = Math.Min(_groupPage, Math.Max(0, (_report.Groups.Count - 1) / GroupPageSize));
        var old = Conversations.ToDictionary(r => r.Identity);
        var rows = _report.Groups.Skip(_groupPage * GroupPageSize).Take(GroupPageSize).Select(g =>
        {
            var row = old.TryGetValue(g.Identity, out var existing) ? existing : new ConversationRow(g);
            row.Update(g);
            return row;
        }).ToArray();
        var selected = SelectedConversation?.Identity;
        _syncing = true;
        CollectionSync.Update(Conversations, rows);
        SelectedConversation = rows.FirstOrDefault(r => r.Identity == selected) ?? rows.FirstOrDefault();
        _syncing = false;
        OnPropertyChanged(nameof(HasConversations));
        GroupPageCaption = Page(_groupPage, _report.Groups.Count, GroupPageSize);
        RenderRequests();
        Commands();
    }

    partial void OnSelectedConversationChanged(ConversationRow? value)
    {
        if (_syncing) return;
        AutoRefresh = false;
        _requestPage = 0;
        RenderRequests();
    }

    private void RenderRequests()
    {
        var requests = SelectedConversation?.Group.Requests ?? [];
        _requestPage = Math.Min(_requestPage, Math.Max(0, (requests.Count - 1) / RequestPageSize));
        var old = Requests.ToDictionary(r => r.Identity);
        var rows = requests.Skip(_requestPage * RequestPageSize).Take(RequestPageSize).Select(r =>
        {
            var row = old.TryGetValue(r.Identity, out var existing) ? existing : new ConversationRequestRow(r);
            row.Update(r);
            return row;
        }).ToArray();
        var selected = SelectedRequest?.Identity;
        _syncing = true;
        CollectionSync.Update(Requests, rows);
        SelectedRequest = rows.FirstOrDefault(r => r.Identity == selected) ?? rows.FirstOrDefault();
        _syncing = false;
        RequestPageCaption = Page(_requestPage, requests.Count, RequestPageSize);
        RenderContext();
        Commands();
    }

    partial void OnSelectedRequestChanged(ConversationRequestRow? value)
    {
        if (_syncing) return;
        AutoRefresh = false;
        RenderContext();
    }

    private void RenderContext()
    {
        OnPropertyChanged(nameof(HasSelectedRequest));
        var analysis = SelectedRequest?.Request.Analysis;
        var usage = SelectedRequest?.Request.Usage.Usage;
        var measurement = analysis?.Measure(usage);
        ContextCaption = measurement?.Tokens is { } n ? StatisticsText.T(measurement.Complete ? "输入 {0} Token" : "可见文字 {0} Token",
            (measurement.Measured ? "" : "≈") + StatisticsText.Exact(n)) : "—";
        WindowCaption = analysis?.Window is { } window ? StatisticsText.T("窗口 {0} · 已用 {1}", StatisticsText.Exact(window),
            (measurement is { Measured: false, FillPercent: not null } ? "≈" : "") + StatisticsText.Rate(measurement?.FillPercent)) : StatisticsText.T("窗口上限未设置");
        WindowProgress = Math.Clamp(measurement?.FillPercent ?? 0, 0, 100);
        CacheCaption = StatisticsText.T("已复用输入 {0}", usage?.CachedTokens is { } read ? StatisticsText.Exact(read) : "—");
        ContextHelp = StatisticsText.T(analysis?.Status switch
        {
            null => "此请求没有上下文分析记录",
            "size_limit" => "请求超过分析上限，未分析内容构成",
            "encoded" => "请求正文已压缩，未分析内容构成",
            "invalid_json" or "unsupported" => "此请求的内容格式暂不支持分析",
            _ when analysis.HasServerHistory || analysis.HasUnmeasuredContent => "分项仅估算本次可见文字；上游保留的历史、图片和不透明内容未拆分",
            _ when measurement?.Measured == true => "分项占比为估算，已按供应商返回的输入总量校准",
            _ => "内容构成与用量均为估算，供应商未返回完整输入总量",
        });
        var old = Parts.ToDictionary(p => p.Kind);
        var rows = (measurement?.Parts ?? []).Select(p =>
        {
            var row = old.TryGetValue(p.Kind, out var existing) ? existing : new ContextPartRow(p.Kind);
            row.Update(p);
            return row;
        }).ToArray();
        CollectionSync.Update(Parts, rows);
    }

    private static string Page(int page, int count, int size) => StatisticsText.T("第 {0} / {1} 页 · {2} 条", page + 1, Math.Max(1, (count + size - 1) / size), count);

    [RelayCommand(CanExecute = nameof(CanPreviousGroup))] private void PreviousGroup() { AutoRefresh = false; _groupPage--; _requestPage = 0; Render(); }
    [RelayCommand(CanExecute = nameof(CanNextGroup))] private void NextGroup() { AutoRefresh = false; _groupPage++; _requestPage = 0; Render(); }
    [RelayCommand(CanExecute = nameof(CanPreviousRequest))] private void PreviousRequest() { AutoRefresh = false; _requestPage--; RenderRequests(); }
    [RelayCommand(CanExecute = nameof(CanNextRequest))] private void NextRequest() { AutoRefresh = false; _requestPage++; RenderRequests(); }

    private void Commands()
    {
        PreviousGroupCommand.NotifyCanExecuteChanged(); NextGroupCommand.NotifyCanExecuteChanged();
        PreviousRequestCommand.NotifyCanExecuteChanged(); NextRequestCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class ConversationRow : ObservableObject
{
    public ConversationGroup Group { get; private set; }
    public string Identity => Group.Identity;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private string _cost = string.Empty;
    public ConversationRow(ConversationGroup group) { Group = group; Update(group); }
    public void Update(ConversationGroup group)
    {
        Group = group;
        Name = group.IsUnassigned ? StatisticsText.T("未归属请求") : $"{group.Client} · {group.Hash![..8]}";
        Detail = StatisticsText.T("{0} 次请求 · {1}", group.Requests.Count, group.Last?.ToString("MM-dd HH:mm:ss", CultureInfo.CurrentUICulture) ?? "—");
        Cost = Price(group.Totals);
    }
    internal static string Price(UsageTotals totals) => totals.Priced == 0 ? "—"
        : (totals.Unpriced > 0 || totals.PartiallyPriced > 0 ? "≥" : "≈") + StatisticsText.Money(totals.Cost);
}

public sealed partial class ConversationRequestRow : ObservableObject
{
    public ConversationRequest Request { get; private set; }
    public string Identity => Request.Identity;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private string _cost = string.Empty;
    public string RequestId => Request.Usage.RequestId;
    public ConversationRequestRow(ConversationRequest request) { Request = request; Update(request); }
    public void Update(ConversationRequest request)
    {
        Request = request;
        var row = request.Usage;
        Title = row.Group(UsageDimension.Model).Name;
        Detail = (row.CompletedAt?.ToString("MM-dd HH:mm:ss", CultureInfo.CurrentUICulture) ?? "—") + " · "
            + StatisticsText.T(row.Outcome == RequestOutcome.Success ? "成功" : "失败") + " · " + row.Group(UsageDimension.Provider).Name;
        Cost = row.Quote.Amount is { } value ? (row.Quote.IsLowerBound ? "≥" : "≈") + StatisticsText.Money(value) : "—";
    }
}

public sealed partial class ContextPartRow : ObservableObject
{
    public PromptPartKind Kind { get; }
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private double _percent;
    public ContextPartRow(PromptPartKind kind) => Kind = kind;
    public void Update(MeasuredPromptPart part)
    {
        Name = StatisticsText.T(Kind switch { PromptPartKind.System => "系统规则", PromptPartKind.Instructions => "指令与记忆", PromptPartKind.Tools => "工具说明", PromptPartKind.Files => "文件内容", PromptPartKind.Results => "工具结果", _ => "对话" });
        Value = "≈" + StatisticsText.Exact(part.Tokens) + " · " + StatisticsText.Rate(part.Percent);
        Percent = part.Percent;
    }
}
