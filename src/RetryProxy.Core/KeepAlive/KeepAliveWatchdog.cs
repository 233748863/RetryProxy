using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.KeepAlive;

public struct KeepAliveTotals : IEquatable<KeepAliveTotals>
{
    public ulong Completed;
    public ulong Failed;
    public ulong Interrupted;

    public KeepAliveTotals(ulong completed, ulong failed, ulong interrupted)
    {
        Completed = completed;
        Failed = failed;
        Interrupted = interrupted;
    }

    public bool Equals(KeepAliveTotals other) => Completed == other.Completed && Failed == other.Failed && Interrupted == other.Interrupted;

    public override bool Equals(object? obj) => obj is KeepAliveTotals other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Completed, Failed, Interrupted);

    public override string ToString() => $"completed {Completed}, failed {Failed}, interrupted {Interrupted}";
}

public sealed class KeepAliveSuccess : IEquatable<KeepAliveSuccess>
{
    public string? Model { get; init; }

    public ulong? ContextTokens { get; init; }

    public bool Equals(KeepAliveSuccess? other) => other is not null && Model == other.Model && ContextTokens == other.ContextTokens;

    public override bool Equals(object? obj) => Equals(obj as KeepAliveSuccess);

    public override int GetHashCode() => HashCode.Combine(Model, ContextTokens);
}

public sealed class KeepAliveSnapshot
{
    public ReasoningEffort ReasoningEffort { get; init; }

    public KeepAliveFlavor Flavor { get; init; } = KeepAliveFlavor.Unknown;

    public string? Model { get; init; }

    public string? SessionId { get; init; }

    public int Turns { get; init; }

    public ulong? ContextTokens { get; init; }

    public ulong ContextLimit { get; init; }

    public TimeSpan Idle { get; init; }

    public KeepAliveTotals Totals { get; init; }

    public KeepAliveSuccess? LastSuccess { get; init; }

    public ulong ActiveRequests { get; init; }

    public bool Probing { get; init; }

    public bool Preparing { get; init; }

    public bool Enabled { get; init; }

    /// <summary>本通道当前的准备与自动保活是否使用用户输入的 Key，而不是本机 CLI 默认配置。</summary>
    public bool WithKey { get; init; }

    public ulong PreparationAttempts { get; init; }

    public TimeSpan? PreparationRetryAfter { get; init; }

    public string? PreparationLastError { get; init; }
}

/// <summary>一次准备的结局（对应 PreparationResult）。</summary>
public abstract record PreparationResult
{
    public sealed record Ready : PreparationResult;

    public sealed record Failed(string Reason) : PreparationResult;

    public sealed record Cancelled : PreparationResult;
}

/// <summary>
/// 一个后台对话：全局登记的会话标记 + 取消令牌 + CLI 子进程。会话与探测各持一份引用，全部释放时取消并注销。
/// </summary>
internal sealed class Conversation
{
    private int _references = 1;
    private readonly CancellationTokenSource _cancel = new();

    public Conversation()
    {
        Id = Guid.NewGuid().ToString();
        InternalSessions.Register(Id, _cancel);
    }

    public string Id { get; }

    public CancellationToken Cancel => _cancel.Token;

    public SemaphoreSlim ProcessLock { get; } = new(1, 1);

    public CliSession? Process { get; set; }

    public void CancelConversation()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public Conversation Retain()
    {
        Interlocked.Increment(ref _references);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0)
        {
            return;
        }

        CancelConversation();
        InternalSessions.Unregister(Id);
        var process = Process;
        Process = null;
        process?.Dispose();
    }
}

/// <summary>探测完成时的结论。</summary>
public sealed class KeepAliveCompletion
{
    public ulong? ContextTokens { get; init; }

    public ulong ContextLimit { get; init; }

    public string? ResetReason { get; init; }
}

/// <summary>
/// 空闲保活看门狗（对应 keepalive.rs 的 KeepAliveWatchdog）：请求计数、模板记忆、服务登记、
/// 后台会话、准备状态机（排队 / 进行中 / 抖动重试）与指定 Key。
/// </summary>
public sealed class KeepAliveWatchdog
{
    public const ulong DefaultContextLimit = 50_000;
    public static readonly TimeSpan PreparationRetryMinDelay = TimeSpan.FromMilliseconds(1_500);
    public static readonly TimeSpan PreparationRetryMaxDelay = TimeSpan.FromMilliseconds(2_500);

