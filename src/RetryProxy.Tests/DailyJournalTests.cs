using System;
using System.IO;
using System.Text;
using System.Text.Json;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Metrics;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>对应 daily.rs 里的单元测试：jsonl 当日日志的恢复、跨日、损坏修复与写失败。</summary>
public class DailyJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));

    public DailyJournalTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

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

    private DailyStorage Spec(string routeId = "route", string routeName = "通道") => new(_directory, routeId, routeName);

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

    [Fact]
    public void EmptyMidnightAndNextDayRestartDoNotReimportCompletionText()
    {
        var spec = Spec();
        using (var state = new MetricsState(Now(19, 23), spec))
        {
            File.WriteAllText(
                Path.Combine(_directory, "retry-proxy.log"),
                "2026-09-20 00:00:00 INFO [通道][abc12345] POST /v1/responses -> 上游 HTTP 200，模型 gpt-test，输入 1000 / 输出 1 token，缓存命中 800 token\n",
                new UTF8Encoding(false));
            state.Rollover(Now(20, 0));
            Assert.Equal(0UL, state.Snapshot().TotalRequests);
            Assert.False(state.Snapshot().RestoredFromLegacyLogs);
        }

        using var after = new MetricsState(Now(20, 1), spec);
        Assert.Equal(0UL, after.Snapshot().TotalRequests);
    }

    [Fact]
    public void RouteRenameKeepsHistoryAndSameNamedRoutesWithOtherIdsStaySeparate()
    {
        using (var first = ProxyMetrics.FromDailyLogs(_directory, "../../id-a", "旧名"))
        {
            first.Success("one", Cache("one", false));
        }

        using var renamed = ProxyMetrics.FromDailyLogs(_directory, "../../id-a", "新名");
        Assert.Equal(1UL, renamed.Snapshot().SuccessfulRequests);
        using var another = ProxyMetrics.FromDailyLogs(_directory, "id-b", "新名");
        Assert.Equal(0UL, another.Snapshot().TotalRequests);
        Assert.False(Directory.Exists(Path.Combine(_directory, "id-a")));
        Assert.False(File.Exists(Path.Combine(_directory, "id-a")));
    }

    [Fact]
    public void TruncatedTailAndDuplicateCompleteRecordDoNotResetOrDoubleCount()
    {
        var spec = Spec();
        using (var state = new MetricsState(Now(19, 12), spec))
        {
            state.Change("done", new DailyChange.Succeeded(Cache("done", false)), Millis(Now(19, 12)));
        }

        var path = spec.JournalPath(new DateOnly(2026, 9, 19));
        var saved = File.ReadAllText(path, Encoding.UTF8);
        var lines = saved.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var finalLine = lines[^1];
        using (var file = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            var bytes = Encoding.UTF8.GetBytes(finalLine + "\n" + "{\"record\":\"request\",\"value\":");
            file.Write(bytes);
        }

        using (var restored = new MetricsState(Now(19, 12), spec))
        {
            Assert.Equal(1UL, restored.Snapshot().SuccessfulRequests);
            Assert.Equal(1UL, restored.Snapshot().Cache.MeasuredRequests);
            Assert.NotNull(restored.Snapshot().StatisticsWarning);
            restored.Change("next", new DailyChange.Failed(), Millis(Now(19, 12)));
        }

        using var again = new MetricsState(Now(19, 12), spec);
        Assert.Equal((2UL, 1UL), (again.Snapshot().TotalRequests, again.Snapshot().FailedRequests));
        Assert.Null(again.Snapshot().StatisticsWarning);
    }

    [Fact]
    public void ValidFinalJsonWithoutNewlineIsRestoredBeforeNextAppend()
    {
        var spec = Spec();
        using (var state = new MetricsState(Now(19, 12), spec))
        {
            state.Change("done", new DailyChange.Failed(), 1);
        }

        var path = spec.JournalPath(new DateOnly(2026, 9, 19));
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^1]);
        using (var restored = new MetricsState(Now(19, 12), spec))
        {
            restored.Change("next", new DailyChange.Failed(), 2);
        }

        using var again = new MetricsState(Now(19, 12), spec);
        Assert.Equal(2UL, again.Snapshot().FailedRequests);
    }

    [Fact]
    public void WriteFailureKeepsLiveStatisticsAndRetriesUnsavedRecords()
    {
        var spec = Spec();
        var path = spec.JournalPath(new DateOnly(2026, 9, 19));
        using (var state = new MetricsState(Now(19, 12), spec))
        {
            var journal = Assert.IsType<DailyJournal>(state.JournalForTest);
            journal.ReplaceStreamForTest(DailyJournal.OpenReadOnlyForTest(path));
            state.Change("failed-write", new DailyChange.Failed(), 1);
            Assert.Equal(1UL, state.Snapshot().FailedRequests);
            Assert.NotNull(state.Snapshot().StatisticsWarning);
            journal.ReplaceStreamForTest(DailyJournal.OpenAppendForTest(path));
            state.Change("next", new DailyChange.Failed(), 2);
            Assert.Null(state.Snapshot().StatisticsWarning);
        }

        using var again = new MetricsState(Now(19, 12), spec);
        Assert.Equal(2UL, again.Snapshot().FailedRequests);
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
        using (var first = ProxyMetrics.FromDailyLogs(_directory, "route", "通道"))
        {
            first.RequestStarted("unfinished", "POST", "/v1/messages");
            Assert.Equal(0UL, first.Snapshot().HistoricalUnfinishedRequests);
        }

        using var restored = ProxyMetrics.FromDailyLogs(_directory, "route", "通道");
        restored.RequestStarted("current", "POST", "/v1/responses");
        restored.RequestStarted("保活-ignore", "POST", "/v1/responses");
        restored.Success("保活-ignore", Cache("保活-ignore", false));
        var snapshot = restored.Snapshot();
        Assert.Equal((2UL, 1UL, 1UL), (snapshot.TotalRequests, snapshot.ActiveRequests, snapshot.HistoricalUnfinishedRequests));
        Assert.Equal(0UL, snapshot.SuccessfulRequests);
    }

    /// <summary>
    /// 行布局沿用 Rust 版 serde 输出；key_id / key_name 是本版新增的两个可选字段，位置在行尾。
    /// 旧文件（Rust 版或本版早期版本写的）没有这两段，读入时为空字符串，本版仍能正确累加。
    /// 反方向（Rust 版读本版新写的行）未在本仓库验证：Rust 源码不在本仓库，
    /// 结论依赖 serde 默认忽略未知字段这一行为。
    /// </summary>
    [Fact]
    public void JournalLinesMatchTheRustSerdeLayout()
    {
        var spec = Spec();
        using (var state = new MetricsState(Now(19, 12), spec))
        {
            state.Change("done", new DailyChange.Failed(), 1);
            state.Change("gpt", new DailyChange.Succeeded(new CacheRequest
            {
                Model = "gpt-test",
                InputTokens = 1000,
                CachedTokens = 800,
                InputAccounting = CacheInputAccounting.IncludesCached,
                CacheKeyStatus = "代理已补全",
            }, "key-1", "主号"), 2);
        }

        var lines = File.ReadAllText(spec.JournalPath(new DateOnly(2026, 9, 19)), Encoding.UTF8).Split('\n');
        Assert.Equal("{\"record\":\"header\",\"version\":1,\"date\":\"2026-09-19\",\"route_id\":\"route\",\"route_name\":\"通道\",\"legacy_imported\":false}", lines[0]);
        Assert.Equal("{\"record\":\"request\",\"value\":{\"request_id\":\"done\",\"updated_at_unix_ms\":1,\"sequence\":1,\"outcome\":\"failure\",\"retry_count\":0,\"last_retry_attempt\":null,\"cache_key\":null,\"cache_fallback\":false,\"cache\":null,\"key_id\":\"\",\"key_name\":\"\"}}", lines[1]);
        Assert.Equal("{\"record\":\"request\",\"value\":{\"request_id\":\"gpt\",\"updated_at_unix_ms\":2,\"sequence\":2,\"outcome\":\"success\",\"retry_count\":0,\"last_retry_attempt\":null,\"cache_key\":null,\"cache_fallback\":false,\"cache\":{\"request_id\":\"gpt\",\"model\":\"gpt-test\",\"completed_at_unix_ms\":2,\"input_tokens\":1000,\"cached_tokens\":800,\"cache_creation_tokens\":null,\"input_accounting\":\"includes_cached\",\"cache_key_status\":\"代理已补全\"},\"key_id\":\"key-1\",\"key_name\":\"主号\"}}", lines[2]);
        Assert.Equal(string.Empty, lines[3]);
    }

    /// <summary>头不匹配（其他通道 ID 写的文件）时只显示本次运行数据，不覆盖别人的日志。</summary>
    [Fact]
    public void MismatchedHeaderIsReportedAsRecoveryFailureWithoutOverwriting()
    {
        var spec = Spec();
        Directory.CreateDirectory(spec.Directory);
        var path = spec.JournalPath(new DateOnly(2026, 9, 19));
        File.WriteAllText(path, "{\"record\":\"header\",\"version\":1,\"date\":\"2026-09-19\",\"route_id\":\"other\",\"route_name\":\"通道\",\"legacy_imported\":false}\n");
        using var state = new MetricsState(Now(19, 12), spec);
        var snapshot = state.Snapshot();
        Assert.Equal("当日统计日志恢复失败（InvalidData），当前仅显示本次运行数据", snapshot.StatisticsWarning);
        state.Change("live", new DailyChange.Failed(), 1);
        Assert.Equal(1UL, state.Snapshot().FailedRequests);
        Assert.Contains("\"route_id\":\"other\"", File.ReadAllText(path));
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

    /// <summary>历史每日汇总只读文件：同一请求的多行只算一次，缺失的那天返回空汇总。</summary>
    [Fact]
    public void DailyHistorySummarisesOneDayWithoutCountingARequestTwice()
    {
        var spec = Spec("history-route");
        var day = new DateOnly(2026, 9, 19);
        var instant = Now(19, 12);
        var ms = Millis(instant);
        using (var state = new MetricsState(instant, spec))
        {
            state.Change("bad", new DailyChange.Failed(), ms);
            state.Change("retried", new DailyChange.Retry(2), ms + 1);
            state.Change("retried", new DailyChange.Succeeded(Cache("retried", true), "key-1", "主号"), ms + 2);
        }

        var summary = DailyHistory.ReadDay(_directory, "history-route", day);
        Assert.True(summary.HasData);
        Assert.Equal(2UL, summary.TotalRequests);
        Assert.Equal(1UL, summary.SuccessfulRequests);
        Assert.Equal(1UL, summary.FailedRequests);
        Assert.Equal(1UL, summary.RetryCount);
        Assert.Equal(1000UL, summary.InputTokens);
        Assert.Equal(800UL, summary.CachedTokens);
        Assert.Equal(80.0, summary.HitRatePercent());

        var missing = DailyHistory.ReadDay(_directory, "history-route", day.AddDays(-1));
        Assert.False(missing.HasData);
        Assert.Equal(0UL, missing.TotalRequests);

        var range = DailyHistory.ReadRange(_directory, "history-route", day.AddDays(-2), 3);
        Assert.Equal(3, range.Count);
        Assert.Equal(day, range[2].Date);
        Assert.True(range[2].HasData);
        Assert.False(range[0].HasData);
    }
}
