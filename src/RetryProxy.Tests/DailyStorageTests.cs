using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Storage;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>
/// 对应 daily.rs 里的单元测试：SQLite 当日统计的恢复、跨日、幂等覆盖、损坏行跳过与写失败。
/// 覆盖与旧 jsonl 版相同的语义（末行覆盖 → 主键 UPSERT、文件头校验 → 结构版本校验）。
/// </summary>
public class DailyStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
    private readonly List<DataDatabase> _databases = new();

    public DailyStorageTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        foreach (var data in _databases)
        {
            data.Dispose();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DataDatabase Data()
    {
        var data = new DataDatabase(_directory);
        _databases.Add(data);
        return data;
    }

    private SqliteDailyStorage Spec(string routeId = "route", string routeName = "通道") => new(Data(), routeId, routeName);

    private static DateTime Now(int day, int hour) => new(2026, 9, day, hour, 0, 0, DateTimeKind.Local);

    private static long Millis(DateTime now) => new DateTimeOffset(now).ToUnixTimeMilliseconds();

    private static CacheRequest Cache(string id, bool claude)
    {
        return new CacheRequest
        {
            RequestId = id,
            Model = claude ? "claude-test" : "gpt-test",
            CompletedAtUnixMs = 0,
            InputTokens = claude ? 100UL : 1000UL,
            CachedTokens = 800,
            CacheCreationTokens = claude ? 100UL : null,
            InputAccounting = claude ? CacheInputAccounting.ExcludesCached : CacheInputAccounting.IncludesCached,
            CacheKeyStatus = "保持原请求",
        };
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>可以随时开始/停止抛错的假日志端：验证写失败提示与 dirty 补写。</summary>
    private sealed class FlakyStorage : IDailyStorage
    {
        public FlakyJournal Journal { get; } = new();

        public DailyJournalOpenResult Open(DateOnly date, bool importLegacy) => new() { Journal = Journal };
    }

    private sealed class FlakyJournal : IDailyJournal
    {
        public bool Failing { get; set; }

        public List<string> Appended { get; } = new();

        public void Append(DailyRequest record)
        {
            if (Failing)
            {
                throw new IOException("模拟写失败");
            }

            Appended.Add(record.RequestId);
        }
    }

    [Fact]
    public void RestartRestoresAllDailyCountsCacheAndRecentHistoryWithoutLiveRequests()
    {
        var spec = Spec("stable-route");
        var instant = Now(19, 12);
        var ms = Millis(instant);
        MetricsSnapshot before;
        using (var state = new MetricsState(instant, spec))
        {
            state.Change("gpt", new DailyChange.Started(), ms);
            state.Change("gpt", new DailyChange.CacheKey(CacheKeyState.Added), ms);
            state.Change("gpt", new DailyChange.CacheFallback(), ms);
            state.Change("gpt", new DailyChange.Retry(1), ms);
            state.Change("gpt", new DailyChange.Retry(1), ms);
            state.Change("gpt", new DailyChange.Succeeded(Cache("gpt", false)), ms);
            state.Change("gpt", new DailyChange.Succeeded(Cache("gpt", false)), ms);
            state.Change("gpt", new DailyChange.Failed(), ms);
            state.Change("claude", new DailyChange.Succeeded(Cache("claude", true)), ms + 1);
            state.Change("failure", new DailyChange.Failed(), ms);
            state.Change("unknown", new DailyChange.Started(), ms);
            before = state.Snapshot();
        }

        Assert.Equal((4UL, 2UL, 1UL, 1UL), (before.TotalRequests, before.SuccessfulRequests, before.FailedRequests, before.RetryCount));
        Assert.Equal(1UL, before.HistoricalUnfinishedRequests);
        Assert.Equal((2000UL, 1600UL, 100UL), (before.Cache.InputTokens, before.Cache.CachedTokens, before.Cache.CacheCreationTokens));
        Assert.Equal(1UL, before.Cache.AddedKeyRequests);
        Assert.Equal(1UL, before.Cache.CompatibilityFallbacks);
        Assert.Equal(1UL, before.GptCache.MeasuredRequests);
        using var restored = new MetricsState(instant, spec);
        Assert.Equal(Json(before), Json(restored.Snapshot()));
        Assert.Empty(restored.Active);
    }

    [Fact]
    public void MidnightResetsDailyTotalsAndPreservesLivePhasesAndCrossDayCompletion()
    {
        var spec = Spec();
        MetricsSnapshot today;
        using (var state = new MetricsState(Now(19, 23), spec))
        {
            state.Change("done", new DailyChange.Succeeded(Cache("done", false)), Millis(Now(19, 23)));
            state.Change("active", new DailyChange.Started(), Millis(Now(19, 23)));
            state.Change("active", new DailyChange.Retry(7), Millis(Now(19, 23)));
            state.Active["active"] = new ActiveRequest
            {
                RequestId = "active",
                Method = "POST",
                Path = "/v1/messages",
                Phase = RequestPhase.WaitingRetry,
                Attempt = 7,
            };
            state.Rollover(Now(20, 0));
            var midnight = state.Snapshot();
            Assert.Equal("2026-09-20", midnight.StatisticsDate);
            Assert.Equal((1UL, 0UL, 0UL, 1UL), (midnight.TotalRequests, midnight.RetryCount, midnight.SuccessfulRequests, midnight.ActiveRequests));
            Assert.Equal(0UL, midnight.HistoricalUnfinishedRequests);
            Assert.Empty(midnight.Cache.RecentRequests);
            Assert.Equal(RequestPhase.WaitingRetry, midnight.Requests[0].Phase);
            Assert.Equal(7UL, midnight.Requests[0].Attempt);
            state.Change("active", new DailyChange.Retry(8), Millis(Now(20, 0)));
            state.Change("active", new DailyChange.Succeeded(Cache("active", true)), Millis(Now(20, 0)));
            state.Active.Clear();
            today = state.Snapshot();
        }

        Assert.Equal((1UL, 1UL, 1UL), (today.TotalRequests, today.SuccessfulRequests, today.RetryCount));
        Assert.Equal(80.0, today.Cache.HitRatePercent());
        using (var again = new MetricsState(Now(20, 1), spec))
        {
            Assert.Equal(Json(today), Json(again.Snapshot()));
        }

        using var yesterdayState = new MetricsState(Now(19, 23), spec);
        var yesterday = yesterdayState.Snapshot();
        Assert.Equal((2UL, 1UL, 1UL), (yesterday.TotalRequests, yesterday.SuccessfulRequests, yesterday.RetryCount));
        Assert.Equal(1UL, yesterday.HistoricalUnfinishedRequests);
    }

    /// <summary>
    /// 午夜切换本身不导入运行日志；该通道在库中还没有任何记录时，重启（新的一天第一次打开）
    /// 按 S4 从运行日志恢复当天统计，且只恢复一次——导入后普通轮转与删除旧日志都不再影响统计。
    /// </summary>
    [Fact]
    public void MidnightRolloverDoesNotImportAndRestartRecoversThatDaysLogOnce()
    {
        using (var state = new MetricsState(Now(19, 23), Spec()))
        {
            File.WriteAllText(
                Path.Combine(_directory, "retry-proxy.log"),
                "2026-09-20 00:00:00 INFO [通道][abc12345] POST /v1/responses -> 上游 HTTP 200，模型 gpt-test，输入 1000 / 输出 1 token，缓存命中 800 token\n",
                new UTF8Encoding(false));
            state.Rollover(Now(20, 0));
            Assert.Equal(0UL, state.Snapshot().TotalRequests);
            Assert.False(state.Snapshot().RestoredFromLegacyLogs);
        }

        using (var after = new MetricsState(Now(20, 1), Spec()))
        {
            Assert.Equal(1UL, after.Snapshot().TotalRequests);
            Assert.True(after.Snapshot().RestoredFromLegacyLogs);
        }

        using (var again = new MetricsState(Now(20, 2), Spec()))
        {
            Assert.Equal(1UL, again.Snapshot().TotalRequests);
            // 与旧 jsonl 头一致：恢复标志按天持久化，重启后统计页仍提示"今日数据包含旧日志恢复记录"。
            Assert.True(again.Snapshot().RestoredFromLegacyLogs);
        }

        File.Delete(Path.Combine(_directory, "retry-proxy.log"));
        using var noLog = new MetricsState(Now(20, 3), Spec());
        Assert.Equal(1UL, noLog.Snapshot().TotalRequests);
    }

    [Fact]
    public void RouteRenameKeepsHistoryAndSameNamedRoutesWithOtherIdsStaySeparate()
    {
        using (var first = ProxyMetrics.FromDailyLogs(Data(), "../../id-a", "旧名"))
        {
            first.Success("one", Cache("one", false));
        }

        using var renamed = ProxyMetrics.FromDailyLogs(Data(), "../../id-a", "新名");
        Assert.Equal(1UL, renamed.Snapshot().SuccessfulRequests);
        using var another = ProxyMetrics.FromDailyLogs(Data(), "id-b", "新名");
        Assert.Equal(0UL, another.Snapshot().TotalRequests);
        // 通道 ID 只作为主键列存进库，不再拼进文件路径。
        Assert.True(File.Exists(Path.Combine(_directory, "data.db")));
    }

    [Fact]
    public void DuplicateWritesAreIdempotentAndDamagedRowIsSkipped()
    {
        var data = Data();
        var spec = new SqliteDailyStorage(data, "route", "通道");
        var instant = Now(19, 12);
        var ms = Millis(instant);
        using (var state = new MetricsState(instant, spec))
        {
            state.Change("done", new DailyChange.Started(), ms);
            state.Change("done", new DailyChange.Succeeded(Cache("done", false)), ms + 1);
        }

        using (var connection = data.Database.Connect())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM daily_requests WHERE route_id = 'route';"));
            // 直接塞一条坏 JSON：模拟库里出现损坏行。
            Exec(connection, "INSERT INTO daily_requests (route_id, date, request_id, updated_at_ms, sequence, outcome, retry_count, key_id, key_name, record_json) VALUES ('route', '2026-09-19', 'broken', 0, 99, NULL, 0, '', '', '{\"request_id\":');");
        }

        using (var restored = new MetricsState(instant, spec))
        {
            Assert.NotNull(restored.Snapshot().StatisticsWarning);
            Assert.Equal(1UL, restored.Snapshot().TotalRequests);
            Assert.Equal(1UL, restored.Snapshot().SuccessfulRequests);
        }

        var summary = DailyHistory.ReadDay(data, "route", new DateOnly(2026, 9, 19));
        Assert.Equal(1UL, summary.TotalRequests);
        Assert.Equal(1UL, summary.SuccessfulRequests);
    }

    [Fact]
    public void WriteFailureKeepsLiveStatisticsAndRetriesUnsavedRecords()
    {
        var storage = new FlakyStorage();
        using (var state = new MetricsState(Now(19, 12), storage))
        {
            storage.Journal.Failing = true;
            state.Change("failed-write", new DailyChange.Failed(), 1);
            Assert.Equal(1UL, state.Snapshot().FailedRequests);
            Assert.NotNull(state.Snapshot().StatisticsWarning);
            storage.Journal.Failing = false;
            state.Change("next", new DailyChange.Failed(), 2);
            Assert.Null(state.Snapshot().StatisticsWarning);
            // 失败的记录留在 dirty 里，下次更新时按序补写。
            Assert.Equal(new[] { "failed-write", "next" }, storage.Journal.Appended);
        }
    }

    [Fact]
    public void RecentHistoryIsSortedByCompletionAndRestoresOnlyLatestTwenty()
    {
        var spec = Spec();
        CacheSnapshot expected;
        using (var state = new MetricsState(Now(19, 12), spec))
        {
            for (var index = 24; index >= 0; index--)
            {
                var id = index.ToString("D8");
                state.Change(id, new DailyChange.Succeeded(Cache(id, index % 2 == 0)), Millis(Now(19, 12)));
            }

            expected = state.Snapshot().Cache;
        }

        Assert.Equal(CacheSnapshot.HistoryLimit, expected.RecentRequests.Count);
        using var restored = new MetricsState(Now(19, 12), spec);
        Assert.Equal(Json(expected), Json(restored.Snapshot().Cache));
    }

    [Fact]
    public void HistoricalUnfinishedRequestsStaySeparateFromCurrentRequestsAndKeepalive()
    {
        using (var first = ProxyMetrics.FromDailyLogs(Data(), "route", "通道"))
        {
            first.RequestStarted("unfinished", "POST", "/v1/messages");
            Assert.Equal(0UL, first.Snapshot().HistoricalUnfinishedRequests);
        }

        using var restored = ProxyMetrics.FromDailyLogs(Data(), "route", "通道");
        restored.RequestStarted("current", "POST", "/v1/responses");
        restored.RequestStarted("保活-ignore", "POST", "/v1/responses");
        restored.Success("保活-ignore", Cache("保活-ignore", false));
        var snapshot = restored.Snapshot();
        Assert.Equal((2UL, 1UL, 1UL), (snapshot.TotalRequests, snapshot.ActiveRequests, snapshot.HistoricalUnfinishedRequests));
        Assert.Equal(0UL, snapshot.SuccessfulRequests);
    }

    /// <summary>成功请求记录下当时实际使用的供应商 Key，重启后按 Key 拆分的用量要能重建。</summary>
    [Fact]
    public void ProviderKeyIsPersistedAndRebuiltOnRestart()
    {
        var spec = Spec("key-route");
        var instant = Now(19, 12);
        var ms = Millis(instant);
        using (var state = new MetricsState(instant, spec))
        {
            state.Change("one", new DailyChange.Succeeded(Cache("one", true), "key-1", "主号"), ms);
            state.Change("two", new DailyChange.Succeeded(Cache("two", false), "key-2", "备用"), ms + 1);
        }

        using var restored = new MetricsState(instant, spec);
        var keys = restored.Snapshot().Cache.Keys;
        Assert.Equal(2, keys.Count);
        Assert.Equal("主号", keys["key-1"].Name);
        Assert.Equal(1UL, keys["key-1"].Requests);
        Assert.Equal("备用", keys["key-2"].Name);
        Assert.Equal(1UL, keys["key-2"].Requests);
    }

    /// <summary>库结构版本高于本程序时不读不写：只显示本次运行数据，等待用户处理（与旧文件头不匹配一致）。</summary>
    [Fact]
    public void SchemaVersionTooNewIsReportedAsRecoveryFailureAndLiveDataStaysInMemory()
    {
        var data = Data();
        using (var connection = data.Database.Connect())
        {
            Exec(connection, "UPDATE meta SET value = '99' WHERE key = 'schema_version';");
        }

        var spec = new SqliteDailyStorage(Data(), "route", "通道");
        using var state = new MetricsState(Now(19, 12), spec);
        Assert.Equal("当日统计日志恢复失败（InvalidData），当前仅显示本次运行数据", state.Snapshot().StatisticsWarning);
        state.Change("live", new DailyChange.Failed(), 1);
        Assert.Equal(1UL, state.Snapshot().FailedRequests);
        Assert.Equal(1UL, state.Snapshot().TotalRequests);
    }

    /// <summary>历史每日汇总只读数据库：每行一条请求只算一次，缺失的那天返回空汇总。</summary>
    [Fact]
    public void DailyHistorySummarisesOneDayWithoutCountingARequestTwice()
    {
        var data = Data();
        var spec = new SqliteDailyStorage(data, "history-route", "通道");
        var day = new DateOnly(2026, 9, 19);
        var instant = Now(19, 12);
        var ms = Millis(instant);
        using (var state = new MetricsState(instant, spec))
        {
            state.Change("bad", new DailyChange.Failed(), ms);
            state.Change("retried", new DailyChange.Retry(2), ms + 1);
            state.Change("retried", new DailyChange.Succeeded(Cache("retried", true), "key-1", "主号"), ms + 2);
        }

        var summary = DailyHistory.ReadDay(data, "history-route", day);
        Assert.True(summary.HasData);
        Assert.Equal(2UL, summary.TotalRequests);
        Assert.Equal(1UL, summary.SuccessfulRequests);
        Assert.Equal(1UL, summary.FailedRequests);
        Assert.Equal(1UL, summary.RetryCount);
        Assert.Equal(1000UL, summary.InputTokens);
        Assert.Equal(800UL, summary.CachedTokens);
        Assert.Equal(80.0, summary.HitRatePercent());

        var missing = DailyHistory.ReadDay(data, "history-route", day.AddDays(-1));
        Assert.False(missing.HasData);
        Assert.Equal(0UL, missing.TotalRequests);

        var range = DailyHistory.ReadRange(data, "history-route", day.AddDays(-2), 3);
        Assert.Equal(3, range.Count);
        Assert.Equal(day, range[2].Date);
        Assert.True(range[2].HasData);
        Assert.False(range[0].HasData);
    }

    /// <summary>7 天趋势的 SQL 口径必须与 CacheRequest.Usage() 一致：includes 取总输入；excludes 为三项相加；越界与缺项排除。</summary>
    [Fact]
    public void DailyHistoryUsageMatchesCacheRequestAccounting()
    {
        var data = Data();
        var spec = new SqliteDailyStorage(data, "usage-route", "通道");
        var instant = Now(19, 12);
        var ms = Millis(instant);
        var caches = new[]
        {
            MakeCache("a", CacheInputAccounting.IncludesCached, 1000, 800, null),
            MakeCache("b", CacheInputAccounting.IncludesCached, 1000, null, null),
            MakeCache("c", CacheInputAccounting.IncludesCached, 0, 0, null),
            MakeCache("d", CacheInputAccounting.ExcludesCached, 100, 900, 200),
            MakeCache("e", CacheInputAccounting.ExcludesCached, 100, 900, null),
            MakeCache("f", CacheInputAccounting.ExcludesCached, null, 500, 100),
            MakeCache("g", CacheInputAccounting.IncludesCached, 500, 600, null),
        };
        using (var state = new MetricsState(instant, spec))
        {
            foreach (var cache in caches)
            {
                state.Change(cache.RequestId, new DailyChange.Succeeded(cache), ms);
            }
        }

        ulong expectedInput = 0;
        ulong expectedCached = 0;
        foreach (var cache in caches)
        {
            if (cache.Usage() is { } usage)
            {
                expectedInput += usage.Input;
                expectedCached += usage.Cached;
            }
        }

        var summary = DailyHistory.ReadDay(data, "usage-route", new DateOnly(2026, 9, 19));
        Assert.Equal(expectedInput, summary.InputTokens);
        Assert.Equal(expectedCached, summary.CachedTokens);
        // 显式值：a(1000/800) + d(1200/900) + e(1000/900)；b 缺项、c 全零、f 缺输入、g 命中超过总输入都被排除。
        Assert.Equal(3200UL, summary.InputTokens);
        Assert.Equal(2600UL, summary.CachedTokens);
    }

    /// <summary>
    /// PRD 风险表要求的吞吐冒烟：统计写入在请求路径上同步排队，1000 次 UPSERT 必须远快于转发本身。
    /// 上限 10 秒非常宽松（正常不到 1 秒），只拦截数量级退化（例如每次写都开库或强制刷盘）。
    /// </summary>
    [Fact]
    public void JournalWritesKeepUpWithRequestBursts()
    {
        var data = Data();
        var spec = new SqliteDailyStorage(data, "burst-route", "通道");
        var instant = Now(19, 12);
        var ms = Millis(instant);
        using (var state = new MetricsState(instant, spec))
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (var index = 0; index < 500; index++)
            {
                var id = index.ToString("D8");
                state.Change(id, new DailyChange.Started(), ms);
                state.Change(id, new DailyChange.Succeeded(Cache(id, false)), ms + 1);
            }

            stopwatch.Stop();
            Assert.Equal(500UL, state.Snapshot().TotalRequests);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"1000 次统计写入耗时 {stopwatch.Elapsed.TotalSeconds:F1} 秒");
        }

        using var connection = data.Database.Connect();
        Assert.Equal(500L, Scalar(connection, "SELECT COUNT(*) FROM daily_requests WHERE route_id = 'burst-route';"));
    }

    private static CacheRequest MakeCache(string id, CacheInputAccounting accounting, ulong? input, ulong? cached, ulong? created)
    {
        return new CacheRequest
        {
            RequestId = id,
            Model = "model-test",
            CompletedAtUnixMs = 0,
            InputTokens = input,
            CachedTokens = cached,
            CacheCreationTokens = created,
            InputAccounting = accounting,
            CacheKeyStatus = "测试",
        };
    }
}
