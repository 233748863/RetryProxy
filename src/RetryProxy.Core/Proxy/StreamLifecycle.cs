using System;
using System.Globalization;
using System.Threading;
using RetryProxy.Core.Internal;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Stats;

namespace RetryProxy.Core.Proxy;

/// <summary>
/// 响应转发阶段的收尾逻辑（对应 proxy.rs 的 StreamLifecycle）：
/// 判定完成 / 中断，记一次成功或失败，并写完成日志。
/// </summary>
internal sealed class StreamLifecycle : IDisposable
{
    private readonly RouteLogger _logger;
    private readonly ProxyMetrics _metrics;
    private readonly string _requestId;
    private readonly ulong _attemptNumber;
    private readonly ulong _totalAttempts;
    private readonly string _method;
    private readonly string _safePath;
    private readonly int _status;
    private readonly MonotonicInstant _startedAt;
    private readonly long? _expectedBodyBytes;
    private ulong _receivedBodyBytes;
    private readonly KeepAliveWatchdog _keepAlive;
    private KeepAliveTemplate? _keepAliveTemplate;
    private readonly bool _logCompletion;
    private readonly bool _warnOnHttpFailure;
    private readonly Deadline _deadline;
    private readonly double _totalTimeoutSeconds;
    private readonly CancellationToken _cancel;

    public StreamLifecycle(
        RouteLogger logger,
        ProxyMetrics metrics,
        string requestId,
        ulong attemptNumber,
        ulong totalAttempts,
        string method,
        string safePath,
        int status,
        MonotonicInstant startedAt,
        ResponseStats stats,
        long? expectedBodyBytes,
        ulong receivedBodyBytes,
        KeepAliveWatchdog keepAlive,
        KeepAliveTemplate? keepAliveTemplate,
        bool logCompletion,
        bool warnOnHttpFailure,
        Deadline deadline,
        double totalTimeoutSeconds,
        CancellationToken cancel)
    {
        _logger = logger;
        _metrics = metrics;
        _requestId = requestId;
        _attemptNumber = attemptNumber;
        _totalAttempts = totalAttempts;
        _method = method;
        _safePath = safePath;
        _status = status;
        _startedAt = startedAt;
        Stats = stats;
        _expectedBodyBytes = expectedBodyBytes;
        _receivedBodyBytes = receivedBodyBytes;
        _keepAlive = keepAlive;
        _keepAliveTemplate = keepAliveTemplate;
        _logCompletion = logCompletion;
        _warnOnHttpFailure = warnOnHttpFailure;
        _deadline = deadline;
        _totalTimeoutSeconds = totalTimeoutSeconds;
        _cancel = cancel;
    }

    public ResponseStats Stats { get; }

    public bool Completed { get; private set; }

    public string TimeoutReason() => $"请求总等待达到 {Format(_totalTimeoutSeconds)} 秒，已停止接收上游响应";

    public void Observe(ReadOnlySpan<byte> chunk)
    {
        _receivedBodyBytes = Saturating.Add(_receivedBodyBytes, (ulong)chunk.Length);
        Stats.Observe(chunk, _startedAt.ElapsedSeconds);
        CheckCompletion();
    }

    public void CheckCompletion()
    {
        if (_expectedBodyBytes is { } expected && _receivedBodyBytes >= (ulong)Math.Max(expected, 0))
        {
            Finish();
        }
        else if (_status < 400 && Stats.Outcome is { } outcome)
        {
            if (outcome.IsFailed)
            {
                Interrupted(outcome.Reason!);
            }
            else
            {
                Complete();
            }
        }
    }

    public void Finish()
    {
        Stats.Finish(_startedAt.ElapsedSeconds);
        if (Stats.Outcome is { IsFailed: true } failed)
        {
            Interrupted(failed.Reason!);
        }
        else
        {
            Complete();
        }
    }

    private void Complete()
    {
        if (Completed)
        {
            return;
        }

        Completed = true;
        var elapsed = _startedAt.ElapsedSeconds;
        if (_logCompletion)
        {
            var message = LogText.FormatCompletedAttempt(
                _requestId,
                _attemptNumber,
                _totalAttempts,
                _method,
                _safePath,
                _status,
                Stats.FirstContentSeconds(),
                elapsed,
                Stats.LogFields());
            if (_warnOnHttpFailure)
            {
                _logger.Warn(message);
            }
            else
            {
                _logger.Info(message);
            }
        }

        if (_status == 200)
        {
            _metrics.Success(_requestId, Stats.CacheRequest(_requestId));
            if (_keepAliveTemplate is { } template)
            {
                _keepAliveTemplate = null;
                _keepAlive.Remember(template);
            }
        }
        else
        {
            _metrics.Failure(_requestId);
        }
    }

    public void Interrupted(string reason)
    {
        if (Completed)
        {
            return;
        }

        Completed = true;
        var elapsed = _startedAt.ElapsedSeconds;
        _metrics.Failure(_requestId);
        if (!_logCompletion)
        {
            return;
        }

        _logger.Warn(
            $"[{_requestId}] 第 {_attemptNumber}/{_totalAttempts} 次 {_method} {_safePath} -> 上游 HTTP {_status}，响应未完成，原因：{Stats.FailureSummary() ?? reason}，不再重试（已进入响应转发阶段）{Stats.FailureLogFields()}，{LogText.TimingText(Stats.FirstContentSeconds(), elapsed)}");
    }

    /// <summary>对应 Drop：正文流没走完就被丢弃。</summary>
    public void Dispose()
    {
        if (Completed)
        {
            return;
        }

        var reason = _cancel.IsCancellationRequested
            ? "代理通道已停止，请求已取消"
            : _deadline.HasPassed ? TimeoutReason() : "客户端断开或响应未读完";
        Interrupted(reason);
    }

    /// <summary>对应 Rust 的 <c>{}</c> 格式化 f64：整数不带小数点，其余最短精确表示。</summary>
    internal static string Format(double value)
    {
        if (double.IsFinite(value) && Math.Abs(value) < 1e15 && value == Math.Floor(value))
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}

/// <summary>日志文案的小工具（对应 timing_text / attempt_prefix / format_completed_attempt）。</summary>
internal static class LogText
{
    public static string TimingText(double? firstByteSeconds, double elapsed)
    {
        return firstByteSeconds is { } value
            ? $"首字 {value:F2} 秒 / 耗时 {elapsed:F2} 秒"
            : $"首字：无 / 耗时 {elapsed:F2} 秒";
    }

    public static string AttemptPrefix(ulong attemptNumber, ulong totalAttempts, int status)
    {
        return attemptNumber == 1 && status == 200 ? string.Empty : $"第 {attemptNumber}/{totalAttempts} 次 ";
    }

    public static string FormatCompletedAttempt(
        string requestId,
        ulong attemptNumber,
        ulong totalAttempts,
        string method,
        string safePath,
        int status,
        double? firstByteSeconds,
        double elapsed,
        string details)
    {
        return $"[{requestId}] {AttemptPrefix(attemptNumber, totalAttempts, status)}{method} {safePath} -> 上游 HTTP {status}{details}，{TimingText(firstByteSeconds, elapsed)}";
    }
}
