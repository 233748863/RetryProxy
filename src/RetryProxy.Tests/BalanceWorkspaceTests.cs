using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RetryProxy.Core.Balance;
using RetryProxy.Core.Config;
using Xunit;

namespace RetryProxy.Tests;

public sealed class BalanceWorkspaceTests
{
    private const string ProviderId = "provider";
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    // 业务时间只由测试推进；任务门的超时仅防止实现出错时测试一直挂起。
    private sealed class Clock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private sealed class Call(BalanceRequest request, CancellationToken token)
    {
        public BalanceRequest Request { get; } = request;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<BalanceResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(BalanceResult result) => Assert.True(Response.TrySetResult(result));
        public void Fail(Exception error) => Assert.True(Response.TrySetException(error));
    }

    private sealed class Fetcher
    {
        private readonly ConcurrentQueue<Call> _calls = new();
        private readonly Channel<Call> _started = Channel.CreateUnbounded<Call>();
        private readonly object _sync = new();
        private int _active;
        private int _peak;
        public Call[] Calls => _calls.ToArray();
        public int Count => _calls.Count;
        public int Peak { get { lock (_sync) return _peak; } }

        public Task<BalanceResult> Fetch(BalanceRequest request, CancellationToken token)
        {
            var call = new Call(request, token);
            lock (_sync)
            {
                _active++;
                _peak = Math.Max(_peak, _active);
            }
            _calls.Enqueue(call);
            _started.Writer.TryWrite(call);
            return AwaitResponse(call);
        }

        private async Task<BalanceResult> AwaitResponse(Call call)
        {
            // 故意不自动响应取消，模拟供应商在取消后仍迟返成功结果。
            try { return await call.Response.Task.ConfigureAwait(false); }
            finally
            {
                lock (_sync) _active--;
                call.Finished.TrySetResult();
            }
        }

        public async Task<Call> Next() => await _started.Reader.ReadAsync().AsTask().WaitAsync(GateTimeout);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Channel<bool> _notifications = Channel.CreateUnbounded<bool>();
        private int _notificationCount;
        public Clock Clock { get; } = new();
        public Fetcher Fetcher { get; } = new();
        public ProxyConfig Config { get; }
        public BalanceWorkspace App { get; }
        public ProviderEndpoint Provider => Config.ProviderById(ProviderId)!;
        public int NotificationCount => Volatile.Read(ref _notificationCount);

        public Fixture(int keyCount = 1, BalanceQueryMode mode = BalanceQueryMode.Usage,
            Func<BalanceRequest, BalanceQueryMode, string?>? remember = null)
        {
            Config = ProxyConfig.Builtin();
            Config.Providers.Clear();
            Config.Providers.Add(CreateProvider(ProviderId, ClientType.Codex,
                Enumerable.Range(1, keyCount).Select(index => $"key-{index}").ToArray()));
            Provider.BalanceQuery.Mode = mode;
            App = new BalanceWorkspace(Fetcher.Fetch, remember, Clock);
            App.SetUiNotifier(() =>
            {
                Interlocked.Increment(ref _notificationCount);
                _notifications.Writer.TryWrite(true);
            });
            App.Synchronize(Config);
        }

        public BalanceSnapshot Snapshot(string key = "key-1") => App.Get(ProviderId, key);

        public async Task WaitForNotificationAfter(int previous)
        {
            while (NotificationCount <= previous)
                await _notifications.Reader.ReadAsync().AsTask().WaitAsync(GateTimeout);
        }

        public async Task NotifyCompletion(Action complete)
        {
            var previous = NotificationCount;
            complete();
            await WaitForNotificationAfter(previous);
        }

        public async Task<bool> Complete(Call call, BalanceResult? result = null)
        {
            await NotifyCompletion(() => call.Complete(result ?? Success()));
            return App.Poll();
        }