    private enum PreparationKind
    {
        Pending,
        Running,
    }

    private readonly record struct PreparationState(PreparationKind Kind, ulong FlightId)
    {
        public static PreparationState Pending => new(PreparationKind.Pending, 0);

        public static PreparationState Running(ulong flight) => new(PreparationKind.Running, flight);

        public bool IsPending => Kind == PreparationKind.Pending;
    }

    private sealed class KeepAliveSession
    {
        public required Conversation Conversation { get; init; }

        public int Turns { get; set; }

        public ulong? ContextTokens { get; set; }

        public string? Model { get; set; }

        public ReasoningEffort ReasoningEffort { get; init; }

        /// <summary>指定 Key 准备创建的会话只服务于那一次准备，不会被默认配置的保活复用。</summary>
        public CliCredential? Credential { get; init; }
    }

    private sealed class KeepAliveFlight
    {
        public ulong Id { get; init; }

        public CancellationToken Cancel { get; init; }

        public Action CancelAction { get; init; } = () => { };

        public PreparationResult? Result { get; set; }
    }

    private readonly object _lock = new();
    private bool _enabled;
    private bool _preparationRequiresContext;
    private bool _enableAfterPreparation;
    private TimeSpan _idle;
    private ulong _contextLimit = DefaultContextLimit;
    private long _lastActivityMs;
    private ulong _activeRequests;
    private KeepAliveFlavor _flavor = KeepAliveFlavor.Unknown;
    private ReasoningEffort _reasoningEffort;
    private KeepAliveTemplate? _template;
    private KeepAliveSession? _session;
    private KeepAliveTotals _totals;
    private KeepAliveSuccess? _lastSuccess;
    private KeepAliveFlight? _flight;
    private ulong _nextFlight;
    private PreparationState? _preparation;
    /// <summary>本通道当前使用的密钥：由最近一次“一键准备”的选择决定，null 表示沿用本机 CLI 默认配置。</summary>
    private CliCredential? _credential;
    private ulong _preparationAttempts;
    private long? _preparationRetryAtMs;
    private string? _preparationLastError;
    private readonly Queue<PreparationResult> _preparationResults = new();
    private int _runningServices;
    private Action? _notifier;
    private Func<int> _questionPicker = () => Random.Shared.Next(JavaQuestions.Count);

    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly object _wakeLock = new();

    public KeepAliveWatchdog(bool enabled, TimeSpan idle)
    {
        _enabled = enabled;
        _idle = idle < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : idle;
        _lastActivityMs = NowMs;
    }

    /// <summary>用指定命令拉起 CLI（测试与验收脚本用），不走 PATH 定位。</summary>
    public static KeepAliveWatchdog WithCliCommand(bool enabled, TimeSpan idle, CliCommand command)
    {
        return new KeepAliveWatchdog(enabled, idle) { Command = command };
    }

    internal CliCommand? Command { get; private init; }

    internal Func<int> QuestionPicker
    {
        set => _questionPicker = value;
    }

