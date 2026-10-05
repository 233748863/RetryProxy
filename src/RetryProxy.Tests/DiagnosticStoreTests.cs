using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Config;
using RetryProxy.Core.Diagnostics;
using RetryProxy.Core.Storage;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>
/// 请求诊断迁库（logs\data.db 的 diagnostic_events / diagnostic_marks）：写入、重放、详情、保留、
/// 容量与损坏、写失败。覆盖与旧 jsonl 版相同的语义：追加行 → 事件行、文件头控制记录 → 标记表、
/// 行号游标 → id 游标、文件字节上限 → payload_bytes 汇总、末行覆盖 → 唯一约束幂等。
/// </summary>
public sealed class DiagnosticStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-proxy-diagnostics-tests", Guid.NewGuid().ToString("N"));
    private readonly List<DataDatabase> _databases = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions RowJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public DiagnosticStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var data in _databases) data.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private DataDatabase Data()
    {
        var data = new DataDatabase(_directory);
        _databases.Add(data);
        return data;
    }

    private DiagnosticStore Store(DiagnosticStoreOptions? options = null) => new(Data(), _clock, options);

    private DiagnosticRequestInfo Info(string? id = null, ClientType client = ClientType.Codex, int seconds = 0)
        => new(id ?? Guid.NewGuid().ToString("N"), client, DateOnly.FromDateTime(_clock.GetLocalNow().DateTime),
            _clock.GetLocalNow().AddSeconds(seconds), "POST", "/v1/responses");

    private DiagnosticQuery Query(ClientType client = ClientType.Codex) => new(DateOnly.FromDateTime(_clock.GetLocalNow().DateTime), client);

    private static DiagnosticTarget Target(string provider = "provider-a", string key = "key-a", string model = "model-a")
        => new(provider, "历史供应商 " + provider, key, "历史账号 " + key, model);

    private static void Send(IDiagnosticRequest request, DiagnosticTarget? target = null, ulong retryCount = 0)
        => request.Record(new DiagnosticEntry(DiagnosticEventKind.SendStarted, 1) { Target = target ?? Target(), RetryCount = retryCount });

    private static void Complete(IDiagnosticRequest request, DiagnosticOutcome outcome = DiagnosticOutcome.Success)
    {
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Outcome, 3) { Outcome = outcome, FirstContentSeconds = 1.25 });
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Delivery, 3.5) { Delivery = DiagnosticDelivery.Complete });
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Finished, 4));
    }

    private long Scalar(string sql)
    {
        using var connection = Data().Database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Exec(string sql)
    {
        using var connection = Data().Database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string StoredText()
    {
        using var connection = Data().Database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(entry_json, '') || ' ' || COALESCE(request_json, '') FROM diagnostic_events ORDER BY id;";
        using var reader = command.ExecuteReader();
        var builder = new StringBuilder();
        while (reader.Read()) builder.AppendLine(reader.GetString(0));
        return builder.ToString();
    }

    private void SeedEvent(string session, string requestId, DateOnly date, long sequence, DiagnosticEntry entry, DiagnosticRequestInfo? request)
    {
        var entryJson = JsonSerializer.Serialize(entry, RowJson);
        var requestJson = request is null ? null : JsonSerializer.Serialize(request, RowJson);
        var payload = Encoding.UTF8.GetByteCount(entryJson) + (requestJson is null ? 0 : Encoding.UTF8.GetByteCount(requestJson));
        using var connection = Data().Database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO diagnostic_events (session_id, request_id, date, sequence, kind, entry_json, request_json, payload_bytes, created_at_ms)
            VALUES ($session, $request, $date, $sequence, $kind, $entry, $requestJson, $payload, $created);
            """;
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$request", requestId);
        command.Parameters.AddWithValue("$date", DiagnosticStore.DateText(date));
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$kind", DiagnosticStore.KindText(entry.Kind));
        command.Parameters.AddWithValue("$entry", entryJson);
        command.Parameters.AddWithValue("$requestJson", (object?)requestJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$created", _clock.GetUtcNow().ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    /// <summary>阻塞的假写入端：进入后等待释放，再真正执行写动作（验证队列溢出与退出等待）。</summary>
    private static Func<Action<SqliteConnection>, Task> BlockingWrite(DataDatabase data, TaskCompletionSource entered, Task release)
        => async action =>
        {
            entered.TrySetResult();
            await release.ConfigureAwait(false);
            using var connection = data.Database.Connect();
            action(connection);
        };

    /// <summary>统计写入的假写入端：库中已有事件行时发出信号（验证定时与批量落库）。</summary>
    private static Func<Action<SqliteConnection>, Task> SignalingWrite(DataDatabase data, TaskCompletionSource flushed)
        => action =>
        {
            using var connection = data.Database.Connect();
            action(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM diagnostic_events;";
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) flushed.TrySetResult();
            return Task.CompletedTask;
        };

    [Fact]
    public async Task CompletedHistoryRestoresExactObservedResultDeliveryCountersAndFullIdentity()
    {
        var info = Info();
        string session;
        using (var store = Store())
        {
            session = store.SessionIdForTest;
            var request = Assert.IsAssignableFrom<IDiagnosticRequest>(store.Begin(info));
            Send(request);
            request.Record(new DiagnosticEntry(DiagnosticEventKind.CompatibilityResend, 1.1));
            Send(request, retryCount: 7);
            request.Record(new DiagnosticEntry(DiagnosticEventKind.ResponseHeaders, 2) { StatusCode = 500 });
            Complete(request);
            var summary = Assert.Single((await store.QueryAsync(Query())).Items);
            Assert.Equal(DiagnosticOutcome.Success, summary.Outcome);
            Assert.Equal(DiagnosticDelivery.Complete, summary.Delivery);
            Assert.Equal(2UL, summary.SendCount);
            Assert.Equal(7UL, summary.RetryCount);
            Assert.Equal(1.25, summary.FirstContentSeconds);
            Assert.Equal(4, summary.TotalSeconds);
            Assert.Equal(500, summary.StatusCode);
            Assert.False(summary.PreviousSession);
            Assert.False(summary.Incomplete);
        }
        using var restored = Store();
        var result = await restored.QueryAsync(Query());
        var recovered = Assert.Single(result.Items);
        Assert.Equal(info.RequestId, recovered.Request.RequestId);
        Assert.True(recovered.PreviousSession);
        Assert.False(recovered.Incomplete);
        Assert.Null(result.Warning);
        var detail = await restored.ReadDetailAsync(info.Date, info.RequestId);
        Assert.All(detail.Events, value => Assert.Equal(session, value.SessionId));
        var text = StoredText();
        Assert.Contains("\"kind\":\"sendStarted\"", text);
        Assert.Contains("\"outcome\":\"success\"", text);
        Assert.Contains(info.RequestId, text);
        Assert.DoesNotContain("\"label\"", text);
    }

    [Fact]
    public async Task InProgressSummaryUpdatesOutcomeAndDeliveryIndependentlyWithoutInferringFromStatus()
    {
        using var store = Store();
        var request = store.Begin(Info())!;
        request.Record(new DiagnosticEntry(DiagnosticEventKind.ResponseHeaders, 1) { StatusCode = 200 });
        Assert.Equal(DiagnosticOutcome.Pending, Assert.Single((await store.QueryAsync(Query())).Items).Outcome);
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Outcome, 2) { Outcome = DiagnosticOutcome.Failure, Reason = "响应未完成" });
        var outcome = Assert.Single((await store.QueryAsync(Query())).Items);
        Assert.Equal(DiagnosticOutcome.Failure, outcome.Outcome);
        Assert.Equal(DiagnosticDelivery.NotStarted, outcome.Delivery);
        Assert.Null(outcome.TotalSeconds);
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Delivery, 3) { Delivery = DiagnosticDelivery.Interrupted });
        var delivery = Assert.Single((await store.QueryAsync(Query())).Items);
        Assert.Equal(DiagnosticDelivery.Interrupted, delivery.Delivery);
        Assert.Equal("响应未完成", delivery.FailureReason);
        Assert.False(delivery.Finished);
        Assert.Null(delivery.TotalSeconds);
    }

    [Fact]
    public async Task RestartKeepsObservedSuccessWithUnknownDeliveryAndNeverUsesRestartTimeAsDuration()
    {
        var success = Info();
        var unknown = Info(seconds: 1);
        using (var store = Store())
        {
            var first = store.Begin(success)!;
            first.Record(new DiagnosticEntry(DiagnosticEventKind.Outcome, 2.5) { Outcome = DiagnosticOutcome.Success });
            var second = store.Begin(unknown)!;
            second.Record(new DiagnosticEntry(DiagnosticEventKind.WaitingGeneration, 11));
        }
        _clock.Advance(TimeSpan.FromHours(3));
        using var restarted = Store();
        var results = (await restarted.QueryAsync(Query())).Items;
        var savedSuccess = Assert.Single(results, item => item.Request.RequestId == success.RequestId);
        Assert.Equal(DiagnosticOutcome.Success, savedSuccess.Outcome);
        Assert.Equal(DiagnosticDelivery.Unknown, savedSuccess.Delivery);
        Assert.True(savedSuccess.Incomplete);
        Assert.True(savedSuccess.PreviousSession);
        Assert.Null(savedSuccess.TotalSeconds);
        var savedUnknown = Assert.Single(results, item => item.Request.RequestId == unknown.RequestId);
        Assert.Equal(DiagnosticOutcome.Unknown, savedUnknown.Outcome);
        Assert.True(savedUnknown.Incomplete);
        Assert.Equal(11, savedUnknown.LastElapsedSeconds);
        Assert.Null(savedUnknown.TotalSeconds);
    }

    [Fact]
    public async Task FinishedWithoutOutcomeIsUnknownAndRepeatedFinishingDoesNotChangeOriginalResult()
    {
        using var store = Store();
        var request = store.Begin(Info())!;
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Finished, 2));
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Outcome, 3) { Outcome = DiagnosticOutcome.Success });
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Finished, 99));
        var item = Assert.Single((await store.QueryAsync(Query())).Items);
        Assert.True(item.Finished);
        Assert.True(item.Incomplete);
        Assert.Equal(DiagnosticOutcome.Unknown, item.Outcome);
        Assert.Equal(2, item.TotalSeconds);
    }

    [Fact]
    public async Task ConcurrentCallbacksHaveStrictMonotonicPerRequestSequenceAndCountActualSends()
    {
        using var store = Store();
        var info = Info();
        var request = store.Begin(info)!;
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => Send(request))));
        Complete(request);
        var first = await store.ReadDetailAsync(info.Date, info.RequestId);
        var second = await store.ReadDetailAsync(info.Date, info.RequestId, 100);
        var third = await store.ReadDetailAsync(info.Date, info.RequestId, 200);
        var all = first.Events.Concat(second.Events).Concat(third.Events).ToArray();
        Assert.Equal(204, all.Length);
        Assert.Equal(Enumerable.Range(1, 204).Select(value => (long)value), all.Select(value => value.Sequence));
        Assert.Equal(200UL, first.Summary!.SendCount);
        Assert.Equal(0UL, first.Summary.RetryCount);
        Assert.False(first.Summary.Incomplete);
    }

    [Fact]
    public async Task SameShortPrefixDoesNotMergeFullUuidsAndPrefixSearchReturnsBoth()
    {
        using var store = Store();
        var first = Info("12345678aaaaaaaaaaaaaaaaaaaaaaaa");
        var second = Info("12345678bbbbbbbbbbbbbbbbbbbbbbbb", seconds: 1);
        Complete(store.Begin(first)!);
        Complete(store.Begin(second)!);
        var both = await store.QueryAsync(Query() with { RequestId = "12345678" });
        Assert.Equal(2, both.Items.Count);
        Assert.Equal(second.RequestId, both.Items[0].Request.RequestId);
        var exact = await store.QueryAsync(Query() with { RequestId = first.RequestId.ToUpperInvariant() });
        Assert.Equal(first.RequestId, Assert.Single(exact.Items).Request.RequestId);
        Assert.Equal(first.RequestId, (await store.ReadDetailAsync(first.Date, first.RequestId)).Summary!.Request.RequestId);
    }

    [Fact]
    public async Task ProviderKeyModelFiltersMustMatchSameActualSendAndKeepHistoricalNames()
    {
        var info = Info();
        using (var store = Store())
        {
            var request = store.Begin(info)!;
            Send(request, Target("deleted-a", "same-key", "model-a"));
            Send(request, Target("deleted-b", "same-key", "model-b"));
            request.Record(new DiagnosticEntry(DiagnosticEventKind.Switched, 2) { Target = Target("never-sent", "other", "model-c") });
            Complete(request);
            var otherClient = store.Begin(Info(client: ClientType.Claude))!;
            Send(otherClient, Target("claude-only", "claude-key", "claude-model"));
            Complete(otherClient);
        }
        using var restored = Store();
        Assert.Single((await restored.QueryAsync(Query() with { ProviderId = "deleted-a", KeyId = "same-key", Model = "model-a" })).Items);
        Assert.Empty((await restored.QueryAsync(Query() with { ProviderId = "deleted-a", KeyId = "same-key", Model = "model-b" })).Items);
        Assert.Empty((await restored.QueryAsync(Query() with { ProviderId = "never-sent" })).Items);
        var filters = (await restored.QueryAsync(Query())).Filters;
        Assert.Equal(2, filters.Providers.Count);
        Assert.Contains(filters.Providers, choice => choice.Name == "历史供应商 deleted-a");
        Assert.Equal(2, filters.Keys.Count);
        Assert.Contains(filters.Keys, choice => choice.Id == "same-key" && choice.ProviderId == "deleted-a");
        Assert.Contains(filters.Keys, choice => choice.Id == "same-key" && choice.ProviderId == "deleted-b");
        Assert.DoesNotContain("model-c", filters.Models);
        Assert.DoesNotContain("claude-model", filters.Models);
    }

    [Fact]
    public async Task ListAndDetailClampToOneHundredWithHasMoreAndDoNotLoseEvictedLiveHistory()
    {
        using var store = Store(new DiagnosticStoreOptions { MaxLiveRequests = 2 });
        for (var index = 0; index < 201; index++) Complete(store.Begin(Info(seconds: index))!);
        var first = await store.QueryAsync(Query() with { PageSize = 1000 });
        var second = await store.QueryAsync(Query() with { Offset = 100 });
        var last = await store.QueryAsync(Query() with { Offset = 200 });
        Assert.Equal(100, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Equal(100, second.Items.Count);
        Assert.True(second.HasMore);
        Assert.Single(last.Items);
        Assert.False(last.HasMore);
        Assert.Equal(2, store.LiveCountForTest);
        Assert.All(first.Items.Concat(second.Items).Concat(last.Items), value => Assert.False(value.Incomplete));
        Assert.Equal(1, store.CachedDateCountForTest);
        var info = Info(seconds: 300);
        var longRequest = store.Begin(info)!;
        for (var index = 0; index < 250; index++) Send(longRequest);
        Complete(longRequest);
        var detail = await store.ReadDetailAsync(info.Date, info.RequestId, pageSize: 10000);
        Assert.Equal(100, detail.Events.Count);
        Assert.True(detail.HasMore);
        Assert.False((await store.ReadDetailAsync(info.Date, info.RequestId, 200)).HasMore);
    }

    [Fact]
    public async Task IncrementalRefreshReadsOnlyNewBytesAndDetailReadsOnlySelectedOffsets()
    {
        long readBytes = 0;
        using var store = Store(new DiagnosticStoreOptions { BytesReadForTest = count => Interlocked.Add(ref readBytes, count) });
        var info = Info();
        var request = store.Begin(info)!;
        for (var index = 0; index < 600; index++) Send(request);
        await store.FlushAsync();
        var total = Scalar($"SELECT COALESCE(SUM(payload_bytes), 0) FROM diagnostic_events WHERE date = '{DiagnosticStore.DateText(info.Date)}';");
        await store.QueryAsync(Query());
        Assert.Equal(total, Interlocked.Read(ref readBytes));
        await store.QueryAsync(Query());
        Assert.Equal(total, Interlocked.Read(ref readBytes));
        Send(request, retryCount: 99);
        var updated = Assert.Single((await store.QueryAsync(Query())).Items);
        var increment = Interlocked.Read(ref readBytes) - total;
        var lastPayload = Scalar("SELECT payload_bytes FROM diagnostic_events ORDER BY id DESC LIMIT 1;");
        Assert.Equal(lastPayload, increment);
        Assert.Equal(99UL, updated.RetryCount);
        var beforeDetail = Interlocked.Read(ref readBytes);
        var detail = await store.ReadDetailAsync(info.Date, info.RequestId, 200);
        Assert.Equal(100, detail.Events.Count);
        Assert.InRange(Interlocked.Read(ref readBytes) - beforeDetail, 1, 100 * 2048);
        Assert.Equal(201, detail.Events[0].Sequence);
    }

    [Fact]
    public async Task StartupRetainsExactlyTodayAndPreviousSixDaysAndDeletesOlderRows()
    {
        for (var ago = 0; ago < 9; ago++)
        {
            var date = Query().Date.AddDays(-ago);
            var info = Info() with { Date = date, StartedAt = _clock.GetUtcNow().AddDays(-ago) };
            SeedEvent(Guid.NewGuid().ToString("N"), info.RequestId, date, 1, new DiagnosticEntry(DiagnosticEventKind.Started, 0), info);
        }
        using var store = Store();
        await store.FlushAsync();
        Assert.Equal(7, Scalar("SELECT COUNT(*) FROM diagnostic_events;"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM diagnostic_events WHERE date = '{DiagnosticStore.DateText(Query().Date.AddDays(-6))}';"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM diagnostic_events WHERE date < '{DiagnosticStore.DateText(Query().Date.AddDays(-6))}';"));
        Assert.Single((await store.QueryAsync(Query() with { Date = Query().Date.AddDays(-6) })).Items);
        Assert.Empty((await store.QueryAsync(Query() with { Date = Query().Date.AddDays(-7) })).Items);
        Assert.Null(store.Begin(Info() with { Date = Query().Date.AddDays(-7) }));
        Assert.Equal(1, store.CachedDateCountForTest);
    }

    [Fact]
    public async Task CrossingMidnightKeepsAcceptanceDayAndExpiredActiveRequestCannotRecreateOldRows()
    {
        _clock.Set(new DateTimeOffset(2026, 10, 3, 23, 59, 30, TimeSpan.Zero));
        using var store = Store();
        var info = Info();
        var request = store.Begin(info)!;
        Send(request);
        await store.FlushAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        Send(request, retryCount: 2);
        await store.FlushAsync();
        Assert.Single((await store.QueryAsync(Query() with { Date = info.Date })).Items);
        Assert.Empty((await store.QueryAsync(Query())).Items);
        _clock.Advance(TimeSpan.FromDays(6));
        Send(request, retryCount: 3);
        await store.FlushAsync();
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM diagnostic_events WHERE date = '{DiagnosticStore.DateText(info.Date)}';"));
        Assert.Empty((await store.QueryAsync(Query() with { Date = info.Date })).Items);
        Assert.Equal(0, store.LiveCountForTest);
    }

    [Fact]
    public async Task LocalTimeZoneRatherThanUtcDecidesToday()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 3, 18, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.CreateCustomTimeZone("Test+8", TimeSpan.FromHours(8), "Test+8", "Test+8"));
        using var store = new DiagnosticStore(Data(), clock);
        Assert.Equal(new DateOnly(2026, 10, 4), store.Today);
        await store.FlushAsync();
    }

    [Fact]
    public async Task QueueOverflowDoesNotWaitForWriterAndPersistsSafeIncompleteMark()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var info = Info();
        var data = Data();
        using (var store = new DiagnosticStore(data, _clock, new DiagnosticStoreOptions
        {
            QueueCapacity = 2,
            WriteForTest = BlockingWrite(data, entered, release.Task),
        }))
        {
            var request = store.Begin(info)!;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await Task.Run(() =>
                {
                    for (var index = 0; index < 30; index++) Send(request, retryCount: 8);
                    Complete(request);
                }).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(DiagnosticStore.IncompleteWarning, store.Warning);
            }
            finally { release.TrySetResult(); }
            var summary = Assert.Single((await store.QueryAsync(Query())).Items);
            Assert.Equal(DiagnosticOutcome.Success, summary.Outcome);
            Assert.Equal(30UL, summary.SendCount);
            Assert.Equal(8UL, summary.RetryCount);
            Assert.True(summary.Incomplete);
        }
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM diagnostic_marks;"));
        using var restored = Store();
        Assert.True(Assert.Single((await restored.QueryAsync(Query())).Items).Incomplete);
    }

    [Fact]
    public async Task EventByteLimitAndDailyLimitIncludeAllSessions()
    {
        using (var store = Store(new DiagnosticStoreOptions { MaxEventBytes = 256 }))
        {
            // 单条事件上限按清洗后的落库字节判定：长供应商名（64）+ 长 Key 名（64）+ 长模型（128）确定超过 256。
            var longName = new string('a', 64);
            var longModel = new string('b', 128);
            var request = store.Begin(Info())!;
            Send(request, new DiagnosticTarget("provider-a", longName, "key-a", longName, longModel));
            Complete(request);
            var page = await store.QueryAsync(Query());
            Assert.Equal(DiagnosticStore.IncompleteWarning, page.Warning);
            Assert.True(Assert.Single(page.Items).Incomplete);
            Assert.InRange(Scalar("SELECT COALESCE(MAX(payload_bytes), 0) FROM diagnostic_events;"), 1, 256);
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM diagnostic_events WHERE entry_json LIKE '%{longModel}%';"));
        }
        for (var session = 0; session < 2; session++)
        {
            using var store = Store(new DiagnosticStoreOptions { MaxDailyBytes = 2048 });
            var request = store.Begin(Info())!;
            for (var index = 0; index < 30; index++) Send(request);
            Complete(request);
            await store.FlushAsync();
            Assert.Equal(DiagnosticStore.IncompleteWarning, store.Warning);
            Assert.InRange(Scalar("SELECT COALESCE(SUM(payload_bytes), 0) FROM diagnostic_events;"), 1, 2048);
        }
        Assert.InRange(Scalar("SELECT COUNT(*) FROM diagnostic_marks;"), 1, 3);
    }

    [Fact]
    public async Task IoFailureAndExceptionMessagesCannotEscapeOrHideLatestObservedOutcome()
    {
        var blockedDirectory = Path.Combine(_directory, "private-path-secret");
        File.WriteAllText(blockedDirectory, "file-blocks-directory");
        using var store = new DiagnosticStore(new DataDatabase(blockedDirectory), _clock);
        var request = store.Begin(Info())!;
        Complete(request);
        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var page = await store.QueryAsync(Query());
        Assert.Equal(DiagnosticStore.IncompleteWarning, page.Warning);
        var result = Assert.Single(page.Items);
        Assert.Equal(DiagnosticOutcome.Success, result.Outcome);
        Assert.True(result.Incomplete);
        Assert.DoesNotContain("private-path-secret", page.Warning!);
    }

    [Fact]
    public async Task ChangedSubscribersCannotThrowOrBlockCollectionAndWriter()
    {
        using var store = Store();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        store.Changed += () => throw new InvalidOperationException("must-not-escape");
        store.Changed += () => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); };
        try
        {
            var request = store.Begin(Info())!;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Complete(request);
            await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(DiagnosticOutcome.Success, Assert.Single((await store.QueryAsync(Query())).Items).Outcome);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task QueriesAndFlushHonorCancellationEvenWhileWriterIsBlocked()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = Data();
        using var store = new DiagnosticStore(data, _clock, new DiagnosticStoreOptions { QueueCapacity = 1, WriteForTest = BlockingWrite(data, entered, release.Task) });
        var info = Info();
        var request = store.Begin(info)!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Send(request);
            using var cancellation = new CancellationTokenSource();
            var query = store.QueryAsync(Query(), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadDetailAsync(info.Date, info.RequestId, cancellationToken: cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.FlushAsync(cancellation.Token));
        }
        finally { release.TrySetResult(); }
        Assert.Single((await store.QueryAsync(Query())).Items);
    }

    [Fact]
    public async Task CancellationDuringIncrementalReadDoesNotDuplicateEventsOnNextQuery()
    {
        var info = Info();
        using (var writer = Store())
        {
            var request = writer.Begin(info)!;
            for (var index = 0; index < 200; index++) Send(request);
            Complete(request);
        }
        using var cancellation = new CancellationTokenSource();
        var cancelOnce = 0;
        using var store = Store(new DiagnosticStoreOptions
        {
            BytesReadForTest = _ => { if (Interlocked.Exchange(ref cancelOnce, 1) == 0) cancellation.Cancel(); },
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.QueryAsync(Query(), cancellation.Token));
        var item = Assert.Single((await store.QueryAsync(Query())).Items);
        Assert.Equal(200UL, item.SendCount);
        Assert.False(item.Incomplete);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("bad-json")]
    [InlineData("partial")]
    [InlineData("oversized")]
    [InlineData("kind")]
    public async Task CorruptHistoryIsIncompleteButObservedSuccessIsNotReclassifiedAsFailure(string corruption)
    {
        using (var writer = Store())
        {
            var request = writer.Begin(Info())!;
            Send(request);
            Complete(request);
        }
        switch (corruption)
        {
            case "gap": Exec("DELETE FROM diagnostic_events WHERE sequence = 2;"); break;
            case "bad-json": Exec("UPDATE diagnostic_events SET entry_json = '{broken-json' WHERE sequence = 2;"); break;
            case "partial": Exec("DELETE FROM diagnostic_events WHERE sequence = 5;"); break;
            case "oversized": Exec("UPDATE diagnostic_events SET payload_bytes = 5000 WHERE sequence = 2;"); break;
            case "kind": Exec("UPDATE diagnostic_events SET kind = 'bogus' WHERE sequence = 2;"); break;
        }
        using var store = Store();
        var page = await store.QueryAsync(Query());
        var summary = Assert.Single(page.Items);
        Assert.Equal(DiagnosticOutcome.Success, summary.Outcome);
        Assert.True(summary.Incomplete);
        Assert.Equal(DiagnosticStore.IncompleteWarning, page.Warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateEventRowsAreIgnoredOrRejectedAndKeepCompleteHistory(bool ignoreDuplicates)
    {
        var info = Info();
        using (var writer = Store()) { var request = writer.Begin(info)!; Send(request); Complete(request); }
        var insert = ignoreDuplicates ? "INSERT OR IGNORE" : "INSERT";
        var statement = $"{insert} INTO diagnostic_events (session_id, request_id, date, sequence, kind, entry_json, request_json, payload_bytes, created_at_ms) SELECT session_id, request_id, date, sequence, kind, entry_json, request_json, payload_bytes, created_at_ms FROM diagnostic_events WHERE sequence = 2;";
        if (ignoreDuplicates) Exec(statement);
        else Assert.Throws<SqliteException>(() => Exec(statement));
        using var store = Store();
        var summary = Assert.Single((await store.QueryAsync(Query())).Items);
        Assert.Equal(1UL, summary.SendCount);
        Assert.False(summary.Incomplete);
        Assert.Equal(5, (await store.ReadDetailAsync(info.Date, info.RequestId)).Events.Count);
    }

    [Fact]
    public async Task PrivacyIsAppliedBeforePersistenceAndAgainWhenReadingUntrustedRows()
    {
        var info = Info() with { Endpoint = "https://secret.invalid/private/v1/responses?token=secret-query" };
        using (var writer = Store())
        {
            var request = writer.Begin(info)!;
            Send(request, new DiagnosticTarget("provider-a", "sk-provider-secret", "key-a", "https://secret.invalid/account", "sess-model-secret"));
            request.Record(new DiagnosticEntry(DiagnosticEventKind.SendFinished, 2)
            {
                ErrorCode = "sk-error-secret", UpstreamRequestId = "eyj-secret-header", Reason = "https://secret.invalid/raw-exception",
            });
            Complete(request);
        }
        var saved = StoredText();
        Assert.DoesNotContain("secret.invalid", saved);
        Assert.DoesNotContain("secret-query", saved);
        Assert.DoesNotContain("sk-provider-secret", saved);
        Assert.DoesNotContain("sess-model-secret", saved);
        Assert.DoesNotContain("sk-error-secret", saved);
        Assert.DoesNotContain("eyj-secret-header", saved);
        Exec("UPDATE diagnostic_events SET entry_json = replace(entry_json, '历史账号', 'sk-disk-secret');");
        using var store = Store();
        var detail = await store.ReadDetailAsync(info.Date, info.RequestId);
        Assert.DoesNotContain("sk-disk-secret", JsonSerializer.Serialize(detail, Json));
        Assert.Equal("/responses", detail.Summary!.Request.Endpoint);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task TimerFlushesWithoutQueryAndConfiguredEventBatchFlushes(int eventCount)
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = Data();
        using var store = new DiagnosticStore(data, _clock, new DiagnosticStoreOptions { FlushEventCount = 4, WriteForTest = SignalingWrite(data, flushed) });
        var request = store.Begin(Info())!;
        for (var index = 1; index < eventCount; index++) Send(request);
        await flushed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.InRange(Scalar("SELECT COUNT(*) FROM diagnostic_events;"), 1, 4);
    }

    [Fact]
    public async Task DisposeWaitsAtMostTwoSecondsAndDoesNotPerformCleanupOnCaller()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = Data();
        var store = new DiagnosticStore(data, _clock, new DiagnosticStoreOptions { WriteForTest = BlockingWrite(data, entered, release.Task) });
        var info = Info();
        var request = store.Begin(info)!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var elapsed = Stopwatch.StartNew();
            store.Dispose();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
            Assert.Null(store.Begin(Info()));
            request.Record(new DiagnosticEntry(DiagnosticEventKind.Finished, 9));
        }
        finally { release.TrySetResult(); }
        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NewSendAndExplicitMissingFirstContentDoNotKeepAnEarlierResponseFirstContent()
    {
        using var store = Store();
        var request = store.Begin(Info())!;
        Send(request);
        request.Record(new DiagnosticEntry(DiagnosticEventKind.SendFinished, 2) { FirstContentSeconds = 0.5, StatusCode = 500 });
        Assert.Equal(0.5, Assert.Single((await store.QueryAsync(Query())).Items).FirstContentSeconds);
        Send(request, retryCount: 1);
        var retry = Assert.Single((await store.QueryAsync(Query())).Items);
        Assert.Null(retry.FirstContentSeconds);
        Assert.Null(retry.StatusCode);
        request.Record(new DiagnosticEntry(DiagnosticEventKind.SendFinished, 3) { FirstContentSeconds = 1, StatusCode = 200 });
        request.Record(new DiagnosticEntry(DiagnosticEventKind.ResponseReady, 3.1));
        Assert.Null(Assert.Single((await store.QueryAsync(Query())).Items).FirstContentSeconds);
        request.Record(new DiagnosticEntry(DiagnosticEventKind.SendFinished, 3.2) { FirstContentSeconds = 2 });
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Outcome, 3.3) { Outcome = DiagnosticOutcome.Success });
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Delivery, 3.4) { Delivery = DiagnosticDelivery.Complete });
        request.Record(new DiagnosticEntry(DiagnosticEventKind.Finished, 4));
        var final = Assert.Single((await store.QueryAsync(Query())).Items);
        Assert.Null(final.FirstContentSeconds);
        Assert.Equal(DiagnosticOutcome.Success, final.Outcome);
        Assert.False(final.Incomplete);
    }

    [Fact]
    public async Task RequestMetadataIsStoredOnFirstEventOnlyAndRestoreKeepsObservedFinish()
    {
        var info = Info();
        using (var writer = Store()) { var request = writer.Begin(info)!; Send(request); Complete(request); }
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM diagnostic_events WHERE request_json IS NOT NULL;"));
        Assert.Equal(5, Scalar("SELECT COUNT(*) FROM diagnostic_events;"));
        using var store = Store();
        var item = Assert.Single((await store.QueryAsync(Query())).Items);
        Assert.False(item.Incomplete);
        Assert.Equal(DiagnosticOutcome.Success, item.Outcome);
        Assert.True(item.Finished);
        Assert.Equal(4, item.TotalSeconds);
        Assert.Equal(5, (await store.ReadDetailAsync(info.Date, info.RequestId)).Events.Count);
    }

    [Fact]
    public async Task CommitFailureDropsBatchMarksIncompleteAndLaterWritesSucceed()
    {
        var failCommits = 0;
        var data = Data();
        using (var store = new DiagnosticStore(data, _clock, new DiagnosticStoreOptions
        {
            WriteForTest = action =>
            {
                if (Volatile.Read(ref failCommits) == 1)
                {
                    Interlocked.Exchange(ref failCommits, 2);
                    throw new IOException("private-exception-must-not-escape");
                }
                using var connection = data.Database.Connect();
                action(connection);
                return Task.CompletedTask;
            },
        }))
        {
            await store.FlushAsync();
            var info = Info();
            var request = store.Begin(info)!;
            Send(request);
            Complete(request);
            Volatile.Write(ref failCommits, 1);
            await store.FlushAsync();
            Assert.Equal(DiagnosticStore.IncompleteWarning, store.Warning);
            Assert.Equal(0, Scalar("SELECT COUNT(*) FROM diagnostic_events;"));
            Assert.Equal(1, Scalar("SELECT COUNT(*) FROM diagnostic_marks;"));
            Volatile.Write(ref failCommits, 2);
            Complete(store.Begin(Info())!);
            await store.FlushAsync();
            Assert.True(Scalar("SELECT COUNT(*) FROM diagnostic_events;") > 0);
        }
    }

    [Fact]
    public async Task FailedRetentionDeletionWarnsButExpiredRowsStayExcluded()
    {
        var expired = Query().Date.AddDays(-7);
        var info = Info() with { Date = expired, StartedAt = _clock.GetUtcNow().AddDays(-7) };
        SeedEvent(Guid.NewGuid().ToString("N"), info.RequestId, expired, 1, new DiagnosticEntry(DiagnosticEventKind.Started, 0), info);
        var failOnce = 0;
        var data = Data();
        using var store = new DiagnosticStore(data, _clock, new DiagnosticStoreOptions
        {
            WriteForTest = action =>
            {
                if (Interlocked.Exchange(ref failOnce, 1) == 0) throw new IOException("retention deletion failure");
                using var connection = data.Database.Connect();
                action(connection);
                return Task.CompletedTask;
            },
        });
        await store.FlushAsync();
        Assert.Equal(DiagnosticStore.IncompleteWarning, store.Warning);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM diagnostic_events WHERE date = '{DiagnosticStore.DateText(expired)}';"));
        Assert.Empty((await store.QueryAsync(Query() with { Date = expired })).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DateExpiringDuringReadCannotEscapeTheRetentionWindow(bool detail)
    {
        var advanced = 0;
        using var store = Store(new DiagnosticStoreOptions
        {
            BytesReadForTest = _ =>
            {
                if (Interlocked.Exchange(ref advanced, 1) == 0) _clock.Advance(TimeSpan.FromDays(7));
            },
        });
        var info = Info();
        var request = store.Begin(info)!;
        Send(request);
        Complete(request);
        await store.FlushAsync();
        if (detail)
        {
            var result = await store.ReadDetailAsync(info.Date, info.RequestId);
            Assert.Null(result.Summary);
            Assert.Empty(result.Events);
        }
        else
        {
            var result = await store.QueryAsync(new DiagnosticQuery(info.Date, ClientType.Codex));
            Assert.Empty(result.Items);
        }
        Assert.Equal(1, advanced);
        Assert.Equal(0, store.CachedDateCountForTest);
    }

    [Fact]
    public async Task QueryingAnExpiredDateAlsoReleasesItsCachedMetadata()
    {
        using var store = Store();
        var info = Info();
        var request = store.Begin(info)!;
        Send(request);
        Complete(request);
        Assert.Single((await store.QueryAsync(new DiagnosticQuery(info.Date, ClientType.Codex))).Items);
        Assert.Equal(1, store.CachedDateCountForTest);
        _clock.Advance(TimeSpan.FromDays(7));
        Assert.Empty((await store.QueryAsync(new DiagnosticQuery(info.Date, ClientType.Codex))).Items);
        Assert.Equal(0, store.CachedDateCountForTest);
    }

    [Fact]
    public void DefaultsMatchApprovedLimits()
    {
        var options = new DiagnosticStoreOptions();
        Assert.Equal(4096, options.QueueCapacity);
        Assert.Equal(2048, options.MaxEventBytes);
        Assert.Equal(64L * 1024 * 1024, options.MaxDailyBytes);
        Assert.Equal(256, options.FlushEventCount);
        Assert.Equal(TimeSpan.FromSeconds(1), options.FlushInterval);
    }

    private sealed class TestClock(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
    {
        private long _ticks = now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
        public void Set(DateTimeOffset time) => Interlocked.Exchange(ref _ticks, time.UtcTicks);
    }
}
