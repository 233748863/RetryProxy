using System;
using System.Threading;

namespace RetryProxy.Core.KeepAlive;

public struct KeepAliveTotals
{
    public ulong Completed;
    public ulong Failed;
    public ulong Interrupted;
}

public sealed class KeepAliveSuccess
{
    public string? Model { get; init; }

    public ulong? ContextTokens { get; init; }
}

public sealed class KeepAliveSnapshot
{
    public KeepAliveFlavor Flavor { get; init; } = KeepAliveFlavor.Unknown;

    public string? Model { get; init; }

    public string? SessionId { get; init; }

    public int Turns { get; init; }

    public ulong? ContextTokens { get; init; }

    public ulong ContextLimit { get; init; }

    public KeepAliveTotals Totals { get; init; }

    public KeepAliveSuccess? LastSuccess { get; init; }

    public ulong ActiveRequests { get; init; }

    public bool Probing { get; init; }

    public bool Preparing { get; init; }

    public bool WithKey { get; init; }

    public ulong PreparationAttempts { get; init; }

    public TimeSpan? PreparationRetryAfter { get; init; }

    public string? PreparationLastError { get; init; }
}

/// <summary>
/// 空闲保活看门狗（对应 keepalive.rs 的 KeepAliveWatchdog）。
/// M2 只实现请求计数、模板记忆、服务登记与配置；探测执行与 CLI 会话在 M4 补齐。
/// </summary>
public sealed class KeepAliveWatchdog
{
    public const ulong DefaultContextLimit = 50_000;
    public static readonly TimeSpan PreparationRetryMinDelay = TimeSpan.FromMilliseconds(1_500);
    public static readonly TimeSpan PreparationRetryMaxDelay = TimeSpan.FromMilliseconds(2_500);

    private readonly object _lock = new();
    private bool _enabled;
    private TimeSpan _idle;
    private ulong _contextLimit = DefaultContextLimit;
    private long _lastActivity;
    private ulong _activeRequests;
    private KeepAliveFlavor _flavor = KeepAliveFlavor.Unknown;
    private KeepAliveTemplate? _template;
    private readonly KeepAliveTotals _totals = default;
    private readonly KeepAliveSuccess? _lastSuccess = null;
    private bool _preparing;
    private int _runningServices;
    private Action? _notifier;

    /// <summary>准备请求或服务变化时唤醒后台轮询。</summary>
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    public KeepAliveWatchdog(bool enabled, TimeSpan idle)
    {
        _enabled = enabled;
        _idle = idle < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : idle;
        _lastActivity = Environment.TickCount64;
    }

    public void SetUiNotifier(Action? notifier)
    {
        lock (_lock)
        {
            _notifier = notifier;
        }
    }

    private void NotifyUi()
    {
        Action? notifier;
        lock (_lock)
        {
            notifier = _notifier;
        }

        notifier?.Invoke();
    }

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
            _lastActivity = Environment.TickCount64;
            if (!enabled)
            {
                _preparing = false;
            }
        }

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
            _preparing = false;
        }

        NotifyUi();
    }

    public void SetContextLimit(ulong limit)
    {
        lock (_lock)
        {
            _contextLimit = Math.Max(limit, 1);
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
            return TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _lastActivity));
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

    public KeepAliveTemplate? TakeDue() => null;

    /// <summary>按通道当前配置发起一次准备；M4 接入 CLI 前只登记状态。</summary>
    public bool RequestPreparation()
    {
        lock (_lock)
        {
            if (_runningServices == 0)
            {
                throw new InvalidOperationException("请先启用本通道");
            }

            if (_preparing)
            {
                return false;
            }

            _preparing = true;
        }

        _wake.Release();
        NotifyUi();
        return true;
    }

    public bool CancelPreparation()
    {
        lock (_lock)
        {
            if (!_preparing)
            {
                return false;
            }

            _preparing = false;
            _lastActivity = Environment.TickCount64;
        }

        _wake.Release();
        NotifyUi();
        return true;
    }

    /// <summary>等待准备请求或唤醒信号（对应 preparation_requested）。</summary>
    public System.Threading.Tasks.Task PreparationRequestedAsync(CancellationToken cancellationToken)
    {
        return _wake.WaitAsync(cancellationToken);
    }

    public IDisposable RegisterService(KeepAliveFlavor flavor)
    {
        lock (_lock)
        {
            if (_runningServices == 0)
            {
                _lastActivity = Environment.TickCount64;
            }

            _runningServices++;
            if (_flavor == KeepAliveFlavor.Unknown)
            {
                _flavor = flavor;
            }
        }

        _wake.Release();
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
                _preparing = false;
            }
        }

        _wake.Release();
        NotifyUi();
    }

    public void RequestStarted(KeepAliveFlavor flavor)
    {
        lock (_lock)
        {
            _activeRequests = _activeRequests == ulong.MaxValue ? _activeRequests : _activeRequests + 1;
            _lastActivity = Environment.TickCount64;
            if (_flavor == KeepAliveFlavor.Unknown && flavor != KeepAliveFlavor.Unknown)
            {
                _flavor = flavor;
            }
        }

        NotifyUi();
    }

    public void RequestFinished()
    {
        var wake = false;
        lock (_lock)
        {
            _activeRequests = _activeRequests == 0 ? 0 : _activeRequests - 1;
            _lastActivity = Environment.TickCount64;
            wake = _activeRequests == 0 && _preparing;
        }

        if (wake)
        {
            _wake.Release();
        }

        NotifyUi();
    }

    /// <summary>是否到了发保活探测的时候（M4 实现真正的探测；M2 只做判定）。</summary>
    public bool IsProbeDue()
    {
        lock (_lock)
        {
            if (_runningServices == 0 || _activeRequests != 0)
            {
                return false;
            }

            if (_preparing)
            {
                return true;
            }

            return _enabled && TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _lastActivity)) >= _idle;
        }
    }

    public KeepAliveSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new KeepAliveSnapshot
            {
                Flavor = _flavor,
                ContextLimit = _contextLimit,
                Totals = _totals,
                LastSuccess = _lastSuccess,
                ActiveRequests = _activeRequests,
                Preparing = _preparing,
            };
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
