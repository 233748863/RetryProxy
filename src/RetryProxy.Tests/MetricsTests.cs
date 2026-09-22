using System;
using System.Text.Json;
using System.Threading;
using RetryProxy.Core.Metrics;
using Xunit;

namespace RetryProxy.Tests;

public class MetricsTests
{
    private static CacheRequest MakeCacheRequest(int id, ulong? input, ulong? cached)
    {
        return new CacheRequest
        {
            RequestId = id.ToString("D8"),
            Model = "gpt-test",
            CompletedAtUnixMs = 0,
            InputTokens = input,
            CachedTokens = cached,
            CacheCreationTokens = null,
            InputAccounting = CacheInputAccounting.IncludesCached,
            CacheKeyStatus = "客户端已设置",
        };
    }

    private static string Serialize(CacheSnapshot snapshot) => JsonSerializer.Serialize(snapshot);

    [Fact]
    public void CacheRateWeightsTokensAndSeparatesZeroHits()
    {
        var metrics = new ProxyMetrics();
        Assert.Null(metrics.Snapshot().GptCache.HitRatePercent());
        var samples = new (ulong Input, ulong Cached)[] { (1000, 500), (3000, 2500), (1000, 0), (0, 0), (10, 11) };
        for (var id = 0; id < samples.Length; id++)
        {
            metrics.RecordCacheRequest(MakeCacheRequest(id, samples[id].Input, samples[id].Cached));
        }

        var cache = metrics.Snapshot().GptCache;
        Assert.Equal(3UL, cache.MeasuredRequests);
        Assert.Equal(60.0, cache.HitRatePercent());
        Assert.Equal(1UL, cache.ZeroHitRequests);
        Assert.Equal(1000UL, cache.ZeroHitInputTokens);
        Assert.Equal(2UL, cache.UnmeasuredRequests);
        Assert.Equal(60.0, cache.RecentHitRatePercent());
        metrics.RecordCacheRequest(MakeCacheRequest(5, ulong.MaxValue, 0));
        Assert.Equal(Serialize(cache), Serialize(metrics.Snapshot().GptCache));
    }

    [Fact]
    public void RecentCacheHistoryIsBoundedAndMissingUsageDoesNotShowAStaleLastHit()
    {
        var metrics = new ProxyMetrics();
        metrics.RecordCacheRequest(MakeCacheRequest(0, 3000, 0));
        for (var id = 1; id <= 25; id++)
        {
            metrics.RecordCacheRequest(MakeCacheRequest(id, 1000, 900));
        }

        metrics.RecordCacheRequest(MakeCacheRequest(26, 1000, null));
        var cache = metrics.Snapshot().GptCache;
        Assert.Equal(CacheSnapshot.HistoryLimit, cache.RecentRequests.Count);
        Assert.Equal("00000007", cache.RecentRequests[0].RequestId);
        Assert.Equal("00000026", cache.RecentRequests[^1].RequestId);
        Assert.Null(cache.RecentRequests[^1].HitRatePercent());
        Assert.Equal(28_000UL, cache.InputTokens);
        Assert.Equal(22_500UL, cache.CachedTokens);
        Assert.Equal(26UL, cache.MeasuredRequests);
        Assert.Equal(1UL, cache.ZeroHitRequests);
        Assert.Equal(1UL, cache.UnmeasuredRequests);
        Assert.Equal(90.0, cache.RecentHitRatePercent());
        Assert.Equal(Serialize(new CacheSnapshot()), Serialize(new ProxyMetrics().Snapshot().GptCache));
    }

    [Fact]
    public void ChannelCacheCombinesProtocolsAndKeepsLegacyGptMetricsSeparate()
    {
        var metrics = new ProxyMetrics();
        metrics.RecordCacheRequest(MakeCacheRequest(0, 1000, 500));
        var claude = MakeCacheRequest(1, 100, 800);
        claude.Model = "claude-test";
        claude.InputAccounting = CacheInputAccounting.ExcludesCached;
        claude.CacheCreationTokens = 100;
        Assert.Equal(1000UL, claude.TotalInputTokens());
        Assert.Equal(80.0, claude.HitRatePercent());
        metrics.RecordCacheRequest(claude.Clone());
        var snapshot = metrics.Snapshot();
        Assert.Equal(2000UL, snapshot.Cache.InputTokens);
        Assert.Equal(1300UL, snapshot.Cache.CachedTokens);
        Assert.Equal(65.0, snapshot.Cache.HitRatePercent());
        Assert.Equal(65.0, snapshot.Cache.RecentHitRatePercent());
        Assert.Equal(100UL, snapshot.Cache.CacheCreationTokens);
        Assert.Equal(1UL, snapshot.Cache.CacheCreationMeasuredRequests);
        Assert.Equal(1UL, snapshot.GptCache.MeasuredRequests);
        Assert.Equal(50.0, snapshot.GptCache.HitRatePercent());

        claude.CacheCreationTokens = null;
        claude.RequestId = "00000002";
        metrics.RecordCacheRequest(claude);
        var cache = metrics.Snapshot().Cache;
        Assert.Equal(1UL, cache.UnmeasuredRequests);
        Assert.Equal(0UL, cache.ZeroHitRequests);
        Assert.Equal(65.0, cache.HitRatePercent());
        Assert.Null(cache.RecentRequests[^1].TotalInputTokens());
    }

