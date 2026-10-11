using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Storage;
using Xunit;

namespace RetryProxy.Tests;

public sealed class RuntimeLogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retry-proxy-runtime-tests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 31, 23, 59, 0, TimeSpan.Zero));
    private ProxyLogger Logger(RuntimeLogOptions? options = null) => ProxyLogger.Silent(_root, _clock, options);
    public void Dispose()
    {
        if (Directory.Exists(_root) && Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "retry-proxy-runtime-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(_root, true);
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Current = now;
        public override DateTimeOffset GetUtcNow() => Current;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task CrossingMidnightPreservesFullRequestIdentityAndOriginalRequestDateAfterRestart()
    {
        var id = Guid.NewGuid().ToString("N");
        using (var logger = Logger())
        {
            var request = logger.Route("通道").ForRequest(id, false, false);
            request.Info($"[{id}] 开始");
            _clock.Current = _clock.Current.AddMinutes(2);
            request.Info($"[{id}] 完成");
        }
        using var reopened = Logger();
        var first = Assert.Single((await reopened.Store.QueryAsync(new(new(2026, 10, 31)))).Entries);
        var last = Assert.Single((await reopened.Store.QueryAsync(new(new(2026, 11, 1)))).Entries);
        Assert.Equal(id, first.RequestId);
        Assert.Equal(id, last.RequestId);
        Assert.Equal(new DateOnly(2026, 10, 31), last.RequestDate);
        Assert.Contains("[请求 " + id[..8] + "]", last.Line);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "runtime"), "*.db").Length);
    }

    [Fact]
    public async Task PaginationKeepsSnapshotWhileNewLogsArriveAndExportUsesFullIds()
    {
        using var logger = Logger();
        var id = Guid.NewGuid().ToString("N");
        var request = logger.Route("alpha").ForRequest(id, false, false);
        for (var i = 1; i <= 6; i++) request.Info($"[{id}] message-{i}");
        var query = new RuntimeLogQuery(logger.Store.Today) { PageSize = 2, Search = id };
        var first = await logger.Store.QueryAsync(query);
        request.Info($"[{id}] message-7");
        var older = await logger.Store.QueryAsync(query with { Offset = 2, SnapshotId = first.SnapshotId });
        Assert.Equal(6, older.Total);
        Assert.Contains("message-3", older.Entries[0].Line);
        Assert.Contains("message-4", older.Entries[1].Line);
        using var output = new StringWriter();
        await logger.Store.ExportAsync(query with { SnapshotId = first.SnapshotId }, output);
        Assert.Contains("[请求 " + id + "]", output.ToString());
        Assert.DoesNotContain("message-7", output.ToString());
        Assert.Equal(0, (await logger.Store.QueryAsync(query with { Search = "' OR 1=1 --" })).Total);
    }

    [Fact]
    public async Task StartupWaitsForConfiguredRetentionAndExpiredCleanupKeepsStatisticsAndOtherFiles()
    {
        using (var logger = Logger(new() { DeferCleanup = true })) logger.Info("old");
        _clock.Current = _clock.Current.AddDays(8);
        var old = Path.Combine(_root, "runtime", "2026-10-31.db");
        using var current = Logger(new() { DeferCleanup = true });
        await current.Store.FlushAsync();
        Assert.True(File.Exists(old));
        current.Store.RetentionDays = 30;
        await current.Store.FlushAsync();
        Assert.True(File.Exists(old));
        Directory.CreateDirectory(Path.Combine(_root, "statistics"));
        var statistics = Path.Combine(_root, "statistics", "2026-10.db");
        File.WriteAllText(statistics, "untouched");
        var notes = Path.Combine(_root, "runtime", "notes.db");
        File.WriteAllText(notes, "untouched");
        current.Store.RetentionDays = 7;
        await current.Store.CleanupNowAsync();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(notes));
        Assert.Equal("untouched", File.ReadAllText(statistics));
    }

    [Fact]
    public async Task DatabaseFailureWritesEmergencyTextAndLaterRecovers()
    {
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "runtime");
        File.WriteAllText(blocked, "occupied");
        using var logger = Logger();
        logger.Error("failure-original-line");
        await logger.Store.FlushAsync();
        Assert.Contains("failure-original-line", File.ReadAllText(Path.Combine(_root, "runtime-emergency.log")));
        Assert.NotNull(logger.Store.Warning);
        File.Delete(blocked);
        logger.Info("recovered");
        var page = await logger.Store.QueryAsync(new(logger.Store.Today));
        Assert.Contains("recovered", Assert.Single(page.Entries).Line);
        Assert.Contains("1 条", page.Warning);
    }

    [Fact]
    public async Task FullQueueDoesNotWaitForBlockedDiskAndReportsMissingRecords()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var logger = Logger(new()
        {
            QueueCapacity = 1, BatchSize = 1,
            BeforeWriteForTest = async () => { entered.TrySetResult(); await release.Task; },
        });
        try
        {
            logger.Info("first");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => { logger.Info("second"); logger.Info("dropped"); }).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Contains("1 条", logger.Store.Warning);
        }
        finally { release.TrySetResult(); }
        var page = await logger.Store.QueryAsync(new(logger.Store.Today));
        Assert.Equal(2, page.Total);
        Assert.DoesNotContain(page.Entries, row => row.Line.Contains("dropped"));
    }

    [Fact]
    public async Task LegacyTextImportsMultilineRecordsOnceAndKeepsReadableBackup()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "retry-proxy.log");
        const string text = "2026-10-31 10:00:00 INFO [系统] old\ncontinuation\n2026-10-31 11:00:00 WARNING [通道代理][alpha][请求 12345678] old request\n";
        File.WriteAllText(path, text);
        using (var logger = Logger())
        {
            var page = await logger.Store.QueryAsync(new(logger.Store.Today));
            Assert.Equal(2, page.Total);
            Assert.Contains("old\ncontinuation", page.Entries[0].Line);
            Assert.Null(page.Entries[1].RequestId);
        }
        Assert.Equal(text, File.ReadAllText(path + ".migrated.bak"));
        File.Copy(path + ".migrated.bak", path);
        using var reopened = Logger();
        Assert.Equal(2, (await reopened.Store.QueryAsync(new(reopened.Store.Today))).Total);
    }

    [Fact]
    public async Task InterruptedTextMigrationWarnsAfterNewWritesAndResumesWithoutDuplicates()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "retry-proxy.log");
        File.WriteAllLines(path, Enumerable.Range(0, 300).Select(i => $"2026-10-31 10:00:00 INFO [系统] legacy-{i}"));
        var batch = 0;
        using (var logger = Logger(new()
        {
            BeforeWriteForTest = () => ++batch == 2 ? Task.FromException(new IOException("migration interrupted")) : Task.CompletedTask,
        }))
        {
            logger.Info("new-write-after-interruption");
            var page = await logger.Store.QueryAsync(new(logger.Store.Today));
            Assert.Equal(257, page.Total);
            Assert.Contains("旧运行日志迁移失败", page.Warning);
            Assert.True(File.Exists(path));
        }
        using var reopened = Logger();
        var restored = await reopened.Store.QueryAsync(new(reopened.Store.Today));
        Assert.Equal(301, restored.Total);
        Assert.Null(restored.Warning);
        Assert.True(File.Exists(path + ".migrated.bak"));
    }

    [Fact]
    public async Task InitialStatisticsRestoreUsesFullIdsAndMigratedTextWithoutDuplicateRequests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "retry-proxy.log"),
            "2026-10-31 10:00:00 INFO [通道代理][alpha][请求 87654321] POST /v1/responses -> HTTP 200\n");
        const string first = "12345678aaaaaaaaaaaaaaaaaaaaaaaa";
        const string second = "12345678bbbbbbbbbbbbbbbbbbbbbbbb";
        using (var logger = Logger())
        {
            foreach (var id in new[] { first, second })
                logger.Route("alpha").ForRequest(id, false, false).Info($"[{id}] POST /v1/responses -> HTTP 200，模型 test，输入/输出 10/2 token");
            logger.Route("alpha").ForRequest(Guid.NewGuid().ToString("N"), true, false).Info("POST /v1/responses -> HTTP 200");
            await logger.Store.FlushAsync();
        }
        using var data = new DataDatabase(_root);
        var storage = new SqliteDailyStorage(data, "route-alpha", "alpha");
        var restored = storage.Open(new(2026, 10, 31), importLegacy: true);
        Assert.True(restored.RestoredFromLegacyLogs);
        Assert.Equal(new[] { first, second, "87654321" }, restored.Records.Keys.OrderBy(id => id));
        Assert.All(restored.Records.Values, row => Assert.Equal(RequestOutcome.Success, row.Outcome));
        Assert.Equal(3, storage.Open(new(2026, 10, 31), importLegacy: true).Records.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3651)]
    public async Task InvalidRetentionFallsBackToSevenDaysWithoutDeletingRecentHistory(int configured)
    {
        using (var logger = Logger()) logger.Info("six-days-old");
        _clock.Current = _clock.Current.AddDays(6);
        using var current = Logger(new() { DeferCleanup = true });
        current.Store.RetentionDays = RuntimeLogStore.NormalizeRetentionDays(configured);
        await current.Store.CleanupNowAsync();
        Assert.Equal(7, current.Store.RetentionDays);
        Assert.True(File.Exists(Path.Combine(_root, "runtime", "2026-10-31.db")));
    }

    [Fact]
    public async Task MissingDateQueryDoesNotCreateEmptyDatabase()
    {
        using var logger = Logger();
        Assert.Empty((await logger.Store.QueryAsync(new(logger.Store.Today))).Entries);
        Assert.False(Directory.Exists(Path.Combine(_root, "runtime")));
    }

    [Fact]
    public async Task DisposeFlushesAcceptedRecords()
    {
        using (var logger = Logger()) for (var i = 0; i < 100; i++) logger.Info("shutdown-" + i);
        using var reopened = Logger();
        Assert.Equal(100, (await reopened.Store.QueryAsync(new(reopened.Store.Today))).Total);
    }
}