    /// <summary>高精度单调毫秒；TickCount64 只有 10–16 ms 粒度，会让抖动重试的到期判定偏早或偏晚。</summary>
    private static long NowMs => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));

    internal static TimeSpan PreparationRetryDelay(Random random)
    {
        var min = (int)PreparationRetryMinDelay.TotalMilliseconds;
        var max = (int)PreparationRetryMaxDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(random.Next(min, max + 1));
    }

    public void SetUiNotifier(Action? notifier)
    {
        lock (_lock)
        {
            _notifier = notifier;
        }
    }

    /// <summary>状态改动后调用；不持有 <c>_lock</c> 时调用。</summary>
    private void NotifyUi()
    {
        Action? notifier;
        lock (_lock)
        {
            notifier = _notifier;
        }

        notifier?.Invoke();
    }

    private void Wake()
    {
        lock (_wakeLock)
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
    }

    // ---------------------------------------------------------------- 状态辅助（须持锁）

    private void ClearSessionLocked()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.Conversation.CancelConversation();
            session.Conversation.Release();
        }
    }

    private bool CancelPreparationLocked(PreparationResult result)
    {
        if (_preparation is not { } preparation)
        {
            return false;
        }

        _preparation = null;
        _preparationRetryAtMs = null;
        _preparationLastError = null;
        if (_flight is { } flight && preparation == PreparationState.Running(flight.Id))
        {
            if (flight.Result is PreparationResult.Ready)
            {
                _preparationResults.Enqueue(new PreparationResult.Ready());
                return false;
            }

            flight.CancelAction();
        }

        _preparationResults.Enqueue(result);
        return true;
    }

    private void CancelFlightLocked()
    {
        _flight?.CancelAction();
    }

    // ---------------------------------------------------------------- 配置

    public void Configure(bool enabled, TimeSpan idle)
    {
        lock (_lock)
        {
            if (idle < TimeSpan.FromSeconds(1))
            {
                idle = TimeSpan.FromSeconds(1);
            }

            if (_enabled == enabled && _idle == idle)
            {
                return;
            }

            _enabled = enabled;
            _idle = idle;
            _lastActivityMs = NowMs;
            if (!enabled)
            {
                ClearSessionLocked();
                CancelPreparationLocked(new PreparationResult.Failed("保活已关闭，本次准备已取消"));
                CancelFlightLocked();
            }
        }

        NotifyUi();
    }

    /// <summary>新强度从下一轮后台问答生效；正在进行的问答继续使用原来的设置。</summary>
    public void SetReasoningEffort(ReasoningEffort effort)
    {
        if (!Enum.IsDefined(effort))
        {
            throw new ArgumentOutOfRangeException(nameof(effort));
        }

        lock (_lock)
        {
            if (_reasoningEffort == effort)
            {
                return;
            }
            _reasoningEffort = effort;
        }

        Wake();
        NotifyUi();
    }

    public void ConfigureFlavor(KeepAliveFlavor flavor)
    {
        if (flavor == KeepAliveFlavor.Unknown)
        {
            return;
        }

        lock (_lock)
        {
            if (_flavor == flavor)
            {
                return;
            }

            _flavor = flavor;
            _template = null;
            ClearSessionLocked();
            CancelFlightLocked();
            _flight = null;
            _preparation = null;
            _credential = null;
            _preparationRetryAtMs = null;
            _preparationLastError = null;
            _preparationResults.Clear();
        }

        NotifyUi();
    }

    public void RequireContextForPreparation()
    {
        lock (_lock)
        {
            _preparationRequiresContext = true;
        }
    }

    internal void EnableAfterPreparation()
    {
        lock (_lock)
        {
            _enableAfterPreparation = true;
        }
    }

    public void SetContextLimit(ulong limit)
    {
        lock (_lock)
        {
            _contextLimit = Math.Max(limit, 1);
            if (_session?.ContextTokens is { } tokens && tokens > _contextLimit)
            {
                ClearSessionLocked();
            }
        }

        NotifyUi();
    }

    public bool Enabled
    {
        get
        {
            lock (_lock)
            {
                return _enabled;
            }
        }
    }

    public TimeSpan Idle
    {
        get
        {
            lock (_lock)
            {
                return _idle;
            }
        }
    }

    public TimeSpan IdleFor()
    {
        lock (_lock)
        {
            return TimeSpan.FromMilliseconds(Math.Max(0, NowMs - _lastActivityMs));
        }
    }

    public void Remember(KeepAliveTemplate template)
    {
        lock (_lock)
        {
            _template = template;
            if (_runningServices == 0)
            {
                _runningServices = 1;
            }
        }
    }

    public KeepAliveTemplate? Template()
    {
        lock (_lock)
        {
            return _template;
        }
    }

    // ---------------------------------------------------------------- 准备

    /// <summary>按模板立刻发起一轮（把空闲计时推到已到期）。</summary>
    public KeepAliveProbe? BeginProbe(KeepAliveTemplate template)
    {
        lock (_lock)
        {
            if (_runningServices == 0)
            {
                _runningServices = 1;
            }

            if (_flavor == KeepAliveFlavor.Unknown)
            {
                _flavor = template.Flavor;
            }

            _lastActivityMs = NowMs - (long)_idle.TotalMilliseconds - 1;
        }

        return BeginDueProbe();
    }

    /// <summary>按通道当前配置发起一次准备，不改变已选的默认配置或指定 Key。通道未运行时抛 <see cref="InvalidOperationException"/>。</summary>
    public bool RequestPreparation() => RequestPreparationInner(false, null);

    public void RestoreCredential(CliCredential credential)
    {
        lock (_lock)
        {
            if (_credential is null && _preparation is null)
            {
                _credential = credential;
            }
        }

        NotifyUi();
    }

    /// <summary>
    /// 发起一次准备，并把 <paramref name="credential"/> 设为之后自动保活使用的配置：
    /// null 沿用本机 CLI 默认配置，非 null 则准备和后续保活都只用这把 Key 经本通道转发。
    /// 与当前配置不同的会话会被丢弃，不复用正在进行的问答。
    /// </summary>
    public bool RequestPreparationWith(CliCredential? credential) => RequestPreparationInner(true, credential);

    private bool RequestPreparationInner(bool switchCredential, CliCredential? credential)
    {
        lock (_lock)
        {
            if (_runningServices == 0)
            {
                throw new InvalidOperationException("请先启用本通道");
            }

            if (_preparation is not null)
            {
                return false;
            }

            if (!switchCredential)
            {
                credential = _credential;
            }

            var sameCredential = Equals(_credential, credential);
            ulong? reusableFlight = _flight is { } flight && !flight.Cancel.IsCancellationRequested && sameCredential ? flight.Id : null;
            if (!sameCredential)
            {
                // 现有会话属于另一套配置：全部丢弃，之后的保活也换用新配置。
                ClearSessionLocked();
                if (_flight is { Result: null })
                {
                    CancelFlightLocked();
                }
            }

            _credential = credential;
            _preparation = reusableFlight is { } id ? PreparationState.Running(id) : PreparationState.Pending;
            _preparationAttempts = reusableFlight is null ? 0UL : 1UL;
            _preparationRetryAtMs = null;
            _preparationLastError = null;
        }

        Wake();
        NotifyUi();
        return true;
    }

    public bool CancelPreparation()
    {
        lock (_lock)
        {
            if (!CancelPreparationLocked(new PreparationResult.Cancelled()))
            {
                return false;
            }

            ClearSessionLocked();
            _lastActivityMs = NowMs;
        }

        Wake();
        NotifyUi();
        return true;
    }

    public PreparationResult? TakePreparationResult()
    {
        lock (_lock)
        {
            return _preparationResults.Count > 0 ? _preparationResults.Dequeue() : null;
        }
    }

    /// <summary>等待准备请求、服务变化或重试时刻（对应 preparation_requested）。</summary>
    public async Task PreparationRequestedAsync(CancellationToken cancellationToken)
    {
        long? retryAt;
        lock (_lock)
        {
            retryAt = _preparation is { IsPending: true } && _activeRequests == 0 && _runningServices > 0 && _flight is null
                ? _preparationRetryAtMs
                : null;
        }

        if (retryAt is not { } deadline)
        {
            await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wake = _wake.WaitAsync(linked.Token);
        // 定时器与单调时钟粒度不同，到点后再核对一次，确保返回时重试时刻确实已到。
        while (!wake.IsCompleted)
        {
            var remaining = deadline - NowMs;
            if (remaining <= 0)
            {
                break;
            }

            var delay = Task.Delay(TimeSpan.FromMilliseconds(remaining), linked.Token);
            await Task.WhenAny(wake, delay).ConfigureAwait(false);
        }

        linked.Cancel();
        try
        {
            await wake.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    // ---------------------------------------------------------------- 服务与请求

    public IDisposable RegisterService(KeepAliveFlavor flavor)
    {
        lock (_lock)
        {
            if (_runningServices == 0)
            {
                _lastActivityMs = NowMs;
            }

            _runningServices++;
            if (_flavor == KeepAliveFlavor.Unknown)
            {
                _flavor = flavor;
            }
        }

        Wake();
        NotifyUi();
        return new ServiceGuard(this);
    }

    private void UnregisterService()
    {
        lock (_lock)
        {
            _runningServices = Math.Max(0, _runningServices - 1);
            if (_runningServices == 0)
            {
                ClearSessionLocked();
                CancelPreparationLocked(new PreparationResult.Failed("本通道已停止"));
                CancelFlightLocked();
            }
            else if (_preparation is { IsPending: true })
            {
                Wake();
            }
        }

        NotifyUi();
    }

    public void RequestStarted(KeepAliveFlavor flavor)
    {
        lock (_lock)
        {
            _activeRequests = _activeRequests == ulong.MaxValue ? _activeRequests : _activeRequests + 1;
            _lastActivityMs = NowMs;
            if (_flight is { Result: null })
            {
                ClearSessionLocked();
                CancelFlightLocked();
            }

            if (_flavor == KeepAliveFlavor.Unknown && flavor != KeepAliveFlavor.Unknown)
            {
                _flavor = flavor;
            }
        }

        NotifyUi();
    }

    public void RequestFinished()
    {
        lock (_lock)
        {
            _activeRequests = _activeRequests == 0 ? 0 : _activeRequests - 1;
            _lastActivityMs = NowMs;
            if (_activeRequests == 0 && _preparation is { IsPending: true })
            {
                Wake();
            }
        }

        NotifyUi();
    }

    // ---------------------------------------------------------------- 探测

    public KeepAliveProbe? BeginDueProbe()
    {
        var probe = BeginDueProbeLocked();
        if (probe is not null)
        {
            NotifyUi();
        }

        return probe;
    }

    private KeepAliveProbe? BeginDueProbeLocked()
    {
        lock (_lock)
        {
            var preparing = _preparation is { IsPending: true };
            var now = NowMs;
            if (_runningServices == 0
                || _activeRequests != 0
                || _flight is not null
                || (preparing && _preparationRetryAtMs is { } retryAt && now < retryAt)
                || (!preparing && (!_enabled || now - _lastActivityMs < (long)_idle.TotalMilliseconds)))
            {
                return null;
            }

            var questionIndex = ((_questionPicker() % JavaQuestions.Count) + JavaQuestions.Count) % JavaQuestions.Count;
            var question = JavaQuestions.All[questionIndex];
            var credential = _credential;
            if (_session is not null && (!Equals(_session.Credential, credential) || _session.ReasoningEffort != _reasoningEffort))
            {
                // 会话必须和本通道当前配置一致，不同配置之间不复用。
                ClearSessionLocked();
            }

            _session ??= new KeepAliveSession { Conversation = new Conversation(), Credential = credential, ReasoningEffort = _reasoningEffort };
            var conversation = _session.Conversation;
            var turn = _session.Turns + 1;
            _nextFlight = unchecked(_nextFlight + 1);
            var flightId = _nextFlight;
            _flight = new KeepAliveFlight
            {
                Id = flightId,
                Cancel = conversation.Cancel,
                CancelAction = conversation.CancelConversation,
                Result = null,
            };
            if (preparing)
            {
                _preparation = PreparationState.Running(flightId);
                _preparationAttempts = _preparationAttempts == ulong.MaxValue ? _preparationAttempts : _preparationAttempts + 1;
                _preparationRetryAtMs = null;
            }

            return new KeepAliveProbe(this, conversation.Retain(), flightId, _flavor, turn, questionIndex, question, credential, _reasoningEffort);
        }
    }

    public KeepAliveSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new KeepAliveSnapshot
            {
                Flavor = _flavor,
                ReasoningEffort = _reasoningEffort,
                Model = _session?.Model ?? _credential?.Model,
                SessionId = _session?.Conversation.Id,
                Turns = _session?.Turns ?? 0,
                ContextTokens = _session?.ContextTokens,
                ContextLimit = _contextLimit,
                Idle = _idle,
                Totals = _totals,
                LastSuccess = _lastSuccess,
                ActiveRequests = _activeRequests,
                Probing = _flight is { Result: null },
                Preparing = _preparation is not null,
                Enabled = _enabled,
                WithKey = _credential is { ApiKey.Length: > 0 },
                PreparationAttempts = _preparationAttempts,
                PreparationRetryAfter = _preparationRetryAtMs is { } retryAt ? TimeSpan.FromMilliseconds(Math.Max(0, retryAt - NowMs)) : null,
                PreparationLastError = _preparationLastError,
            };
        }
    }

    // ---------------------------------------------------------------- 探测回调（由 KeepAliveProbe 调用）

    internal void FinishUnsuccessfully(ulong flightId, string reason, bool interrupted)
    {
        lock (_lock)
        {
            if (_flight is { Result: null } flight && flight.Id == flightId)
            {
                flight.Result = new PreparationResult.Failed(reason);
                if (interrupted)
                {
                    _totals.Interrupted = Saturating(_totals.Interrupted);
                }
                else
                {
                    _totals.Failed = Saturating(_totals.Failed);
                }

                ClearSessionLocked();
            }
        }

        NotifyUi();
    }

    internal KeepAliveCompletion? Complete(KeepAliveProbe probe, string? model, ulong? contextTokens)
    {
        KeepAliveCompletion? completion;
        lock (_lock)
        {
            completion = CompleteLocked(probe, model, contextTokens);
        }

        NotifyUi();
        return completion;
    }

    private KeepAliveCompletion? CompleteLocked(KeepAliveProbe probe, string? model, ulong? contextTokens)
    {
        if (probe.Cancel.IsCancellationRequested || _flight is not { Result: null } flight || flight.Id != probe.FlightId)
        {
            return null;
        }

        if (_session is not { } session || session.Conversation.Id != probe.SessionId)
        {
            return null;
        }

        session.Turns++;
        session.Model = model;
        session.ContextTokens = contextTokens;
        var preparing = _preparation is { } preparation && preparation == PreparationState.Running(probe.FlightId);
        if (preparing && _preparationRequiresContext && contextTokens is null or 0)
        {
            flight.Result = new PreparationResult.Failed("CLI 未返回上下文用量或用量为零");
            _totals.Failed = Saturating(_totals.Failed);
            ClearSessionLocked();
            return null;
        }

        _totals.Completed = Saturating(_totals.Completed);
        _lastSuccess = new KeepAliveSuccess { Model = model, ContextTokens = contextTokens };
        var contextLimit = _contextLimit;
        string? resetReason = contextTokens is null
            ? "CLI 未返回完整用量，已清理后台会话，下轮新建"
            : contextTokens > contextLimit
                ? $"当前会话超过 {contextLimit} token，已清理后台会话，下轮新建"
                : null;
        if (resetReason is not null)
        {
            ClearSessionLocked();
        }

        if (preparing && _enableAfterPreparation)
        {
            _enabled = true;
        }

        flight.Result = new PreparationResult.Ready();
        return new KeepAliveCompletion { ContextTokens = contextTokens, ContextLimit = contextLimit, ResetReason = resetReason };
    }

    internal void ProbeDropped(ulong flightId)
    {
        lock (_lock)
        {
            if (_flight is { } flight && flight.Id == flightId)
            {
                _flight = null;
                if (flight.Result is null)
                {
                    _totals.Interrupted = Saturating(_totals.Interrupted);
                    ClearSessionLocked();
                }

                _lastActivityMs = NowMs;
                if (_preparation == PreparationState.Running(flightId))
                {
                    if (flight.Result is PreparationResult.Ready)
                    {
                        _preparation = null;
                        _preparationRetryAtMs = null;
                        _preparationLastError = null;
                        _preparationResults.Enqueue(new PreparationResult.Ready());
                    }
                    else
                    {
                        _preparation = PreparationState.Pending;
                        _preparationRetryAtMs = NowMs + (long)PreparationRetryDelay(Random.Shared).TotalMilliseconds;
                        _preparationLastError = flight.Result is PreparationResult.Failed failed ? failed.Reason : "本轮已让行或中断，等待继续准备";
                        Wake();
                    }
                }
                else if (_preparation is { IsPending: true })
                {
                    Wake();
                }
            }
        }

        NotifyUi();
    }

    private static ulong Saturating(ulong value) => value == ulong.MaxValue ? value : value + 1;

    // ---------------------------------------------------------------- 测试钩子

    internal void MakeDueForTest()
    {
        lock (_lock)
        {
            _lastActivityMs = NowMs - (long)_idle.TotalMilliseconds - 1;
        }
    }

    internal void SetPreparationRetryNowForTest()
    {
        lock (_lock)
        {
            _preparationRetryAtMs = NowMs;
        }
    }

    internal TimeSpan? PreparationRetryAfterForTest
    {
        get
        {
            lock (_lock)
            {
                return _preparationRetryAtMs is { } retryAt ? TimeSpan.FromMilliseconds(retryAt - NowMs) : null;
            }
        }
    }

    private sealed class ServiceGuard : IDisposable
    {
        private KeepAliveWatchdog? _watchdog;

        public ServiceGuard(KeepAliveWatchdog watchdog)
        {
            _watchdog = watchdog;
        }

        public void Dispose()
        {
            var watchdog = Interlocked.Exchange(ref _watchdog, null);
            watchdog?.UnregisterService();
        }
    }
}

