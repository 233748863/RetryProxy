using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Internal;
using RetryProxy.Core.KeepAlive;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>对应 keepalive.rs 里的单元测试：准备状态机、会话复用、指定 Key 与界面通知。</summary>
public class KeepAliveWatchdogTests
{
    private static (KeepAliveWatchdog Watchdog, IDisposable Service) Running(bool enabled)
    {
        var watchdog = new KeepAliveWatchdog(enabled, TimeSpan.FromSeconds(180));
        var service = watchdog.RegisterService(KeepAliveFlavor.Codex);
        return (watchdog, service);
    }

    private static CliCredential SuppliedKey() => CliCredential.Create("sk-test-secret", "http://127.0.0.1:18081");

    [Fact]
    public void PreparationNeedsNoUserHistoryAndDoesNotEnableAutomaticMode()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        Assert.True(watchdog.RequestPreparation());
        Assert.False(watchdog.RequestPreparation());
        var probe = watchdog.BeginDueProbe()!;
        Assert.Equal(KeepAliveFlavor.Codex, probe.Flavor);
        Assert.Equal(1, probe.Turn);
        Assert.NotNull(probe.Complete("cli-model", 120));
        probe.Dispose();
        Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
        Assert.False(watchdog.Enabled);
        Assert.Equal(1, watchdog.Snapshot().Turns);
        Assert.Equal("cli-model", watchdog.Snapshot().Model);
    }

    [Fact]
    public void AutomaticModeRequiresARunningChannelAndTheIdleThreshold()
    {
        var watchdog = new KeepAliveWatchdog(true, TimeSpan.FromSeconds(180));
        watchdog.MakeDueForTest();
        Assert.Null(watchdog.BeginDueProbe());
        Assert.Throws<InvalidOperationException>(() => watchdog.RequestPreparation());
        using var service = watchdog.RegisterService(KeepAliveFlavor.Codex);
        Assert.Null(watchdog.BeginDueProbe());
        watchdog.MakeDueForTest();
        Assert.NotNull(watchdog.BeginDueProbe());
    }

    [Fact]
    public void FailedRealRequestsStillSetTheCliAndRestartTheIdleWindow()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.RequestStarted(KeepAliveFlavor.Claude);
        watchdog.MakeDueForTest();
        Assert.Null(watchdog.BeginDueProbe());
        watchdog.RequestFinished();
        Assert.Null(watchdog.BeginDueProbe());
        watchdog.MakeDueForTest();
        Assert.Equal(KeepAliveFlavor.Codex, watchdog.BeginDueProbe()!.Flavor);
    }

    [Fact]
    public void PreparationWaitsForAllRealRequestsToFinish()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestStarted(KeepAliveFlavor.Codex);
        watchdog.RequestStarted(KeepAliveFlavor.Unknown);
        Assert.True(watchdog.RequestPreparation());
        watchdog.RequestFinished();
        Assert.Null(watchdog.BeginDueProbe());
        watchdog.RequestFinished();
        Assert.NotNull(watchdog.BeginDueProbe());
    }

    [Fact]
    public void RealRequestsPreserveAnAlreadyCompletedProbeBeforeItIsDropped()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestPreparation();
        var probe = watchdog.BeginDueProbe()!;
        Assert.NotNull(probe.Complete("test-model", 52));
        Assert.False(watchdog.Snapshot().Probing);
        watchdog.RequestStarted(KeepAliveFlavor.Codex);
        Assert.False(probe.Cancel.IsCancellationRequested);
        Assert.Equal(probe.SessionId, watchdog.Snapshot().SessionId);
        Assert.Null(probe.Complete(null, 99));
        probe.Fail("late failure");
        probe.Interrupt("late interruption");
        probe.Dispose();
        var snapshot = watchdog.Snapshot();
        Assert.Equal(1, snapshot.Turns);
        Assert.Equal(new KeepAliveTotals(1, 0, 0), snapshot.Totals);
        Assert.Equal(new KeepAliveSuccess { Model = "test-model", ContextTokens = 52 }, snapshot.LastSuccess);
        Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
        watchdog.RequestFinished();
    }

    [Fact]
    public void TotalsAndLastSuccessSurviveNewSessionsFailuresAndInterruptions()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.MakeDueForTest();
        var first = watchdog.BeginDueProbe()!;
        Assert.NotNull(first.Complete(null, 80));
        first.Dispose();
        watchdog.SetContextLimit(79);
        Assert.Null(watchdog.Snapshot().SessionId);
        watchdog.MakeDueForTest();
        var second = watchdog.BeginDueProbe()!;
        second.Fail("fixture failure");
        second.Fail("duplicate failure");
        second.Dispose();
        watchdog.MakeDueForTest();
        var third = watchdog.BeginDueProbe()!;
        watchdog.RequestStarted(KeepAliveFlavor.Codex);
        Assert.Null(third.Complete(null, 90));
        third.Interrupt("real request");
        third.Interrupt("duplicate interruption");
        third.Dispose();
        watchdog.RequestFinished();
        watchdog.Configure(false, TimeSpan.FromSeconds(180));
        var snapshot = watchdog.Snapshot();
        Assert.Equal(new KeepAliveTotals(1, 1, 1), snapshot.Totals);
        Assert.Equal(80UL, snapshot.LastSuccess!.ContextTokens);
        Assert.Equal(0, snapshot.Turns);
        Assert.Null(snapshot.ContextTokens);
    }

    [Fact]
    public void DroppingAnUnfinishedProbeRecordsOneInterruption()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.MakeDueForTest();
        var probe = watchdog.BeginDueProbe()!;
        watchdog.RequestStarted(KeepAliveFlavor.Codex);
        probe.Dispose();
        Assert.Equal(new KeepAliveTotals(0, 0, 1), watchdog.Snapshot().Totals);
        watchdog.RequestFinished();
    }

    [Fact]
    public void RealRequestsCancelInflightConversationsAndClearPartialHistory()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.RequestPreparation();
        var probe = watchdog.BeginDueProbe()!;
        watchdog.RequestStarted(KeepAliveFlavor.Codex);
        Assert.True(probe.Cancel.IsCancellationRequested);
        Assert.Null(probe.Complete(null, 10));
        Assert.Null(watchdog.Snapshot().SessionId);
        probe.Dispose();
        Assert.Null(watchdog.TakePreparationResult());
        Assert.True(watchdog.Snapshot().Preparing);
        watchdog.RequestFinished();
        Assert.Null(watchdog.BeginDueProbe());
        watchdog.SetPreparationRetryNowForTest();
        Assert.NotNull(watchdog.BeginDueProbe());
    }

    [Fact]
    public void OnlyOneProbeRunsPerProvider()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.MakeDueForTest();
        var probe = watchdog.BeginDueProbe()!;
        watchdog.MakeDueForTest();
        Assert.Null(watchdog.BeginDueProbe());
        Assert.True(watchdog.RequestPreparation());
        Assert.NotNull(probe.Complete(null, 10));
        probe.Dispose();
        Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
    }

    [Fact]
    public void SessionReusesLatestContextAndRollsOverOnlyAboveConfiguredLimit()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.SetContextLimit(100);
        var sessionId = string.Empty;
        var tokens = new ulong[] { 80, 100, 101 };
        for (var round = 0; round < tokens.Length; round++)
        {
            watchdog.RequestPreparation();
            var probe = watchdog.BeginDueProbe()!;
            if (sessionId.Length == 0)
            {
                sessionId = probe.SessionId;
            }

            Assert.Equal(sessionId, probe.SessionId);
            Assert.Equal(round + 1, probe.Turn);
            var completion = probe.Complete(null, tokens[round])!;
            Assert.Equal(tokens[round] > 100, completion.ResetReason is not null);
            probe.Dispose();
            Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
        }

        Assert.Null(watchdog.Snapshot().SessionId);
        watchdog.RequestPreparation();
        var fresh = watchdog.BeginDueProbe()!;
        Assert.NotEqual(sessionId, fresh.SessionId);
        Assert.Equal(1, fresh.Turn);
    }

    [Fact]
    public void MissingUsageResetsTheSessionWithoutFabricatingTokens()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestPreparation();
        var probe = watchdog.BeginDueProbe()!;
        var completed = probe.Complete(null, null)!;
        Assert.Null(completed.ContextTokens);
        Assert.NotNull(completed.ResetReason);
        probe.Dispose();
        Assert.Null(watchdog.Snapshot().SessionId);
        Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
        var snapshot = watchdog.Snapshot();
        Assert.Equal(1UL, snapshot.Totals.Completed);
        Assert.Equal(0UL, snapshot.Totals.Interrupted);
        Assert.Null(snapshot.LastSuccess!.ContextTokens);
    }

    [Fact]
    public void LoweringTheLimitClearsAnOversizedIdleSession()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.MakeDueForTest();
        var probe = watchdog.BeginDueProbe()!;
        Assert.NotNull(probe.Complete(null, 100));
        probe.Dispose();
        watchdog.SetContextLimit(99);
        Assert.Null(watchdog.Snapshot().SessionId);
        Assert.Equal(99UL, watchdog.Snapshot().ContextLimit);
    }

    [Fact]
    public void ConfiguredCliProtocolDoesNotChangeForAnotherRequestPath()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.MakeDueForTest();
        var probe = watchdog.BeginDueProbe()!;
        Assert.NotNull(probe.Complete(null, 100));
        probe.Dispose();
        watchdog.RequestStarted(KeepAliveFlavor.Unknown);
        watchdog.RequestFinished();
        Assert.NotNull(watchdog.Snapshot().SessionId);
        watchdog.RequestStarted(KeepAliveFlavor.Claude);
        watchdog.RequestFinished();
        Assert.NotNull(watchdog.Snapshot().SessionId);
        Assert.Equal(KeepAliveFlavor.Codex, watchdog.Snapshot().Flavor);
    }

    [Fact]
    public void ChangingAChannelCliProtocolClearsItsOldSession()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.MakeDueForTest();
        var probe = watchdog.BeginDueProbe()!;
        Assert.NotNull(probe.Complete(null, 100));
        probe.Dispose();
        Assert.NotNull(watchdog.Snapshot().SessionId);
        watchdog.ConfigureFlavor(KeepAliveFlavor.Claude);
        Assert.Null(watchdog.Snapshot().SessionId);
        Assert.Equal(KeepAliveFlavor.Claude, watchdog.Snapshot().Flavor);
    }

    [Fact]
    public void LastServiceStopCancelsTheProbeButOtherRunningChannelsKeepItAlive()
    {
        var (watchdog, firstService) = Running(true);
        var secondService = watchdog.RegisterService(KeepAliveFlavor.Codex);
        watchdog.RequestPreparation();
        var probe = watchdog.BeginDueProbe()!;
        firstService.Dispose();
        Assert.False(probe.Cancel.IsCancellationRequested);
        secondService.Dispose();
        Assert.True(probe.Cancel.IsCancellationRequested);
        probe.Dispose();
        Assert.IsType<PreparationResult.Failed>(watchdog.TakePreparationResult());
        Assert.Null(watchdog.TakePreparationResult());
    }

    [Fact]
    public void DisablingKeepaliveReportsCancellationOnce()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.RequestPreparation();
        var probe = watchdog.BeginDueProbe()!;
        watchdog.Configure(false, TimeSpan.FromSeconds(180));
        Assert.True(probe.Cancel.IsCancellationRequested);
        probe.Dispose();
        Assert.IsType<PreparationResult.Failed>(watchdog.TakePreparationResult());
        Assert.Null(watchdog.TakePreparationResult());
        Assert.Null(watchdog.Snapshot().SessionId);
    }

    [Fact]
    public void FailuresStartNewSessionsAndPickNewQuestions()
    {
        var counter = -1;
        var watchdog = new KeepAliveWatchdog(false, TimeSpan.FromSeconds(180)) { QuestionPicker = () => Interlocked.Increment(ref counter) };
        using var service = watchdog.RegisterService(KeepAliveFlavor.Codex);
        watchdog.RequestPreparation();
        var first = watchdog.BeginDueProbe()!;
        var session = first.SessionId;
        Assert.Equal(0, first.QuestionIndex);
        first.Fail("fixture failure");
        first.Dispose();
        Assert.False(watchdog.RequestPreparation());
        Assert.Null(watchdog.BeginDueProbe());
        watchdog.SetPreparationRetryNowForTest();
        var second = watchdog.BeginDueProbe()!;
        Assert.NotEqual(session, second.SessionId);
        Assert.Equal(1, second.QuestionIndex);
        Assert.Equal(2UL, watchdog.Snapshot().PreparationAttempts);
    }

    [Fact]
    public void PreparationRetryJitterVariesInMillisecondsAroundTwoSeconds()
    {
        var random = new Random(42);
        var delays = new List<TimeSpan>();
        for (var index = 0; index < 32; index++)
        {
            delays.Add(KeepAliveWatchdog.PreparationRetryDelay(random));
        }

        Assert.All(delays, delay =>
        {
            Assert.InRange(delay, TimeSpan.FromMilliseconds(1_500), TimeSpan.FromMilliseconds(2_500));
            Assert.Equal(0, delay.Ticks % TimeSpan.TicksPerMillisecond);
        });
        Assert.Contains(delays, delay => delay < TimeSpan.FromSeconds(2));
        Assert.Contains(delays, delay => delay > TimeSpan.FromSeconds(2));
        Assert.Contains(delays, delay => (long)delay.TotalMilliseconds % 100 != 0);
    }

    [Fact]
    public void PreparationRetriesFailuresUntilACompleteReply()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestPreparation();
        for (var attempt = 1UL; attempt <= 4; attempt++)
        {
            var probe = watchdog.BeginDueProbe()!;
            Assert.Equal(attempt, watchdog.Snapshot().PreparationAttempts);
            probe.Fail("temporary failure");
            probe.Dispose();
            Assert.True(watchdog.Snapshot().Preparing);
            Assert.Null(watchdog.TakePreparationResult());
            var retryAfter = watchdog.PreparationRetryAfterForTest!.Value;
            Assert.InRange(retryAfter, TimeSpan.FromMilliseconds(1_400), TimeSpan.FromMilliseconds(2_500));
            Assert.Null(watchdog.BeginDueProbe());
            watchdog.SetPreparationRetryNowForTest();
        }

        var success = watchdog.BeginDueProbe()!;
        Assert.NotNull(success.Complete(null, 52));
        success.Dispose();
        Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
        Assert.False(watchdog.Snapshot().Preparing);
        Assert.Equal(4UL, watchdog.Snapshot().Totals.Failed);
        Assert.Equal(1UL, watchdog.Snapshot().Totals.Completed);
        Assert.False(watchdog.Enabled);
    }

    [Fact]
    public void UserCanCancelQueuedRunningAndRetryingPreparations()
    {
        foreach (var stage in new[] { "queued", "running", "retrying" })
        {
            var (watchdog, service) = Running(false);
            using var _ = service;
            watchdog.RequestPreparation();
            var probe = stage != "queued" ? watchdog.BeginDueProbe() : null;
            if (stage == "retrying")
            {
                probe!.Fail("temporary failure");
                probe.Dispose();
                probe = null;
            }

            Assert.True(watchdog.CancelPreparation());
            Assert.False(watchdog.CancelPreparation());
            if (probe is not null)
            {
                Assert.True(probe.Cancel.IsCancellationRequested);
                Assert.Null(probe.Complete(null, 52));
            }

            probe?.Dispose();
            Assert.IsType<PreparationResult.Cancelled>(watchdog.TakePreparationResult());
            Assert.Null(watchdog.TakePreparationResult());
            Assert.False(watchdog.Snapshot().Preparing);
            Assert.Null(watchdog.Snapshot().PreparationRetryAfter);
            watchdog.MakeDueForTest();
            Assert.Null(watchdog.BeginDueProbe());
        }
    }

    [Fact]
    public void SuppliedKeyPreparationSwitchesAutomaticKeepaliveToThatKey()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.MakeDueForTest();
        var defaultProbe = watchdog.BeginDueProbe()!;
        Assert.False(defaultProbe.UsesSuppliedKey);
        Assert.NotNull(defaultProbe.Complete(null, 40));
        defaultProbe.Dispose();
        var defaultSession = watchdog.Snapshot().SessionId!;

        Assert.True(watchdog.RequestPreparationWith(SuppliedKey()));
        Assert.True(watchdog.Snapshot().WithKey);
        // 默认配置的会话不能被指定 Key 复用。
        Assert.Null(watchdog.Snapshot().SessionId);
        var keyProbe = watchdog.BeginDueProbe()!;
        Assert.True(keyProbe.UsesSuppliedKey);
        Assert.NotEqual(defaultSession, keyProbe.SessionId);
        var keySession = keyProbe.SessionId;
        Assert.NotNull(keyProbe.Complete(null, 52));
        keyProbe.Dispose();
        Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
        var snapshot = watchdog.Snapshot();
        Assert.False(snapshot.Preparing);
        Assert.True(snapshot.WithKey);
        // 准备成功后会话保留，之后的自动保活继续用这把 Key 和同一会话。
        Assert.Equal(keySession, snapshot.SessionId);
        Assert.Equal(2UL, snapshot.Totals.Completed);
        Assert.Equal(52UL, snapshot.LastSuccess!.ContextTokens);
        watchdog.MakeDueForTest();
        var next = watchdog.BeginDueProbe()!;
        Assert.True(next.UsesSuppliedKey);
        Assert.Equal(SuppliedKey(), next.Credential);
        Assert.Equal(keySession, next.SessionId);
        Assert.Equal(2, next.Turn);
        next.Dispose();

        // 再用默认配置准备一次：切回本机配置，Key 会话丢弃。
        Assert.True(watchdog.RequestPreparationWith(null));
        Assert.False(watchdog.Snapshot().WithKey);
        Assert.Null(watchdog.Snapshot().SessionId);
        var back = watchdog.BeginDueProbe()!;
        Assert.False(back.UsesSuppliedKey);
        Assert.NotNull(back.Complete(null, 10));
        back.Dispose();
        watchdog.MakeDueForTest();
        Assert.False(watchdog.BeginDueProbe()!.UsesSuppliedKey);
    }

    [Fact]
    public void SuppliedKeyPreparationRetriesWithTheSameKeyAndKeepsItWhenCancelled()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestPreparationWith(SuppliedKey());
        var first = watchdog.BeginDueProbe()!;
        Assert.True(first.UsesSuppliedKey);
        first.Fail("temporary failure");
        first.Dispose();
        Assert.True(watchdog.Snapshot().Preparing);
        Assert.True(watchdog.Snapshot().WithKey);
        watchdog.SetPreparationRetryNowForTest();
        var second = watchdog.BeginDueProbe()!;
        Assert.True(second.UsesSuppliedKey);
        Assert.Equal(SuppliedKey(), second.Credential);
        Assert.True(watchdog.CancelPreparation());
        Assert.True(second.Cancel.IsCancellationRequested);
        second.Dispose();
        Assert.IsType<PreparationResult.Cancelled>(watchdog.TakePreparationResult());
        // 终止只结束本次准备；通道仍记住用户选择的 Key，开启自动保活时继续用它。
        Assert.True(watchdog.Snapshot().WithKey);
        Assert.Null(watchdog.Snapshot().SessionId);
        watchdog.Configure(true, TimeSpan.FromSeconds(180));
        watchdog.MakeDueForTest();
        Assert.True(watchdog.BeginDueProbe()!.UsesSuppliedKey);
    }

    [Fact]
    public void PlainPreparationKeepsTheCurrentKeyInsteadOfResettingToDefault()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestPreparationWith(SuppliedKey());
        var first = watchdog.BeginDueProbe()!;
        var session = first.SessionId;
        Assert.NotNull(first.Complete(null, 10));
        first.Dispose();
        watchdog.TakePreparationResult();
        // 主按钮走的是不带参数的 RequestPreparation：沿用 Key 和会话，不切回默认。
        Assert.True(watchdog.RequestPreparation());
        Assert.True(watchdog.Snapshot().WithKey);
        var second = watchdog.BeginDueProbe()!;
        Assert.True(second.UsesSuppliedKey);
        Assert.Equal(session, second.SessionId);
        Assert.Equal(2, second.Turn);
    }

    [Fact]
    public void RepeatingTheSameKeyReusesTheExistingSession()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestPreparationWith(SuppliedKey());
        var first = watchdog.BeginDueProbe()!;
        var session = first.SessionId;
        Assert.NotNull(first.Complete(null, 10));
        first.Dispose();
        watchdog.TakePreparationResult();
        watchdog.RequestPreparationWith(SuppliedKey());
        var second = watchdog.BeginDueProbe()!;
        Assert.Equal(session, second.SessionId);
        Assert.Equal(2, second.Turn);
        Assert.NotNull(second.Complete(null, 20));
        second.Dispose();
        watchdog.TakePreparationResult();
        // 换一把不同的 Key 则新建会话。
        var other = CliCredential.Create("sk-other", "http://127.0.0.1:18081");
        watchdog.RequestPreparationWith(other);
        Assert.Null(watchdog.Snapshot().SessionId);
        var third = watchdog.BeginDueProbe()!;
        Assert.NotEqual(session, third.SessionId);
        Assert.Equal(other, third.Credential);
    }

    [Fact]
    public void SuppliedKeyPreparationDoesNotReuseAnInflightDefaultProbe()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.MakeDueForTest();
        var defaultProbe = watchdog.BeginDueProbe()!;
        Assert.True(watchdog.RequestPreparationWith(SuppliedKey()));
        Assert.True(defaultProbe.Cancel.IsCancellationRequested);
        Assert.Null(defaultProbe.Complete(null, 10));
        defaultProbe.Dispose();
        Assert.True(watchdog.Snapshot().Preparing);
        Assert.Null(watchdog.TakePreparationResult());
        watchdog.SetPreparationRetryNowForTest();
        var keyProbe = watchdog.BeginDueProbe()!;
        Assert.True(keyProbe.UsesSuppliedKey);
        Assert.NotEqual(string.Empty, keyProbe.SessionId);
    }

    [Fact]
    public void DefaultPreparationStillReusesAnInflightDefaultProbe()
    {
        var (watchdog, service) = Running(true);
        using var _ = service;
        watchdog.MakeDueForTest();
        var probe = watchdog.BeginDueProbe()!;
        Assert.True(watchdog.RequestPreparationWith(null));
        Assert.False(probe.Cancel.IsCancellationRequested);
        Assert.False(watchdog.Snapshot().WithKey);
        Assert.NotNull(probe.Complete(null, 10));
        probe.Dispose();
        Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
    }

    [Fact]
    public void CancellingAfterSuccessKeepsTheConfirmedResult()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestPreparation();
        var probe = watchdog.BeginDueProbe()!;
        Assert.NotNull(probe.Complete(null, 52));
        Assert.False(watchdog.CancelPreparation());
        probe.Dispose();
        Assert.IsType<PreparationResult.Ready>(watchdog.TakePreparationResult());
        Assert.Null(watchdog.TakePreparationResult());
        Assert.Equal(1UL, watchdog.Snapshot().Totals.Completed);
    }

    [Fact]
    public async Task PreparationRetryHasItsOwnWakeup()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestPreparation();
        var probe = watchdog.BeginDueProbe()!;
        probe.Fail("temporary failure");
        probe.Dispose();
        await watchdog.PreparationRequestedAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(KeepAliveWatchdog.PreparationRetryMaxDelay + TimeSpan.FromSeconds(1));
        await watchdog.PreparationRequestedAsync(timeout.Token);
        Assert.NotNull(watchdog.BeginDueProbe());
    }

    [Fact]
    public void OnlyRegisteredCliMarkersAreRecognizedAsBackgroundRequests()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        var headers = new HeaderList();
        headers.Set("x-retry-keepalive", "1");
        Assert.Null(InternalSessions.RequestCancel(headers));
        watchdog.RequestPreparation();
        var probe = watchdog.BeginDueProbe()!;
        headers.Set("x-retry-keepalive", probe.SessionId);
        Assert.NotNull(InternalSessions.RequestCancel(headers));
        headers.Remove("x-retry-keepalive");
        headers.Set("x-codex-turn-metadata", new JsonObject { ["retry_proxy_keepalive"] = probe.SessionId }.ToJsonString());
        Assert.NotNull(InternalSessions.RequestCancel(headers));
        probe.Fail("finished");
        probe.Dispose();
        Assert.Null(InternalSessions.RequestCancel(headers));
    }

    [Fact]
    public void RegisteredBodyMetadataUsesTheSameConversationCancellation()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestPreparation();
        var probe = watchdog.BeginDueProbe()!;
        var body = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["client_metadata"] = new JsonObject
            {
                ["x-codex-turn-metadata"] = new JsonObject { ["retry_proxy_keepalive"] = probe.SessionId, ["thread_id"] = "fixture-thread" }.ToJsonString(),
            },
        }.ToJsonString());
        var cancel = InternalSessions.BodyRequestCancel(body)!.Value;
        Assert.False(cancel.IsCancellationRequested);
        probe.Fail("finished");
        probe.Dispose();
        Assert.True(cancel.IsCancellationRequested);
        Assert.Null(InternalSessions.BodyRequestCancel(body));
    }

    [Fact]
    public void BodyMarkersRequireValidRegisteredTurnMetadata()
    {
        var (watchdog, service) = Running(false);
        using var _ = service;
        watchdog.RequestPreparation();
        var probe = watchdog.BeginDueProbe()!;
        foreach (var body in new JsonObject[]
                 {
                     new() { ["input"] = new JsonObject { ["retry_proxy_keepalive"] = probe.SessionId } },
                     new() { ["client_metadata"] = new JsonObject { ["retry_proxy_keepalive"] = probe.SessionId } },
                     new() { ["client_metadata"] = new JsonObject { ["x-codex-turn-metadata"] = new JsonObject { ["retry_proxy_keepalive"] = probe.SessionId } } },
                     new() { ["client_metadata"] = new JsonObject { ["x-codex-turn-metadata"] = "invalid" } },
                     new() { ["client_metadata"] = new JsonObject { ["x-codex-turn-metadata"] = new JsonObject { ["retry_proxy_keepalive"] = "unknown" }.ToJsonString() } },
                     new() { ["client_metadata"] = new JsonObject { ["x-codex-turn-metadata"] = new JsonObject { ["retry_proxy_keepalive"] = 1 }.ToJsonString() } },
                     new() { ["client_metadata"] = null },
                 })
        {
            Assert.Null(InternalSessions.BodyRequestCancel(Encoding.UTF8.GetBytes(body.ToJsonString())));
        }

        Assert.Null(InternalSessions.BodyRequestCancel(Encoding.UTF8.GetBytes("invalid JSON")));
        Assert.False(probe.Cancel.IsCancellationRequested);
        probe.Dispose();
    }

    [Fact]
    public void PreparationAndProbeLifecycleNotifyUiWithoutHoldingStateLock()
    {
        var (watchdog, service) = Running(false);
        var count = 0;
        watchdog.SetUiNotifier(() =>
        {
            // 回调里再取一次快照：若通知时仍持有状态锁，这里会死锁。
            _ = watchdog.Snapshot();
            Interlocked.Increment(ref count);
        });
        int Notified() => Volatile.Read(ref count);
        watchdog.Configure(true, TimeSpan.FromSeconds(60));
        var afterConfigure = Notified();
        Assert.True(afterConfigure >= 1);
        Assert.True(watchdog.RequestPreparation());
        var afterRequest = Notified();
        Assert.True(afterRequest > afterConfigure);
        var probe = watchdog.BeginDueProbe()!;
        var afterBegin = Notified();
        Assert.True(afterBegin > afterRequest);
        Assert.NotNull(probe.Complete(null, 12));
        probe.Dispose();
        var afterComplete = Notified();
        Assert.True(afterComplete > afterBegin);
        service.Dispose();
        Assert.True(Notified() > afterComplete);
    }

    [Fact]
    public void JavaQuestionsAreUniqueAndReadyToAsk()
    {
        Assert.Equal(250, JavaQuestions.Count);
        Assert.Equal(250, new HashSet<string>(JavaQuestions.All).Count);
        Assert.All(JavaQuestions.All, question =>
        {
            Assert.EndsWith("？", question);
            Assert.True(Encoding.UTF8.GetByteCount(question) < 256);
        });
    }
}