        public async ValueTask DisposeAsync()
        {
            App.Dispose();
            var calls = Fetcher.Calls;
            foreach (var call in calls) call.Response.TrySetResult(Success());
            await Task.WhenAll(calls.Select(call => call.Finished.Task)).WaitAsync(GateTimeout);
        }
    }

    private static ProviderEndpoint CreateProvider(string id, ClientType client, params string[] keys) => new()
    {
        Id = id,
        Name = id,
        ClientType = client,
        BaseUrl = $"https://{id}.example/v1",
        BalanceQuery = new BalanceQuery { Mode = BalanceQueryMode.Usage },
        Keys = keys.Select(key => new ProviderKey { Id = key, Name = key, ApiKey = $"sk-test-secret-{key}" }).ToList(),
    };

    private static BalanceResult Success(decimal amount = 12.5m, BalanceQueryMode mode = BalanceQueryMode.Usage) =>
        new(amount, "USD", false, false, mode, null);

    [Fact]
    public async Task PageRefreshUsesCompletionTimeAndFiveMinuteBoundaryForTheSelectedClient()
    {
        await using var fixture = new Fixture();
        fixture.Config.Providers.Add(CreateProvider("other", ClientType.Claude, "other-key"));
        fixture.App.Synchronize(fixture.Config);
        fixture.App.RefreshClient(ClientType.Codex);
        var first = await fixture.Fetcher.Next();
        Assert.True(fixture.Snapshot().IsRefreshing);
        Assert.True(fixture.Snapshot().IsEnabled);
        Assert.Null(fixture.Snapshot().CheckedAt);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await fixture.Complete(first));
        var checkedAt = fixture.Clock.GetUtcNow();
        Assert.Equal(checkedAt, fixture.Snapshot().CheckedAt);
        Assert.False(fixture.Snapshot().IsRefreshing);
        Assert.False(fixture.App.Poll());

