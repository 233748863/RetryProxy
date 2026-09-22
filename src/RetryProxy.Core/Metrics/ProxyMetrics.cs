using System;
using RetryProxy.Core.Cache;

namespace RetryProxy.Core.Metrics;

/// <summary>
/// 请求统计（对应 metrics.rs 的 ProxyMetrics）。所有以 <c>保活-</c> 开头的请求 ID 不计入当日统计。
/// </summary>
public sealed class ProxyMetrics
{
    public const string KeepAlivePrefix = "保活-";

    private readonly object _lock = new();
    private readonly MetricsState _state;
    private Action? _notifier;

    public ProxyMetrics()
        : this(null)
    {
    }

    /// <summary>恢复本地日历日的统计；稳定的通道 ID 让改名后的通道仍各自独立。</summary>
    public ProxyMetrics(IDailyStorage? storage)
    {
        _state = new MetricsState(DateTime.Now, storage);
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

    public void RequestStarted(string requestId, string method, string path)
    {
        if (requestId.StartsWith(KeepAlivePrefix, StringComparison.Ordinal))
        {
            return;
        }

        lock (_lock)
        {
            var now = DateTime.Now;
            _state.Rollover(now);
            if (!_state.Active.ContainsKey(requestId))
            {
                _state.Active[requestId] = new ActiveRequest
                {
                    RequestId = requestId,
                    Method = method,
                    Path = path,
                    Phase = Metrics.RequestPhase.WaitingResponse,
                    Attempt = 1,
                };
            }

            _state.Change(requestId, new DailyChange.Started(), UnixMs(now));
        }

        NotifyUi();
    }

    public void RequestAttempt(string requestId, ulong attempt)
    {
        lock (_lock)
        {
            _state.Rollover(DateTime.Now);
            if (_state.Active.TryGetValue(requestId, out var request))
            {
                request.Phase = Metrics.RequestPhase.WaitingResponse;
                request.Attempt = attempt;
            }
        }

        NotifyUi();
    }

    public void RequestPhase(string requestId, RequestPhase phase)
    {
        lock (_lock)
        {
            _state.Rollover(DateTime.Now);
            if (_state.Active.TryGetValue(requestId, out var request))
            {
                request.Phase = phase;
            }
        }

        NotifyUi();
    }

    public void RequestFinished(string requestId)
    {
        lock (_lock)
        {
            _state.Rollover(DateTime.Now);
            _state.Active.Remove(requestId);
        }

        NotifyUi();
    }

    public void Success(string requestId, CacheRequest? cache) => Change(requestId, new DailyChange.Succeeded(cache));

    public void Retry(string requestId, ulong attempt) => Change(requestId, new DailyChange.Retry(attempt));

    public void Failure(string requestId) => Change(requestId, new DailyChange.Failed());

    public void CacheKey(string requestId, CacheKeyState state)
    {
        if (state != CacheKeyState.Unchanged)
        {
            Change(requestId, new DailyChange.CacheKey(state));
        }
    }

    public void CacheFallback(string requestId) => Change(requestId, new DailyChange.CacheFallback());

    /// <summary>测试用：直接记入一条成功请求的缓存用量。</summary>
    internal void RecordCacheRequest(CacheRequest request) => Success(request.RequestId, request);

    private void Change(string requestId, DailyChange change)
    {
        if (requestId.StartsWith(KeepAlivePrefix, StringComparison.Ordinal))
        {
            return;
        }

        lock (_lock)
        {
            var now = DateTime.Now;
            _state.Rollover(now);
            _state.Change(requestId, change, UnixMs(now));
        }

        NotifyUi();
    }

    public MetricsSnapshot Snapshot()
    {
        lock (_lock)
        {
            _state.Rollover(DateTime.Now);
            return _state.Snapshot();
        }
    }

    private static long UnixMs(DateTime now) => new DateTimeOffset(now).ToUnixTimeMilliseconds();
}
