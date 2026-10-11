using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using RetryProxy.Service.Interface;
using RetryProxy.ViewModel.Pages;
using Wpf.Ui;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed partial class StatisticsViewModelTests
{
    [Fact]
    public void FiltersPaginationAndRapidNavigationKeepTheLatestSelection() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo();
        await Ready(view);
        Assert.Equal("30", view.TotalRequests);
        Assert.Equal(25, view.RequestRows.Count);
        view.NextPageCommand.Execute(null);
        Assert.Equal(5, view.RequestRows.Count);
        view.SelectedPeriod = "week";
        await Ready(view);
        Assert.Equal("31", view.TotalRequests);
        Assert.False(view.CanPrevious);

        view.SelectedModel = "model-b";
        view.SelectedModel = "model-a";
        view.SelectedModel = "model-b";
        view.OnNavigatedFrom();
        view.OnNavigatedTo();
        await Ready(view);
        Assert.Equal("1", view.TotalRequests);
        Assert.Equal("model-b", Assert.Single(view.RequestRows).Model);
        Assert.False(view.CanNext);
        view.SelectedStatus = "failure";
        await Ready(view);
        Assert.Equal("0", view.TotalRequests);
        Assert.False(view.CanExport);
        view.ResetFiltersCommand.Execute(null);
        await Ready(view);
        Assert.Equal("31", view.TotalRequests);
        Assert.Equal(371, view.HeatDays.Count);
        Assert.Equal(2, view.HeatDays.Count(day => day.ToolTip.Contains("请求 ")));
    });

    [Fact]
    public void InvalidDateHidesPreviousReportAndRecoversAfterCorrection() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo();
        await Ready(view);
        view.FromDate = DateTime.Today;
        view.ToDate = DateTime.Today.AddDays(-1);
        view.SelectedPeriod = "custom";
        await WaitUntil(() => !view.IsLoading);
        Assert.False(view.HasReport);
        Assert.True(view.HasNotice);
        Assert.False(view.CanExport);
        view.ToDate = DateTime.Today;
        await Ready(view);
        Assert.Equal("30", view.TotalRequests);
        Assert.False(view.HasNotice);
    });

    [Fact]
    public void SavedPricesRecalculateHistoryAndFailedSaveKeepsPreviousPrice() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo();
        await Ready(view);
        view.EditingModel = "model-a";
        view.BasePrices.Input = "2";
        view.BasePrices.Output = "10";
        view.BasePrices.CacheRead = "0.2";
        await view.SavePriceCommand.ExecuteAsync(null);
        Assert.Equal("≈$0.0468", view.TotalCost);
        Assert.All(view.RequestRows, row => Assert.Equal("≈$0.00156", row.Cost));
        view.SelectedMetric = "cost";
        Assert.Equal(view.TotalCost, Assert.Single(view.RankRows).Value);

        var saved = fixture.Config.Get().OtherConfig.StatisticsModelPrices;
        fixture.Config.FailSave = true;
        view.BasePrices.Output = "20";
        await view.SavePriceCommand.ExecuteAsync(null);
        Assert.Same(saved, fixture.Config.Get().OtherConfig.StatisticsModelPrices);
        Assert.Equal("≈$0.0468", view.TotalCost);
        Assert.Contains("单价未保存", view.PriceFeedback);
        Assert.Equal("20", view.BasePrices.Output);
        fixture.Config.FailSave = false;
        await view.SavePriceCommand.ExecuteAsync(null);
        Assert.Equal("≈$0.0768", view.TotalCost);
        await view.RestorePriceCommand.ExecuteAsync(null);
        Assert.Equal("—", view.TotalCost);
        Assert.Empty(fixture.Config.Get().OtherConfig.StatisticsModelPrices);
    });

    [Fact]
    public void LanguageChangePreservesFiltersPageAndUnsavedPriceDraft() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo();
        await Ready(view);
        view.SelectedModel = "model-a";
        view.SelectedMetric = "cost";
        view.SelectedDimension = "provider";
        view.SelectedHeatMetric = "tokens";
        await Ready(view);
        view.NextPageCommand.Execute(null);
        view.EditingModel = "model-a";
        view.BasePrices.Input = "3.25";
        view.PriceTiers.Add(new StatisticsPriceTier { AboveTokens = "200000", Output = "9.5" });
        var draft = view.BasePrices;
        I18nService.Instance.ChangeLanguage("en");
        await Ready(view);
        Assert.Equal("All clients", view.ClientChoices[0].Name);
        Assert.Equal("Cost", view.MetricChoices.Single(c => c.Id == "cost").Name);
        Assert.Equal("model-a", view.SelectedModel);
        Assert.Equal("provider", view.SelectedDimension);
        Assert.Equal("tokens", view.SelectedHeatMetric);
        Assert.True(view.CanPrevious);
        Assert.Equal(5, view.RequestRows.Count);
        Assert.Same(draft, view.BasePrices);
        Assert.Equal("3.25", view.BasePrices.Input);
        Assert.Equal("9.5", Assert.Single(view.PriceTiers).Output);
    });

    private sealed class TestConfig : IConfigService
    {
        private readonly AllConfig _config = new() { Proxy = ProxyConfig.Builtin() };
        public bool FailSave { get; set; }
        public AllConfig Get() => _config;
        public void Save() => SaveChecked();
        public void SaveChecked() { if (FailSave) throw new IOException("fixture save failed"); }
        public void BackupBeforeDeletion() { }
        public void BackupBeforeClientTakeover() { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-statistics-vm-" + Guid.NewGuid().ToString("N"));
        private readonly ProxyLogger _logger;
        private readonly WorkspaceService _workspace;
        public TestConfig Config { get; } = new();
        public OverviewPageViewModel View { get; }

        public Fixture()
        {
            I18nService.Instance.ChangeLanguage("zh-Hans");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            foreach (var route in Config.Get().Proxy!.Routes) route.DesiredRunning = false;
            _logger = ProxyLogger.Silent(_directory);
            _workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, Config, _logger, new SnackbarService());
            var routeId = Config.Get().Proxy!.RouteFor(ClientType.Codex)!.Id;
            var storage = new SqliteDailyStorage(_workspace.Data, routeId, "Codex");
            var today = DateOnly.FromDateTime(DateTime.Today);
            for (var i = 0; i < 31; i++)
            {
                var date = i == 30 ? today.AddDays(-1) : today;
                var model = i == 30 ? "model-b" : "model-a";
                var journal = storage.Open(date, false).Journal!;
                journal.Append(new DailyRequest
                {
                    RequestId = "request-" + i,
                    Outcome = i < 30 && i % 5 == 0 ? RequestOutcome.Failure : RequestOutcome.Success,
                    UpdatedAtUnixMs = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, i), DateTimeKind.Local)).ToUnixTimeMilliseconds(),
                    KeyId = "key", KeyName = "test key",
                    Attribution = new RequestAttribution { ProviderId = "provider", ProviderName = "test provider", Model = model, Client = ClientType.Codex },
                    Usage = new RequestUsage { Model = model, InputAccounting = CacheInputAccounting.IncludesCached, InputTokens = 1000, CachedTokens = 800, OutputTokens = 100 },
                });
            }
            View = new OverviewPageViewModel(_workspace, Config);
        }

        public void Append(string id, int daysAgo = 0, ClientType client = ClientType.Codex)
        {
            var date = DateOnly.FromDateTime(DateTime.Today).AddDays(-daysAgo);
            var route = Config.Get().Proxy!.RouteFor(client)!;
            new SqliteDailyStorage(_workspace.Data, route.Id, route.Name).Open(date, false).Journal!.Append(new DailyRequest
            {
                RequestId = id, Outcome = RequestOutcome.Success,
                UpdatedAtUnixMs = new DateTimeOffset(date.ToDateTime(new TimeOnly(13, 0), DateTimeKind.Local)).ToUnixTimeMilliseconds(),
                KeyId = "key", KeyName = "test key",
                Attribution = new RequestAttribution { ProviderId = "provider", ProviderName = "test provider", Model = daysAgo == 0 ? "model-a" : "model-b", Client = client },
                Usage = new RequestUsage { Model = daysAgo == 0 ? "model-a" : "model-b", InputAccounting = CacheInputAccounting.IncludesCached,
                    InputTokens = 1000, CachedTokens = 800, OutputTokens = 100 },
            });
        }

        public void NotifyRefresh() => _workspace.Flush();

        public void Dispose()
        {
            View.OnNavigatedFrom();
            _workspace.Shutdown();
            _logger.Dispose();
            I18nService.Instance.ChangeLanguage("zh-Hans");
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private static Task Ready(OverviewPageViewModel view) => WaitUntil(() => !view.IsLoading && view.HasReport);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), "Statistics refresh did not finish");
            await Task.Delay(10);
        }
    }

    private static void Run(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await test(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Statistics tests timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