        fixture.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        fixture.App.RefreshClient(ClientType.Codex);
        Assert.Equal(1, fixture.Fetcher.Count);
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        fixture.App.RefreshClient(ClientType.Codex);
        var second = await fixture.Fetcher.Next();
        Assert.Equal(ClientType.Codex, second.Request.ClientType);
        Assert.Equal(checkedAt, fixture.Snapshot().CheckedAt);
        Assert.True(await fixture.Complete(second, Success(20m)));
        Assert.Equal(2, fixture.Fetcher.Count);
        Assert.Null(fixture.App.Get("other", "other-key").CheckedAt);
    }

    [Fact]
    public async Task ManualRefreshBypassesFreshCacheAndKeepsTheOldBalanceUntilCompletion()
    {
        await using var fixture = new Fixture();
        fixture.Config.Providers.Add(CreateProvider("other", ClientType.Codex, "other-key"));
        fixture.App.Synchronize(fixture.Config);
        fixture.App.RefreshProvider(ProviderId);
        var old = Success(10m);
        await fixture.Complete(await fixture.Fetcher.Next(), old);
        var checkedAt = fixture.Snapshot().CheckedAt;

        fixture.App.RefreshProvider(ProviderId);
        var refreshed = await fixture.Fetcher.Next();
        Assert.True(fixture.App.IsRefreshing(ProviderId));
        Assert.Same(old, fixture.Snapshot().Result);
        Assert.Equal(checkedAt, fixture.Snapshot().CheckedAt);
        await fixture.Complete(refreshed, Success(20m));
        Assert.Equal(20m, fixture.Snapshot().Result!.Amount);
        Assert.False(fixture.App.IsRefreshing(ProviderId));
        fixture.App.RefreshProvider("missing");
        Assert.Equal(2, fixture.Fetcher.Count);
        Assert.Null(fixture.App.Get("other", "other-key").Result);
    }

    [Fact]
    public async Task BackgroundRefreshUsesOnlySuppliedCurrentAndReadyKeysAtThirtyMinuteBoundary()
    {
        await using var fixture = new Fixture(keyCount: 3);
        var current = new BalanceKey(ProviderId, "key-1");
        var ready = new BalanceKey(ProviderId, "key-2");
        var idle = new BalanceKey(ProviderId, "key-3");
        var route = fixture.Config.RouteFor(ClientType.Codex)!;
        route.CurrentProviderId = current.ProviderId;
        route.CurrentKeyId = current.KeyId;
        fixture.App.Synchronize(fixture.Config);
        fixture.App.RefreshClient(ClientType.Codex);
        for (var index = 0; index < 3; index++) await fixture.Complete(await fixture.Fetcher.Next());
        var firstCheckedAt = fixture.Snapshot(idle.KeyId).CheckedAt;
        var supplied = new[] { current, ready, ready, new BalanceKey("missing", "key") };

        fixture.Clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1));
        fixture.App.RefreshBackground(supplied);
        Assert.Equal(3, fixture.Fetcher.Count);
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        fixture.App.RefreshBackground(supplied);
        var refreshed = new[] { await fixture.Fetcher.Next(), await fixture.Fetcher.Next() };
        Assert.Equal(new[] { current, ready }.OrderBy(key => key.KeyId), refreshed.Select(call => call.Request.Key).OrderBy(key => key.KeyId));
        foreach (var call in refreshed) await fixture.Complete(call);
        Assert.Equal(5, fixture.Fetcher.Count);
        Assert.Equal(firstCheckedAt, fixture.Snapshot(idle.KeyId).CheckedAt);
        Assert.Equal(fixture.Clock.GetUtcNow(), fixture.Snapshot(current.KeyId).CheckedAt);
        Assert.Equal(fixture.Clock.GetUtcNow(), fixture.Snapshot(ready.KeyId).CheckedAt);
    }

    [Fact]
    public async Task AtMostFourQueriesRunAndRepeatedRefreshesDoNotDuplicateActiveOrQueuedKeys()
    {
        await using var fixture = new Fixture(keyCount: 7);
        fixture.App.RefreshClient(ClientType.Codex);
        Assert.Equal(4, fixture.Fetcher.Count);
        Assert.All(fixture.Provider.Keys, key => Assert.True(fixture.Snapshot(key.Id).IsRefreshing));

        fixture.App.RefreshClient(ClientType.Codex);
        fixture.App.RefreshProvider(ProviderId);
        fixture.App.RefreshBackground(fixture.Provider.Keys.Select(key => new BalanceKey(ProviderId, key.Id)));
        Assert.Equal(4, fixture.Fetcher.Count);
        for (var index = 0; index < 7; index++) await fixture.Complete(await fixture.Fetcher.Next());

        Assert.Equal(4, fixture.Fetcher.Peak);
        Assert.Equal(7, fixture.Fetcher.Count);
        Assert.Equal(7, fixture.Fetcher.Calls.Select(call => call.Request.Key).Distinct().Count());
        Assert.All(fixture.Provider.Keys, key =>
        {
            Assert.False(fixture.Snapshot(key.Id).IsRefreshing);
            Assert.NotNull(fixture.Snapshot(key.Id).Result);
        });
    }

    [Fact]
    public async Task DisabledQueryModeNeverQueuesFromAnyRefreshPath()
    {
        await using var fixture = new Fixture(mode: BalanceQueryMode.None);
        fixture.App.RefreshClient(ClientType.Codex);
        fixture.App.RefreshProvider(ProviderId);
        fixture.App.RefreshBackground(new[] { new BalanceKey(ProviderId, "key-1") });
        Assert.Equal(0, fixture.Fetcher.Count);
        Assert.Equal(BalanceSnapshot.Empty, fixture.Snapshot());
        Assert.False(fixture.App.IsRefreshing(ProviderId));
        Assert.False(fixture.App.Poll());
    }

    [Theory]
    [InlineData("key")]
    [InlineData("url")]
    [InlineData("mode")]
    [InlineData("disabled")]
    [InlineData("deleted-key")]
    public async Task SourceChangesCancelPendingQueriesAndLateResultsCannotReplaceTheNewSource(string change)
    {
        var remembered = new List<BalanceRequest>();
        await using var fixture = new Fixture(mode: BalanceQueryMode.Auto, remember: (request, _) =>
        {
            remembered.Add(request);
            return null;
        });
        fixture.App.RefreshProvider(ProviderId);
        await fixture.Complete(await fixture.Fetcher.Next());
        fixture.App.RefreshProvider(ProviderId);
        var stale = await fixture.Fetcher.Next();
        remembered.Clear();

        switch (change)
        {
            case "key": fixture.Provider.Keys[0].ApiKey = "sk-replacement"; break;
            case "url": fixture.Provider.BaseUrl = "https://replacement.example/v1"; break;
            case "mode": fixture.Provider.BalanceQuery.Mode = BalanceQueryMode.UserBalance; break;
            case "disabled": fixture.Provider.BalanceQuery.Mode = BalanceQueryMode.None; break;
            case "deleted-key": fixture.Provider.Keys.Clear(); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        fixture.App.Synchronize(fixture.Config);
        Assert.True(stale.Token.IsCancellationRequested);
        Assert.Null(fixture.Snapshot().Result);
        Assert.Null(fixture.Snapshot().CheckedAt);
        Assert.False(fixture.Snapshot().IsRefreshing);

        if (change == "deleted-key")
        {
            Assert.Equal(BalanceSnapshot.Empty, fixture.Snapshot());
            // 删除后恢复相同 ID、相同凭据，也必须建立新条目，不能接纳删除前的结果。
            fixture.Provider.Keys.Add(new ProviderKey { Id = "key-1", Name = "key-1", ApiKey = stale.Request.ApiKey });
            fixture.App.Synchronize(fixture.Config);
        }
        fixture.App.RefreshProvider(ProviderId);
        if (change != "disabled")
        {
            var replacement = await fixture.Fetcher.Next();
            await fixture.Complete(replacement, Success(22m, BalanceQueryMode.UserBalance));
            Assert.Equal(change == "mode" ? 0 : 1, remembered.Count);
        }
        else
        {
            Assert.Equal(2, fixture.Fetcher.Count);
            Assert.False(fixture.Snapshot().IsEnabled);
        }
        var expected = fixture.Snapshot();
        var expectedRemembered = remembered.Count;
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await fixture.Complete(stale, Success(999m, BalanceQueryMode.OpenAiBilling)));
        Assert.Equal(expected, fixture.Snapshot());
        Assert.Equal(expectedRemembered, remembered.Count);
    }

    [Fact]
    public async Task CancelingAQueuedKeySkipsItsFetchAndAllowsTheNextQueuedKeyToRun()
    {
        await using var fixture = new Fixture(keyCount: 6);
        fixture.App.RefreshProvider(ProviderId);
        var active = new[] { await fixture.Fetcher.Next(), await fixture.Fetcher.Next(), await fixture.Fetcher.Next(), await fixture.Fetcher.Next() };
        var previous = fixture.NotificationCount;
        fixture.Provider.Keys.RemoveAll(key => key.Id == "key-5");
        fixture.App.Synchronize(fixture.Config);
        await fixture.WaitForNotificationAfter(previous);
        fixture.App.Poll();
        Assert.Equal(BalanceSnapshot.Empty, fixture.Snapshot("key-5"));
        Assert.Equal(4, fixture.Fetcher.Count);

        await fixture.Complete(active[0]);
        var next = await fixture.Fetcher.Next();
        Assert.Equal("key-6", next.Request.Key.KeyId);
        foreach (var call in active.Skip(1).Append(next)) await fixture.Complete(call);
        Assert.Equal(5, fixture.Fetcher.Count);
        Assert.DoesNotContain(fixture.Fetcher.Calls, call => call.Request.Key.KeyId == "key-5");
        Assert.False(fixture.App.IsRefreshing(ProviderId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchExceptionsIncludingUnrequestedCancellationCannotExposeCredentials(bool cancellationException)
    {
        await using var fixture = new Fixture();
        fixture.App.RefreshProvider(ProviderId);
        var call = await fixture.Fetcher.Next();
        var sensitive = $"Bearer {call.Request.ApiKey} at {call.Request.BaseUrl}";
        Exception error = cancellationException ? new OperationCanceledException(sensitive) : new InvalidOperationException(sensitive);
        await fixture.NotifyCompletion(() => call.Fail(error));
        Assert.True(fixture.App.Poll());
        Assert.Equal(new BalanceResult(null, string.Empty, false, false, null, "余额查询失败，请检查网络与供应商设置"), fixture.Snapshot().Result);
        Assert.Equal(fixture.Clock.GetUtcNow(), fixture.Snapshot().CheckedAt);
        Assert.DoesNotContain(call.Request.ApiKey, fixture.Snapshot().ToString());
        Assert.DoesNotContain(call.Request.ApiKey, call.Request.ToString());
        Assert.DoesNotContain(call.Request.BaseUrl, call.Request.ToString());
    }

    [Theory]
    [InlineData(BalanceQueryMode.Usage)]
    [InlineData(BalanceQueryMode.UserBalance)]
    [InlineData(BalanceQueryMode.OpenAiBilling)]
    public async Task SuccessfulAutoDetectionIsRememberedOnlyWhenTheCompletionIsPolled(BalanceQueryMode detected)
    {
        var remembered = new List<(BalanceRequest Request, BalanceQueryMode Mode)>();
        await using var fixture = new Fixture(mode: BalanceQueryMode.Auto, remember: (request, mode) =>
        {
            remembered.Add((request, mode));
            return null;
        });
        fixture.App.RefreshProvider(ProviderId);
        var call = await fixture.Fetcher.Next();
        var result = Success(18m, detected);
        await fixture.NotifyCompletion(() => call.Complete(result));
        Assert.Empty(remembered);
        Assert.Null(fixture.Snapshot().Result);
        Assert.True(fixture.App.Poll());
        var saved = Assert.Single(remembered);
        Assert.Same(call.Request, saved.Request);
        Assert.Equal(detected, saved.Mode);
        Assert.Same(result, fixture.Snapshot().Result);
        Assert.Null(fixture.Snapshot().PersistenceError);
        Assert.False(fixture.App.Poll());
        Assert.Single(remembered);
    }

    [Theory]
    [InlineData(BalanceQueryMode.Usage, null, BalanceQueryMode.Usage, false)]
    [InlineData(BalanceQueryMode.Auto, BalanceQueryMode.Usage, BalanceQueryMode.Usage, false)]
    [InlineData(BalanceQueryMode.Auto, null, null, false)]
    [InlineData(BalanceQueryMode.Auto, null, BalanceQueryMode.Auto, false)]
    [InlineData(BalanceQueryMode.Auto, null, BalanceQueryMode.None, false)]
    [InlineData(BalanceQueryMode.Auto, null, BalanceQueryMode.Usage, true)]
    public async Task ExplicitKnownFailedOrNonConcreteDetectionDoesNotCallRemember(
        BalanceQueryMode queryMode, BalanceQueryMode? known, BalanceQueryMode? detected, bool failed)
    {
        var rememberCalls = 0;
        await using var fixture = new Fixture(mode: queryMode, remember: (_, _) => { rememberCalls++; return null; });
        fixture.Provider.BalanceQuery.Detected = known;
        fixture.App.Synchronize(fixture.Config);
        fixture.App.RefreshProvider(ProviderId);
        var result = new BalanceResult(failed ? null : 10m, "USD", false, false, detected, failed ? "余额查询失败" : null);
        await fixture.Complete(await fixture.Fetcher.Next(), result);
        Assert.Equal(0, rememberCalls);
        Assert.Same(result, fixture.Snapshot().Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectionSaveFailureKeepsTheBalanceAndUsesAFixedWarningUntilTheNextSuccess(bool throws)
    {
        var fail = true;
        await using var fixture = new Fixture(mode: BalanceQueryMode.Auto, remember: (request, _) =>
        {
            if (!fail) return null;
            var sensitive = $"写入失败：{request.ApiKey}，{request.BaseUrl}";
            if (throws) throw new InvalidOperationException(sensitive);
            return sensitive;
        });
        fixture.App.RefreshProvider(ProviderId);
        var call = await fixture.Fetcher.Next();
        var result = Success();
        await fixture.Complete(call, result);
        Assert.Same(result, fixture.Snapshot().Result);
        Assert.Equal(fixture.Clock.GetUtcNow(), fixture.Snapshot().CheckedAt);
        Assert.Equal("已查询余额，但识别方式保存失败", fixture.Snapshot().PersistenceError);
        Assert.DoesNotContain(call.Request.ApiKey, fixture.Snapshot().ToString());
        Assert.Null(fixture.Snapshot().Result!.Error);

        fail = false;
        fixture.App.RefreshProvider(ProviderId);
        await fixture.Complete(await fixture.Fetcher.Next(), Success(25m));
        Assert.Null(fixture.Snapshot().PersistenceError);
        Assert.Equal(25m, fixture.Snapshot().Result!.Amount);
    }

    [Fact]
    public async Task DetectedChangesKeepInFlightQueriesAndSuccessfulCacheButUpdateTheNextRequest()
    {
        var rememberCalls = 0;
        await using var fixture = new Fixture(mode: BalanceQueryMode.Auto, remember: (_, _) => { rememberCalls++; return null; });
        fixture.App.RefreshProvider(ProviderId);
        var call = await fixture.Fetcher.Next();
        fixture.Provider.BalanceQuery.Detected = BalanceQueryMode.Usage;
        fixture.App.Synchronize(fixture.Config);
        Assert.Null(call.Request.Query.Detected);
        Assert.False(call.Token.IsCancellationRequested);
        await fixture.Complete(call);
        Assert.Equal(0, rememberCalls);
        var cached = fixture.Snapshot();

        fixture.Provider.BalanceQuery.Detected = BalanceQueryMode.UserBalance;
        fixture.App.Synchronize(fixture.Config);
        Assert.Equal(cached, fixture.Snapshot());
        fixture.App.RefreshClient(ClientType.Codex);
        Assert.Equal(1, fixture.Fetcher.Count);
        fixture.App.RefreshProvider(ProviderId);
        var next = await fixture.Fetcher.Next();
        Assert.Equal(BalanceQueryMode.UserBalance, next.Request.Query.Detected);
        await fixture.Complete(next, Success(22m, BalanceQueryMode.UserBalance));
    }

    [Fact]
    public async Task BackgroundDeadlineTracksEachCompletionInsteadOfASlowFixedPollingInterval()
    {
        await using var fixture = new Fixture(keyCount: 2);
        var first = new BalanceKey(ProviderId, "key-1");
        var second = new BalanceKey(ProviderId, "key-2");
        var keys = new[] { first, second, new BalanceKey("missing", "missing") };
        Assert.Equal(fixture.Clock.GetUtcNow(), fixture.App.NextBackgroundRefreshAt(keys));
        fixture.App.RefreshBackground(keys);
        Assert.Null(fixture.App.NextBackgroundRefreshAt(keys));
        var firstCall = await fixture.Fetcher.Next();
        var secondCall = await fixture.Fetcher.Next();
        await fixture.Complete(firstCall);
        var firstDue = fixture.Clock.GetUtcNow() + TimeSpan.FromMinutes(30);
        Assert.Equal(firstDue, fixture.App.NextBackgroundRefreshAt(keys));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Complete(secondCall);
        Assert.Equal(firstDue, fixture.App.NextBackgroundRefreshAt(keys));
        fixture.Clock.Advance(TimeSpan.FromMinutes(24));
        fixture.App.RefreshProvider(ProviderId);
        await fixture.Complete(await fixture.Fetcher.Next());
        await fixture.Complete(await fixture.Fetcher.Next());
        Assert.Equal(firstDue + TimeSpan.FromMinutes(25), fixture.App.NextBackgroundRefreshAt(keys));
        fixture.Provider.BalanceQuery.Mode = BalanceQueryMode.None;
        fixture.App.Synchronize(fixture.Config);
        Assert.Null(fixture.App.NextBackgroundRefreshAt(keys));
    }

    [Fact]
    public async Task DisposeCancelsActiveAndQueuedQueriesWithoutWaitingOrPublishingLateResults()
    {
        var rememberCalls = 0;
        await using var fixture = new Fixture(keyCount: 5, mode: BalanceQueryMode.Auto,
            remember: (_, _) => { rememberCalls++; return null; });
        fixture.App.RefreshProvider(ProviderId);
        var active = new[] { await fixture.Fetcher.Next(), await fixture.Fetcher.Next(), await fixture.Fetcher.Next(), await fixture.Fetcher.Next() };
        fixture.App.Dispose();
        var notificationsAfterDispose = fixture.NotificationCount;
        Assert.All(active, call =>
        {
            Assert.True(call.Token.IsCancellationRequested);
            Assert.False(call.Response.Task.IsCompleted);
        });
        fixture.App.Dispose();
        fixture.App.Synchronize(fixture.Config);
        fixture.App.RefreshClient(ClientType.Codex);
        fixture.App.RefreshProvider(ProviderId);
        fixture.App.RefreshBackground(new[] { new BalanceKey(ProviderId, "key-1") });
        foreach (var call in active) call.Complete(Success());
        await Task.WhenAll(active.Select(call => call.Finished.Task)).WaitAsync(GateTimeout);
        Assert.False(fixture.App.Poll());
        Assert.Equal(4, fixture.Fetcher.Count);
        Assert.Equal(0, rememberCalls);
        Assert.Equal(notificationsAfterDispose, fixture.NotificationCount);
        Assert.False(fixture.App.IsRefreshing(ProviderId));
        Assert.All(fixture.Provider.Keys, key => Assert.Equal(BalanceSnapshot.Empty, fixture.Snapshot(key.Id)));
    }

    [Fact]
    public void DisposeReleasesCancellationHandlesForBothPresentAndAlreadyRemovedEntries()
    {
        var clock = new Clock();
        var tokens = new List<CancellationToken>();
        var handles = new List<WaitHandle>();
        using var workspace = new BalanceWorkspace((_, token) =>
        {
            tokens.Add(token);
            handles.Add(token.WaitHandle);
            return Task.FromResult(Success());
        }, timeProvider: clock);
        var config = ProxyConfig.Builtin();
        config.Providers.Clear();
        config.Providers.Add(CreateProvider(ProviderId, ClientType.Codex, "kept", "removed"));
        workspace.Synchronize(config);
        workspace.RefreshProvider(ProviderId);
        Assert.Equal(2, tokens.Count);
        config.Providers[0].Keys.RemoveAll(key => key.Id == "removed");
        workspace.Synchronize(config);

        // 同步完成但尚未 Poll 的结果仍持有取消源；删除条目的取消源也必须在退出时释放。
        workspace.Dispose();
        workspace.Dispose();
        Assert.All(tokens, token =>
        {
            Assert.True(token.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
        });
        Assert.All(handles, handle => Assert.True(handle.SafeWaitHandle.IsClosed));
        Assert.False(workspace.Poll());
    }
}
