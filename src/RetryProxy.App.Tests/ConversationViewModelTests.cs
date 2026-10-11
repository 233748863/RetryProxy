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

public sealed class ConversationViewModelTests
{
    [Fact]
    public void RefreshPreservesRowsSelectionAndPartsAndReadingPausesAutomaticUpdates() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo(); await Ready(view);
        var rows = view.Conversations.ToArray();
        view.SelectedConversation = rows[1];
        Assert.False(view.AutoRefresh);
        var request = view.SelectedRequest;
        var parts = view.Parts.ToArray();
        var changes = 0;
        foreach (var row in rows) row.PropertyChanged += (_, _) => changes++;
        foreach (var part in parts) part.PropertyChanged += (_, _) => changes++;
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(rows, view.Conversations);
        Assert.Same(request, view.SelectedRequest);
        Assert.Equal(parts, view.Parts);
        Assert.Equal(0, changes);
        Assert.Equal(6, parts.Length);
        Assert.Contains("100", view.ContextCaption);
    });

    [Fact]
    public void FiltersPaginationAndNavigationCancellationDoNotMixPreviousResults() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo(); await Ready(view);
        Assert.Equal("23", view.ConversationCount);
        Assert.Equal(20, view.Conversations.Count);
        view.NextGroupCommand.Execute(null);
        Assert.Equal(3, view.Conversations.Count);
        Assert.True(view.CanPreviousGroup);
        view.Client = "Claude Code";
        view.Client = "Codex";
        view.OnNavigatedFrom(); view.OnNavigatedTo(); await Ready(view);
        Assert.Equal("23", view.ConversationCount);
        Assert.False(view.CanPreviousGroup);
        view.SelectedConversation = view.Conversations.Single(r => r.Group.Requests.Count > 20);
        Assert.True(view.CanNextRequest);
        view.NextRequestCommand.Execute(null);
        Assert.Equal(3, view.Requests.Count);
        Assert.True(view.CanPreviousRequest);
        view.Client = "Claude Code"; await Ready(view);
        Assert.Empty(view.Conversations);
        Assert.Empty(view.Requests);
        Assert.Empty(view.Parts);
        Assert.False(view.HasSelectedRequest);
    });

    [Fact]
    public void LanguageAndPriceChangesKeepFiltersAndUseCurrentPrices() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.Period = "all";
        view.OnNavigatedTo(); await Ready(view);
        var selected = view.SelectedConversation;
        I18nService.Instance.ChangeLanguage("en");
        Assert.Equal("all", view.Period);
        Assert.Same(selected, view.SelectedConversation);
        Assert.Equal("System rules", view.Parts[0].Name);
        Assert.Equal("—", view.Cost);
        fixture.Config.Get().OtherConfig.StatisticsModelPrices.Add(new() { Model = "conversation-test", Price = new() { Rates = new() { Input = 1, Output = 2, CacheRead = 0 } } });
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.StartsWith("≈$", view.Cost);
        Assert.Same(selected, view.SelectedConversation);
    });

    private sealed class TestConfig : IConfigService
    {
        private readonly AllConfig _all = new() { Proxy = ProxyConfig.Builtin() };
        public AllConfig Get() => _all;
        public void Save() { }
        public void SaveChecked() { }
        public void BackupBeforeDeletion() { }
        public void BackupBeforeClientTakeover() { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-conversation-vm-" + Guid.NewGuid().ToString("N"));
        private readonly ProxyLogger _logger;
        private readonly WorkspaceService _workspace;
        public TestConfig Config { get; } = new();
        public ConversationPageViewModel View { get; }
        public Fixture()
        {
            I18nService.Instance.ChangeLanguage("zh-Hans");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            foreach (var route in Config.Get().Proxy!.Routes) route.DesiredRunning = false;
            _logger = ProxyLogger.Silent(_directory);
            _workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, Config, _logger, new SnackbarService());
            var routeId = Config.Get().Proxy!.RouteFor(ClientType.Codex)!.Id;
            var day = DateOnly.FromDateTime(DateTime.Today);
            var journal = new SqliteDailyStorage(_workspace.Data, routeId, "Codex").Open(day, false).Journal!;
            for (var i = 0; i < 45; i++) journal.Append(new()
            {
                RequestId = "r-" + i, Outcome = RequestOutcome.Success,
                UpdatedAtUnixMs = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, i), DateTimeKind.Local)).ToUnixTimeMilliseconds(),
                Attribution = new RequestAttribution { Client = ClientType.Codex, ProviderId = "p", Model = "conversation-test" },
                Usage = new RequestUsage { Model = "conversation-test", InputTokens = 100, CachedTokens = 0, OutputTokens = 10, InputAccounting = CacheInputAccounting.IncludesCached },
                PromptAnalysis = new PromptAnalysis { SessionHash = Math.Min(i, 22).ToString("x64"), Window = 1000,
                    Parts = Enum.GetValues<PromptPartKind>().Select(k => new PromptPart(k, (int)k + 1)).ToArray() },
            });
            View = new ConversationPageViewModel(_workspace, Config);
        }
        public void Dispose()
        {
            View.OnNavigatedFrom(); _workspace.Shutdown(); _logger.Dispose();
            I18nService.Instance.ChangeLanguage("zh-Hans"); SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private static async Task Ready(ConversationPageViewModel view)
    {
        var watch = Stopwatch.StartNew();
        while (view.IsLoading) { Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8)); await Task.Delay(10); }
        Assert.False(view.HasNotice, view.Notice);
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
                try { await test(); } catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Conversation tests timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