    [Fact]
    public void ClaudeCacheHandlesZeroFreshInputAndRejectsOverflow()
    {
        var request = MakeCacheRequest(0, 0, 800);
        request.InputAccounting = CacheInputAccounting.ExcludesCached;
        request.CacheCreationTokens = 0;
        Assert.Equal(100.0, request.HitRatePercent());
        request.CachedTokens = 0;
        request.CacheCreationTokens = 800;
        Assert.Equal(800UL, request.TotalInputTokens());
        Assert.Equal(0.0, request.HitRatePercent());
        request.InputTokens = ulong.MaxValue;
        Assert.Null(request.Usage());
        Assert.True(request.UsageIsInvalid());
    }

    [Fact]
    public void ConcurrentRequestsKeepIndependentPhases()
    {
        var metrics = new ProxyMetrics();
        metrics.RequestStarted("first", "POST", "/v1/responses");
        metrics.RequestStarted("second", "POST", "/v1/messages");
        metrics.RequestAttempt("first", 2);
        metrics.RequestPhase("first", RequestPhase.WaitingRetry);
        metrics.RequestPhase("second", RequestPhase.ReceivingResponse);
        var snapshot = metrics.Snapshot();
        Assert.Equal(2UL, snapshot.TotalRequests);
        Assert.Equal(2UL, snapshot.ActiveRequests);
        Assert.Equal(RequestPhase.WaitingRetry, snapshot.Requests[0].Phase);
        Assert.Equal(2UL, snapshot.Requests[0].Attempt);
        Assert.Equal(RequestPhase.ReceivingResponse, snapshot.Requests[1].Phase);
        metrics.RequestFinished("first");
        snapshot = metrics.Snapshot();
        Assert.Equal(1UL, snapshot.ActiveRequests);
        Assert.Equal("second", snapshot.Requests[0].RequestId);
    }

    [Fact]
    public void PhaseUpdatesDoNotCreateOrReopenRequests()
    {
        var metrics = new ProxyMetrics();
        metrics.RequestAttempt("background", 1);
        metrics.RequestPhase("background", RequestPhase.WaitingRetry);
        var empty = metrics.Snapshot();
        Assert.Equal(0UL, empty.TotalRequests);
        Assert.Equal(0UL, empty.ActiveRequests);
        Assert.Empty(empty.Requests);
        metrics.RequestStarted("request", "GET", "/test");
        metrics.RequestFinished("request");
        metrics.RequestFinished("request");
        metrics.RequestAttempt("request", 2);
        metrics.RequestPhase("request", RequestPhase.ReceivingResponse);
        var snapshot = metrics.Snapshot();
        Assert.Equal(1UL, snapshot.TotalRequests);
        Assert.Equal(0UL, snapshot.ActiveRequests);
        Assert.Empty(snapshot.Requests);
    }

    [Fact]
    public void RequestLifecycleNotifiesUiAfterEachChange()
    {
        var count = 0;
        var metrics = new ProxyMetrics();
        metrics.SetUiNotifier(() => Interlocked.Increment(ref count));
        metrics.RequestStarted("req-1", "POST", "/v1/responses");
        Assert.Equal(1, Volatile.Read(ref count));
        metrics.RequestAttempt("req-1", 2);
        Assert.Equal(2, Volatile.Read(ref count));
        metrics.RequestPhase("req-1", RequestPhase.ReceivingResponse);
        Assert.Equal(3, Volatile.Read(ref count));
        metrics.RequestFinished("req-1");
        Assert.True(Volatile.Read(ref count) >= 4);
        Assert.Equal(0UL, metrics.Snapshot().ActiveRequests);
    }
}
