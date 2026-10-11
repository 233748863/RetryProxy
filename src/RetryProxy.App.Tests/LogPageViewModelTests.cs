using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
using RetryProxy.Core.Diagnostics;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using RetryProxy.Service.I18n;
using RetryProxy.Service.Interface;
using RetryProxy.ViewModel.Pages;
using Wpf.Ui;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed class LogPageViewModelTests
{
    [Fact]
    public void UnchangedRefreshKeepsRequestRowsAndDoesNotNotifyAnyRowFields() => Run(async () =>
    {
        using var fixture = new Fixture(12);
        var view = fixture.View;
        await fixture.Start();
        var rows = view.RequestRows.ToArray();
        var changes = new List<string>();
        foreach (var row in rows) row.PropertyChanged += (_, args) => changes.Add(args.PropertyName!);
        view.RequestRows.CollectionChanged += (_, args) => changes.Add(args.Action.ToString());
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(rows, view.RequestRows);
        Assert.Empty(changes);
    });

    [Fact]
    public void ExpandedRequestAndHistoricalPagesStayFixedWhileNewRequestsArrive() => Run(async () =>
    {
        using var fixture = new Fixture(115);
        var view = fixture.View;
        await fixture.Start();
        var rows = view.RequestRows.ToArray();
        view.ToggleRequestCommand.Execute(rows[2]);
        Assert.False(view.AutoScroll);
        Assert.Same(rows[2], view.SelectedRequest);
        fixture.Append();
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(rows, view.RequestRows);
        Assert.True(rows[2].IsExpanded);
        Assert.Equal(1, view.PendingCount);
        Assert.Equal(115, view.Total);
        view.OlderCommand.Execute(null);
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(15, view.RequestRows.Count);
        var older = view.RequestRows.ToArray();
        fixture.Append();
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(older, view.RequestRows);
        Assert.Equal(2, view.PendingCount);
        view.LatestCommand.Execute(null);
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(117, view.Total);
        Assert.Equal(0, view.PendingCount);
        Assert.True(view.AutoScroll);
        Assert.False(view.CanNewer);
    });

    [Fact]
    public void PausingDuringAnInflightReadPreventsItsNewRowsFromBeingInserted() => Run(async () =>
    {
        using var fixture = new Fixture(10);
        var view = fixture.View;
        await fixture.Start();
        var original = view.RequestRows.ToArray();
        fixture.Append();
        var refresh = view.RefreshCommand.ExecuteAsync(null);
        view.PauseFollowing();
        await refresh;
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(original, view.RequestRows);
        Assert.Equal(1, view.PendingCount);
    });

    [Fact]
    public void FinishingAVisibleRequestUpdatesThatRowWithoutResettingTheCollection() => Run(async () =>
    {
        using var fixture = new Fixture(5);
        var pending = fixture.Append(pending: true);
        var view = fixture.View;
        await fixture.Start();
        var row = view.RequestRows[0];
        Assert.Equal(DiagnosticOutcome.Pending, row.Tone);
        view.ToggleRequestCommand.Execute(row);
        var changes = 0;
        view.RequestRows.CollectionChanged += (_, _) => changes++;
        Fixture.Complete(pending);
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Same(row, view.RequestRows[0]);
        Assert.Equal(DiagnosticOutcome.Success, row.Tone);
        Assert.True(row.IsExpanded);
        Assert.Equal(0, changes);
    });

    [Fact]
    public void RapidSearchAndModeChangesShowOnlyTheLatestFilterAndKeepPreparationNavigation() => Run(async () =>
    {
        using var fixture = new Fixture(12);
        var view = fixture.View;
        await fixture.Start();
        view.Query = "absent";
        var stale = view.RefreshCommand.ExecuteAsync(null);
        view.Query = "gpt-5.4";
        await stale;
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.NotEmpty(view.RequestRows);
        Assert.All(view.RequestRows, row => Assert.Equal("gpt-5.4", row.Model));
        view.RequestFilter = "failed";
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.All(view.RequestRows, row => Assert.Equal(DiagnosticOutcome.Failure, row.Tone));
        view.OpenPreparation(42);
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.False(view.IsRequestView);
        Assert.Equal(LogSource.Preparation, view.SourceFilter);
        Assert.Equal("[准备 42 · ", view.Query);
        Assert.True(view.IsEmpty);
    });

    [Fact]
    public void RuntimeSnapshotRetainsRowsSelectionAndFullIdentityUntilFollowingResumes() => Run(async () =>
    {
        using var fixture = new Fixture(0);
        var view = fixture.View;
        var id = Guid.NewGuid().ToString("N");
        var request = fixture.Logger.Route("Codex").ForRequest(id, false, false);
        request.Info($"[{id}] first");
        request.Info($"[{id}] second");
        view.IsRequestView = false;
        await fixture.Start();
        var rows = view.Rows.ToArray();
        view.SelectedEvent = rows[0];
        Assert.True(view.CanCopyRequest);
        request.Info($"[{id}] third");
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(rows, view.Rows);
        Assert.Same(rows[0], view.SelectedEvent);
        Assert.Contains("[请求 " + id + "]", rows[0].FullLine);
        Assert.Equal(1, view.PendingCount);
        view.LatestCommand.Execute(null);
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(3, view.Total);
        view.ClearCommand.Execute(null);
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(view.Rows);
        Assert.Equal(3, (await fixture.Logger.Store.QueryAsync(new(fixture.Logger.Store.Today))).Total);
    });

    [Fact]
    public void PriceChangeUpdatesCostInPlaceAndUnknownUsageStaysMissing() => Run(async () =>
    {
        using var fixture = new Fixture(2);
        var view = fixture.View;
        await fixture.Start();
        var row = view.RequestRows.First(item => item.Model == "gpt-5.4");
        fixture.Config.Get().OtherConfig.StatisticsModelPrices =
        [new ModelPriceOverride { Model = "gpt-5.4", Price = new ModelPrice { Rates = new TokenPrices { Input = 2, Output = 10, CacheRead = .2m } } }];
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Same(row, view.RequestRows.Single(item => item.RequestId == row.RequestId));
        Assert.Equal("≈$0.00156", row.Cost);
        fixture.Append(pending: true);
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("—", view.RequestRows[0].Tokens);
        Assert.Equal("—", view.RequestRows[0].Cost);
    });

    [Fact]
    public void UsageLookupDoesNotCombineCollidingClientOrRouteIdentities()
    {
        var date = new DateOnly(2026, 10, 11);
        var summary = new DiagnosticSummary(new("id", ClientType.Codex, date, DateTimeOffset.Now, "POST", "/v1/responses"));
        var codex = new DailyRequest { RequestId = "id", Attribution = new RequestAttribution { Client = ClientType.Codex } };
        var claude = new DailyRequest { RequestId = "id", Attribution = new RequestAttribution { Client = ClientType.Claude } };
        Assert.Same(codex, LogPageViewModel.FindUsage(summary, [new("codex", date, codex), new("claude", date, claude)]));
        Assert.Null(LogPageViewModel.FindUsage(summary, [new("one", date, codex), new("two", date, codex)]));
        Assert.True(LogPageViewModel.Matches(summary with { Delivery = DiagnosticDelivery.Interrupted }, null, "", "failed"));
    }

    [Fact]
    public void CsvExportIncludesEveryPageAndPreservesMissingValuesAndQuotedFields() => Run(async () =>
    {
        using var fixture = new Fixture(114);
        fixture.Append(pending: true);
        await fixture.Start();
        Assert.Equal(100, fixture.View.RequestRows.Count);
        var snapshot = (await fixture.ReadSummaries()).Items.ToArray();
        var pending = snapshot.Single(summary => summary.Outcome == DiagnosticOutcome.Pending).Request.RequestId;
        var unsafeTarget = snapshot[0].LastTarget! with { Model = "=1+1", ProviderName = "名称,含\"引号\"\n与换行" };
        snapshot[0] = snapshot[0] with { LastTarget = unsafeTarget };
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var config = fixture.Config.Get().OtherConfig;
        await fixture.View.ExportRequestsAsync(output, snapshot, fixture.Logger.Store.Today,
            new ModelPricing(config.StatisticsPublicPrices, config.StatisticsModelPrices));
        using var reader = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(output.ToString()));
        reader.SetDelimiters(",");
        reader.HasFieldsEnclosedInQuotes = true;
        Assert.Equal(20, reader.ReadFields()!.Length);
        var records = new List<string[]>();
        while (!reader.EndOfData) records.Add(reader.ReadFields()!);
        Assert.Equal(115, records.Count);
        Assert.All(records, record => Assert.Equal(20, record.Length));
        Assert.Equal(115, records.Select(record => record[19]).Distinct().Count());
        var escaped = records.Single(record => record[19] == snapshot[0].Request.RequestId);
        Assert.Equal("'=1+1", escaped[2]);
        Assert.Equal(unsafeTarget.ProviderName, escaped[3]);
        var missing = records.Single(record => record[19] == pending);
        Assert.Equal(string.Empty, missing[9]);
        Assert.Equal(string.Empty, missing[15]);
    });

    private sealed class TestConfig : IConfigService
    {
        private readonly AllConfig _config = new() { Proxy = ProxyConfig.Builtin() };
        public AllConfig Get() => _config;
        public void Save() { }
        public void SaveChecked() { }
        public void BackupBeforeDeletion() { }
        public void BackupBeforeClientTakeover() { }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-log-vm-" + Guid.NewGuid().ToString("N"));
        private readonly WorkspaceService _workspace;
        private int _sequence;
        public TestConfig Config { get; } = new();
        public ProxyLogger Logger { get; }
        public LogPageViewModel View { get; }
        public Fixture(int count)
        {
            I18nService.Instance.ChangeLanguage("zh-Hans");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            foreach (var route in Config.Get().Proxy!.Routes) route.DesiredRunning = false;
            Logger = ProxyLogger.Silent(_directory);
            _workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, Config, Logger, new SnackbarService());
            for (var index = 0; index < count; index++) Append();
            View = new LogPageViewModel(_workspace, Config);
        }
        public async Task Start() { View.OnNavigatedTo(); await View.RefreshCommand.ExecuteAsync(null); }
        public Task<DiagnosticSummaries> ReadSummaries() => _workspace.Diagnostics.ReadSummariesAsync(_workspace.Diagnostics.Today);
        public IDiagnosticRequest Append(bool pending = false)
        {
            var index = _sequence++;
            var client = index % 2 == 0 ? ClientType.Codex : ClientType.Claude;
            var route = Config.Get().Proxy!.RouteFor(client)!;
            var date = _workspace.Diagnostics.Today;
            var id = Guid.NewGuid().ToString("N");
            var model = client == ClientType.Codex ? "gpt-5.4" : "claude-sonnet-4-6";
            var request = _workspace.Diagnostics.Begin(new(id, client, date, DateTimeOffset.Now.AddHours(-1).AddSeconds(index), "POST", "/v1/responses"))!;
            request.Record(new(DiagnosticEventKind.SendStarted, .1) { Target = new("p", "Provider", "key", "Account", model), RetryCount = index % 3 == 0 ? 1UL : 0 });
            if (!pending) Complete(request, index % 5 == 0 ? DiagnosticOutcome.Failure : DiagnosticOutcome.Success);
            new SqliteDailyStorage(_workspace.Data, route.Id, route.Name).Open(date, false).Journal!.Append(new DailyRequest
            {
                RequestId = id, Outcome = pending ? null : RequestOutcome.Success, UpdatedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Attribution = new RequestAttribution { Client = client, Model = model, ProviderId = "p", ProviderName = "Provider" },
                Usage = pending ? null : new RequestUsage { Model = model, InputAccounting = CacheInputAccounting.IncludesCached, InputTokens = 1000, CachedTokens = 800, OutputTokens = 100 }
            });
            return request;
        }
        public static void Complete(IDiagnosticRequest request, DiagnosticOutcome outcome = DiagnosticOutcome.Success)
        {
            request.Record(new(DiagnosticEventKind.Outcome, 2) { Outcome = outcome, StatusCode = outcome == DiagnosticOutcome.Success ? 200 : 429, FirstContentSeconds = .5 });
            request.Record(new(DiagnosticEventKind.Delivery, 2.1) { Delivery = DiagnosticDelivery.Complete });
            request.Record(new(DiagnosticEventKind.Finished, 2.2));
        }
        public void Dispose()
        {
            View.OnNavigatedFrom(); _workspace.Shutdown(); Logger.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
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
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Log page tests timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
