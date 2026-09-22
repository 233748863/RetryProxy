using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Metrics;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>对应 legacy.rs 里的单元测试：从保留的文本日志一次性恢复当日统计。</summary>
public class LegacyLogRestoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));

    public LegacyLogRestoreTests()
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

    private static readonly DateOnly Date = new(2026, 9, 19);

    private void WriteLog(string name, params string[] lines)
    {
        File.WriteAllText(Path.Combine(_directory, name), string.Concat(lines.Select(line => line + "\n")), new UTF8Encoding(false));
    }

    [Fact]
    public void RetainedLogsDeduplicateAttemptsAndTerminalResultsAndExcludeKeepalive()
    {
        var first = new[]
        {
            "2026-09-18 23:59:59 INFO [通道][bbbbbbbb] 第 450/1000 次 POST /v1/responses -> 上游 HTTP 500，耗时 1.00 秒",
            "2026-09-19 00:00:01 INFO [通道][bbbbbbbb] 第 451/1000 次 POST /v1/responses -> 上游 HTTP 500，耗时 1.00 秒",
            "2026-09-19 00:00:01 WARNING [通道][bbbbbbbb] 上游 HTTP 500 可重试，0.01 秒后再次请求",
            "2026-09-19 00:00:02 WARNING [通道][bbbbbbbb] 第 452/1000 次 POST /v1/responses -> 上游状态码：无，连接失败，将在 0.01 秒后重试，耗时 1.00 秒",
        };
        WriteLog("retry-proxy.log.1", first);
        WriteLog("retry-proxy.log", first.Concat(new[]
        {
            "2026-09-19 00:00:03 INFO [通道][bbbbbbbb] 第 453/1000 次 POST /v1/responses -> 上游 HTTP 200，模型 gpt-test，输入 1000 / 输出 10 token，缓存命中 800 token，缓存标识：客户端已设置，耗时 3.00 秒",
            "2026-09-19 00:00:04 WARNING [通道][cccccccc] 第 1/6 次 POST /v1/messages -> 上游 HTTP 200，响应未完成，原因：上游请求超限，不再重试（已进入响应转发阶段），模型 claude-test，输入 100 / 输出 0 token，缓存命中 900 token，缓存写入 0 token",
            "2026-09-19 00:00:04 WARNING [通道][cccccccc] 客户端在响应转发前断开，已取消当前请求，不再重试，耗时 1.00 秒",
            "2026-09-19 00:00:05 INFO [通道][dddddddd] 第 1/2 次 POST /v1/messages -> 上游 HTTP 500，耗时 1.00 秒",
            "2026-09-19 00:00:05 WARNING [通道][dddddddd] 上游 HTTP 500 可重试，0.01 秒后再次请求",
            "2026-09-19 00:00:06 INFO [其他通道][eeeeeeee] POST /v1/responses -> 上游 HTTP 200",
            "2026-09-19 00:00:06 INFO [通道][保活-ffffffff] POST /v1/responses -> 上游 HTTP 200",
            "2026-09-19 00:00:06 INFO [通道] 供应商保活 [会话 aaaaaaaa] 第 1 轮 完整回复",
        }).ToArray());
        var records = LegacyLogRestore.Restore(_directory, "通道", Date);
        Assert.Equal(3, records.Count);
        Assert.Equal(2UL, records["bbbbbbbb"].RetryCount);
        Assert.Equal(RequestOutcome.Success, records["bbbbbbbb"].Outcome);
        Assert.Equal(CacheKeyState.Client, records["bbbbbbbb"].CacheKey);
        Assert.Equal(RequestOutcome.Failure, records["cccccccc"].Outcome);
        Assert.Null(records["cccccccc"].Cache);
        Assert.Null(records["dddddddd"].Outcome);

        var spec = new DailyStorage(_directory, "route", "通道");
        var instant = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Local);
        MetricsSnapshot snapshot;
        using (var state = new MetricsState(instant, spec))
        {
            snapshot = state.Snapshot();
        }

        Assert.Equal((3UL, 1UL, 1UL, 3UL), (snapshot.TotalRequests, snapshot.SuccessfulRequests, snapshot.FailedRequests, snapshot.RetryCount));
        Assert.Equal(1UL, snapshot.HistoricalUnfinishedRequests);
        Assert.Equal(80.0, snapshot.Cache.HitRatePercent());
        Assert.True(snapshot.RestoredFromLegacyLogs);
        // 普通轮转与重复的旧日志行不会再触发导入。
        File.Delete(Path.Combine(_directory, "retry-proxy.log"));
        File.Delete(Path.Combine(_directory, "retry-proxy.log.1"));
        using var restored = new MetricsState(instant, spec);
        Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(restored.Snapshot()));
    }

    [Fact]
    public void ClaudeUsageIsCombinedOnlyWhenAllThreeFieldsWereLogged()
    {
        WriteLog(
            "retry-proxy.log",
            "2026-09-19 01:00:00 INFO [通道][11111111] POST /v1/messages -> 上游 HTTP 200，模型 claude-test，输入 0 / 输出 1 token，缓存命中 800 token，缓存写入 200 token，首字 1.00 秒，耗时 1.00 秒",
            "2026-09-19 01:00:01 INFO [通道][22222222] POST /v1/messages -> 上游 HTTP 200，模型 claude-test，输入 0 / 输出 1 token，缓存命中 800 token，首字 1.00 秒，耗时 1.00 秒",
            "2026-09-19 01:00:02 INFO [通道][33333333] POST /v1/responses -> 上游 HTTP 200，模型 gpt-test，输入 1000 / 输出 1 token，缓存命中 0 token，耗时 1.00 秒",
            "2026-09-19 01:00:03 INFO [通道][44444444] POST /v1/chat/completions -> 上游 HTTP 200，模型 other-model，输入 未获取 / 输出 未获取 token，耗时 1.00 秒");
        var records = LegacyLogRestore.Restore(_directory, "通道", Date);
        Assert.Equal((1000UL, 800UL), records["11111111"].Cache!.Usage());
        Assert.Null(records["22222222"].Cache!.TotalInputTokens());
        Assert.Equal((1000UL, 0UL), records["33333333"].Cache!.Usage());
        var absent = records["44444444"].Cache!;
        Assert.Equal("other-model", absent.Model);
        Assert.Null(absent.InputTokens);
        Assert.Null(absent.CachedTokens);
        Assert.Null(absent.CacheCreationTokens);
    }

    [Fact]
    public void RetryExhaustionRejectionAndCompatibilityResendHaveDistinctCounts()
    {
        WriteLog(
            "retry-proxy.log",
            "2026-09-19 01:00:00 INFO [通道][11111111] 上游不接受代理补充的缓存标识，使用原请求兼容重发一次；当前通道对同一接口、模型及鉴权暂停补充",
            "2026-09-19 01:00:00 INFO [通道][11111111] POST /v1/responses -> 上游 HTTP 200，模型 gpt-test，输入 1000 / 输出 10 token，缓存命中 800 token，缓存标识：保持原请求（上游不支持），耗时 1.00 秒",
            "2026-09-19 01:00:01 WARNING [通道][22222222] 第 6/6 次 POST /v1/messages -> 上游状态码：无，连接失败，已达到重试上限，耗时 1.00 秒",
            "2026-09-19 01:00:02 WARNING [通道][33333333] 重试耗尽，向客户端返回最后一次上游响应 HTTP 500",
            "2026-09-19 01:00:02 INFO [通道][33333333] 第 6/6 次 POST /v1/messages -> 上游 HTTP 500，耗时 1.00 秒",
            "2026-09-19 01:00:03 INFO [通道][44444444] 第 1/6 次 POST /v1/responses -> 上游 HTTP 400，耗时 1.00 秒");
        var records = LegacyLogRestore.Restore(_directory, "通道", Date);
        Assert.Equal(4, records.Count);
        Assert.True(records["11111111"].CacheFallback);
        Assert.Equal("保持原请求（上游不支持）", records["11111111"].Cache!.CacheKeyStatus);
        Assert.Equal(CacheKeyState.Added, records["11111111"].CacheKey);
        Assert.Equal(0UL, records.Values.Aggregate(0UL, (sum, record) => sum + record.RetryCount));
        Assert.Equal(3, records.Values.Count(record => record.Outcome == RequestOutcome.Failure));
    }

    [Fact]
    public void PartialTextAndOtherDaysAreNotTreatedAsCompleteRequests()
    {
        File.WriteAllText(
            Path.Combine(_directory, "retry-proxy.log"),
            "2026-09-18 23:59:59 INFO [通道][11111111] POST /v1/messages -> 上游 HTTP 200\n"
            + "2026-09-20 00:00:00 INFO [通道][22222222] POST /v1/messages -> 上游 HTTP 200\n"
            + "2026-09-19 01:00:00 INFO [通道][33333333] POST /v1/messages -> 上游 HTTP 200，模型 claude-",
            new UTF8Encoding(false));
        Assert.Empty(LegacyLogRestore.Restore(_directory, "通道", Date));
    }
}