/// <summary>
/// 一轮进行中的保活探测（对应 KeepAliveProbe）。<see cref="Dispose"/> 对应 Rust 的 Drop：
/// 未完成即记一次中断，并推进准备状态机。
/// </summary>
public sealed class KeepAliveProbe : IDisposable
{
    private readonly KeepAliveWatchdog _watchdog;
    private Conversation? _conversation;

    internal KeepAliveProbe(KeepAliveWatchdog watchdog, Conversation conversation, ulong flightId, KeepAliveFlavor flavor, int turn, int questionIndex, string question, CliCredential? credential, ReasoningEffort reasoningEffort)
    {
        _watchdog = watchdog;
        _conversation = conversation;
        FlightId = flightId;
        Flavor = flavor;
        SessionId = conversation.Id;
        Turn = turn;
        QuestionIndex = questionIndex;
        Question = question;
        Cancel = conversation.Cancel;
        Credential = credential;
        ReasoningEffort = reasoningEffort;
    }

    internal ulong FlightId { get; }

    public KeepAliveFlavor Flavor { get; }

    public string SessionId { get; }

    public int Turn { get; }

    public int QuestionIndex { get; }

    public string Question { get; }

    public CancellationToken Cancel { get; }

    internal CliCredential? Credential { get; }

    public ReasoningEffort ReasoningEffort { get; }

