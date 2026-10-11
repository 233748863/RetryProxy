using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RetryProxy.Core.Config;
using RetryProxy.Core.Metrics;
using RetryProxy.Service;
using RetryProxy.Service.Interface;
using RetryProxy.Service.I18n;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace RetryProxy.ViewModel.Pages;

/// <summary>统计分析：只在可见时后台读取。旧查询不能覆盖新筛选，切页取消，刷新不重置用户选择。</summary>
public partial class OverviewPageViewModel : ViewModel
{
    private const int PageSize = 25;
    private readonly WorkspaceService _workspaceService;
    private readonly IConfigService _configService;
    private readonly UsageHistory _history = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _loadCancellation;
    private long _generation;
    private bool _visible;
    private bool _dirty;
    private bool _syncing;
    private bool _forceRead;
    private DateTime _lastDay = DateTime.Today;
    private UsageReport? _report;
    private ModelPricing _prices;
    private int _page;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _hasRequests;
    [ObservableProperty] private string _notice = string.Empty;
    [ObservableProperty] private string _rangeCaption = string.Empty;
    [ObservableProperty] private string _updatedCaption = string.Empty;
    [ObservableProperty] private string _selectedPeriod = "today";
    [ObservableProperty] private DateTime? _fromDate = DateTime.Today;
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;
    [ObservableProperty] private string _selectedClient = string.Empty;
    [ObservableProperty] private string _selectedProvider = string.Empty;
    [ObservableProperty] private string _selectedModel = string.Empty;
    [ObservableProperty] private string _selectedKey = string.Empty;
    [ObservableProperty] private string _selectedStatus = string.Empty;
    [ObservableProperty] private string _selectedMetric = "tokens";
    [ObservableProperty] private string _selectedDimension = "model";
    [ObservableProperty] private string _selectedHeatMetric = "requests";

    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _clientChoices = [];
    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _providerChoices = [];
    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _modelChoices = [];
    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _keyChoices = [];
    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _statusChoices = [];
    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _metricChoices = [];
    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _dimensionChoices = [];
    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _heatMetricChoices = [];

    [ObservableProperty] private string _totalRequests = "0";
    [ObservableProperty] private string _requestsDetail = string.Empty;
    [ObservableProperty] private string _requestsHelp = string.Empty;
    [ObservableProperty] private string _totalTokens = "—";
    [ObservableProperty] private string _tokensDetail = string.Empty;
    [ObservableProperty] private string _tokensHelp = string.Empty;
    [ObservableProperty] private string _cacheRate = "—";
    [ObservableProperty] private double _cacheProgress;
    [ObservableProperty] private string _cacheDetail = string.Empty;
    [ObservableProperty] private string _cacheRateHelp = string.Empty;
    [ObservableProperty] private string _totalCost = "—";
    [ObservableProperty] private string _costDetail = string.Empty;
    [ObservableProperty] private string _costHelp = string.Empty;
    [ObservableProperty] private IReadOnlyList<StatisticsTokenTile> _tokenTiles = [];
    [ObservableProperty] private StatisticsChartData? _chart;
    [ObservableProperty] private IReadOnlyList<StatisticsCategory> _categories = [];
    [ObservableProperty] private IReadOnlyList<StatisticsRankRow> _rankRows = [];
    [ObservableProperty] private string _chartCaption = string.Empty;
    [ObservableProperty] private IReadOnlyList<StatisticsHeatDay> _heatDays = [];
    [ObservableProperty] private IReadOnlyList<string> _heatMonths = [];
    [ObservableProperty] private string _heatCaption = string.Empty;
    [ObservableProperty] private IReadOnlyList<StatisticsRequestRow> _requestRows = [];
    [ObservableProperty] private string _pageCaption = string.Empty;

    public bool IsToday => SelectedPeriod == "today";
    public bool IsWeek => SelectedPeriod == "week";
    public bool IsMonth => SelectedPeriod == "month";
    public bool IsAll => SelectedPeriod == "all";
    public bool IsCustom => SelectedPeriod == "custom";
    public bool HasNotice => Notice.Length > 0;
    public DateTime Today => DateTime.Today;
    public bool CanExport => HasReport && HasRequests && !IsLoading;
    public bool CanPrevious => _page > 0;
    public bool CanNext => _report is { } report && (_page + 1) * PageSize < report.Records.Count;