    /// <summary>本轮是否使用用户临时输入的 Key。</summary>
    public bool UsesSuppliedKey => Credential is { ApiKey.Length: > 0 };

    /// <summary>拉起（或复用）CLI 会话并问一道题。失败抛 <see cref="CliException"/>；取消抛 <see cref="OperationCanceledException"/>。</summary>
    internal async Task<CliReply> ExecuteAsync(CancellationToken cancellationToken)
    {
        var conversation = _conversation ?? throw new ObjectDisposedException(nameof(KeepAliveProbe));
        var started = Stopwatch.StartNew();
        await conversation.ProcessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            conversation.Process ??= await CliSession.StartAsync(Flavor, SessionId, _watchdog.Command, Credential, ReasoningEffort, cancellationToken).ConfigureAwait(false);
            var setupSeconds = started.Elapsed.TotalSeconds;
            var reply = await conversation.Process.AskAsync(Question, SessionId, cancellationToken).ConfigureAwait(false);
            if (reply.FirstContentSeconds is { } seconds)
            {
                reply.FirstContentSeconds = seconds + setupSeconds;
            }

            return reply;
        }
        finally
        {
            conversation.ProcessLock.Release();
        }
    }

    public void Fail(string reason) => _watchdog.FinishUnsuccessfully(FlightId, reason, interrupted: false);

    public void Interrupt(string reason) => _watchdog.FinishUnsuccessfully(FlightId, reason, interrupted: true);

    public KeepAliveCompletion? Complete(string? model, ulong? contextTokens) => _watchdog.Complete(this, model, contextTokens);

    public void Dispose()
    {
        var conversation = Interlocked.Exchange(ref _conversation, null);
        if (conversation is null)
        {
            return;
        }

        _watchdog.ProbeDropped(FlightId);
        conversation.Release();
    }
}