    public OverviewPageViewModel(WorkspaceService workspaceService, IConfigService configService)
    {
        _workspaceService = workspaceService;
        _configService = configService;
        _prices = CurrentPrices();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) =>
        {
            if (_lastDay != DateTime.Today) { _lastDay = DateTime.Today; OnPropertyChanged(nameof(Today)); _dirty = true; }
            if (_dirty && !IsLoading) _ = ReloadAsync();
        };
        _workspaceService.Refreshed += () => { if (_visible) _dirty = true; };
        ResetChoices();
        UpdatePriceChoices();
        I18nService.Instance.PropertyChanged += (_, _) => RefreshLanguage();
    }

    private ModelPricing CurrentPrices()
    {
        var config = _configService.Get().OtherConfig;
        return new ModelPricing(config.StatisticsPublicPrices, config.StatisticsModelPrices);
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
        _loadCancellation?.Cancel();
        IsLoading = false;
    }

    private void QueryChanged()
    {
        if (_syncing) return;
        _page = 0;
        _dirty = true;
        if (_visible) _ = ReloadAsync();
    }

    partial void OnSelectedPeriodChanged(string value)
    {
        foreach (var name in new[] { nameof(IsToday), nameof(IsWeek), nameof(IsMonth), nameof(IsAll), nameof(IsCustom) }) OnPropertyChanged(name);
        QueryChanged();
    }
    partial void OnFromDateChanged(DateTime? value) { if (IsCustom) QueryChanged(); }
    partial void OnToDateChanged(DateTime? value) { if (IsCustom) QueryChanged(); }
    partial void OnSelectedClientChanged(string value) => QueryChanged();
    partial void OnSelectedProviderChanged(string value) => QueryChanged();
    partial void OnSelectedModelChanged(string value) => QueryChanged();
    partial void OnSelectedKeyChanged(string value) => QueryChanged();
    partial void OnSelectedStatusChanged(string value) => QueryChanged();
    partial void OnSelectedDimensionChanged(string value) => QueryChanged();
    partial void OnSelectedMetricChanged(string value) { if (!_syncing && _report is not null) RenderChart(_report); }
    partial void OnSelectedHeatMetricChanged(string value) { if (!_syncing && _report is not null) RenderHeatmap(_report); }
    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnIsLoadingChanged(bool value) => UpdateCommands();
    partial void OnHasReportChanged(bool value) => UpdateCommands();
    partial void OnHasRequestsChanged(bool value) => UpdateCommands();

    private UsageQuery Query()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var first = SelectedPeriod switch
        {
            "week" => today.AddDays(-6), "month" => today.AddDays(-29), "all" => (DateOnly?)null,
            "custom" => FromDate is { } startDate ? DateOnly.FromDateTime(startDate) : throw new ArgumentException(StatisticsText.T("请选择开始日期")),
            _ => today,
        };
        var last = IsCustom ? ToDate is { } date ? DateOnly.FromDateTime(date) : throw new ArgumentException(StatisticsText.T("请选择结束日期")) : today;
        if (first > last || last > today) throw new ArgumentException(StatisticsText.T("开始日期不能晚于结束日期，结束日期不能晚于今天"));
        return new UsageQuery
        {
            FirstDate = first, LastDate = last, Today = today, Client = SelectedClient ?? string.Empty,
            Provider = SelectedProvider ?? string.Empty, Model = SelectedModel ?? string.Empty, Key = SelectedKey ?? string.Empty,
            Outcome = SelectedStatus switch { "success" => RequestOutcome.Success, "failure" => RequestOutcome.Failure, _ => null },
            Dimension = SelectedDimension switch { "provider" => UsageDimension.Provider, "client" => UsageDimension.Client, "key" => UsageDimension.Key, _ => UsageDimension.Model },
        };
    }

    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        _loadCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();
        var cancellation = _loadCancellation;
        var token = cancellation.Token;
        _dirty = false;
        IsLoading = true;
        try
        {
            var query = Query();
            if (_report?.Query != query) HasReport = false;
            var clients = _workspaceService.Workspace.Config.Routes.ToDictionary(r => r.Id, r => r.ClientType, StringComparer.Ordinal);
            var prices = CurrentPrices();
            var force = _forceRead;
            _forceRead = false;
            var result = await Task.Run(async () =>
            {
                await _readGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (force) _history.Invalidate();
                    var history = _history.Read(_workspaceService.Data, token);
                    var report = UsageReport.Build(history.Records, clients, prices, query, token);
                    return (history, report);
                }
                finally { _readGate.Release(); }
            }, token);
            if (token.IsCancellationRequested || generation != _generation || !_visible) return;
            _prices = prices;
            _report = result.report;
            Notice = string.Join("\n", result.history.Warnings);
            HasReport = true;
            HasRequests = _report.Records.Count > 0;
            RangeCaption = StatisticsText.T("{0} 至 {1} · 按结束日期统计", _report.FirstDate.ToString("yyyy-MM-dd"), query.LastDate.ToString("yyyy-MM-dd"));
            UpdatedCaption = StatisticsText.T("本机时间 · 自动刷新 · {0}", DateTime.Now.ToString("HH:mm:ss"));
            UpdateChoices(_report);
            RenderTotals(_report.Totals);
            RenderChart(_report);
            RenderHeatmap(_report);
            RenderRequestPage();
            UpdatePriceChoices();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (generation != _generation || !_visible) return;
            HasReport = false;
            Notice = StatisticsText.T("统计读取失败：{0}", error.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
            if (generation == _generation) IsLoading = false;
            cancellation.Dispose();
        }
    }

    [RelayCommand] private void OnSelectPeriod(string period) => SelectedPeriod = period;
    [RelayCommand] private async Task OnRefresh() { _forceRead = true; await ReloadAsync(); }

    [RelayCommand]
    private void OnResetFilters()
    {
        _syncing = true;
        SelectedClient = SelectedProvider = SelectedModel = SelectedKey = SelectedStatus = string.Empty;
        _syncing = false;
        QueryChanged();
    }

    [RelayCommand]
    private void OnSelectRange(StatisticsChartPoint point)
    {
        if (_report?.Bucket == "hour") return;
        SelectDates(point.First, point.Last);
    }

    [RelayCommand] private void OnSelectDay(DateTime date) => SelectDates(DateOnly.FromDateTime(date), DateOnly.FromDateTime(date));

    private void SelectDates(DateOnly first, DateOnly last)
    {
        _syncing = true;
        FromDate = first.ToDateTime(TimeOnly.MinValue);
        ToDate = last.ToDateTime(TimeOnly.MinValue);
        SelectedPeriod = "custom";
        _syncing = false;
        QueryChanged();
    }

    [RelayCommand]
    private void OnSelectGroup(string id)
    {
        switch (_report?.Query.Dimension)
        {
            case UsageDimension.Provider: SelectedProvider = SelectedProvider == id ? string.Empty : id; break;
            case UsageDimension.Client: SelectedClient = SelectedClient == id ? string.Empty : id; break;
            case UsageDimension.Key: SelectedKey = SelectedKey == id ? string.Empty : id; break;
            default: SelectedModel = SelectedModel == id ? string.Empty : id; break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPrevious))] private void OnPreviousPage() { _page--; RenderRequestPage(); }
    [RelayCommand(CanExecute = nameof(CanNext))] private void OnNextPage() { _page++; RenderRequestPage(); }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task OnExport()
    {
        if (_report is not { } report) return;
        var dialog = new SaveFileDialog
        {
            Title = StatisticsText.T("导出当前筛选的全部请求"), Filter = "CSV (*.csv)|*.csv",
            FileName = $"RetryProxy-{report.FirstDate:yyyyMMdd}-{report.Query.LastDate:yyyyMMdd}.csv",
            DefaultExt = ".csv", AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await Task.Run(() =>
            {
                using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
                UsageCsv.Write(writer, report.Records);
            });
            Notice = StatisticsText.T("已导出 {0} 条请求", report.Records.Count);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Notice = StatisticsText.T("导出失败：{0}", error.Message); }
    }

    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        ExportCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }
}
