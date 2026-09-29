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
    private readonly string _routeFields;

    public StreamLifecycle(
        RouteLogger logger,
        ProxyMetrics metrics,
        string requestId,
        ulong attemptNumber,
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
        CancellationToken cancel,
        string routeFields = "")
    {
        _logger = logger;
        _metrics = metrics;
        _requestId = requestId;
        _attemptNumber = attemptNumber;
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
        _routeFields = routeFields;
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
                _method,
                _safePath,
                _status,
                Stats.FirstContentSeconds(),
                elapsed,
                _routeFields + Stats.LogFields());
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

        // 能从上游错误码认出原因且状态码非 2xx 时，原因已写在状态后的括号里，不再重复。
        var summary = Stats.FailureSummary();
        var reasonText = summary is not null && _status is < 200 or >= 300 ? string.Empty : $"，原因：{summary ?? reason}";
        _logger.Warn(
            $"[{_requestId}] {_method} {_safePath} -> {LogText.UpstreamStatus(_status, summary)}{LogText.RetriedNote(_attemptNumber, false)}{_routeFields}，响应未完成{reasonText}，不再重试（已进入响应转发阶段）{Stats.FailureLogFields()}，{LogText.TimingText(Stats.FirstContentSeconds(), elapsed)}");
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

/// <summary>
/// 一次请求内的重试日志节流：同一原因连续重试只写第一次，之后每 <see cref="ProgressEvery"/> 次写一条进度；原因变了重新写一次完整行。
/// 例：连续 47 次 HTTP 500 → 第 1 次写完整行，第 20、40 次写「已重试 20 次，仍是 …」，其余不写。
/// </summary>
internal sealed class RetryLog
{
    public const int ProgressEvery = 20;

    private string? _reason;
    private ulong _sameReason;

    public ulong Retries { get; private set; }

    /// <summary>登记一次将要重试的失败，返回这次该写哪种日志。</summary>
    public RetryLogKind Next(string reason)
    {
        Retries++;
        if (!string.Equals(reason, _reason, StringComparison.Ordinal))
        {
            _reason = reason;
            _sameReason = 1;
            return RetryLogKind.Full;
        }

        _sameReason++;
        return _sameReason % ProgressEvery == 0 ? RetryLogKind.Progress : RetryLogKind.None;
    }

    public string ProgressText(string requestId, string reason) => $"[{requestId}] 已重试 {Retries} 次，仍是{reason}";

    /// <summary>切换 Key 改投后重新开始节流：换了 Key，下一次失败即使原因相同也写完整行。</summary>
    public void Reset()
    {
        _reason = null;
        _sameReason = 0;
    }
}

internal enum RetryLogKind
{
    None,
    Full,
    Progress,
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

    /// <summary>
    /// 跟在上游状态后面的重试说明；第一次就结束时为空。
    /// 例：第 48 次拿到 200 → <c>（重试 47 次后成功）</c>；第 4 次拿到 400 → <c>，已重试 3 次</c>。
    /// </summary>
    public static string RetriedNote(ulong attemptNumber, bool succeeded)
    {
        if (attemptNumber <= 1)
        {
            return string.Empty;
        }

        var retries = attemptNumber - 1;
        return succeeded ? $"（重试 {retries} 次后成功）" : $"，已重试 {retries} 次";
    }

    /// <summary>重试失败行里的“第 N 次”，不写总次数（通常是 100001 这种没有意义的上限）。</summary>
    public static string AttemptText(ulong attemptNumber) => $"第 {attemptNumber} 次";

    public static string RetryDelayText(double delay) => $"{delay:F1} 秒后重试";

    public static string FormatCompletedAttempt(
        string requestId,
        ulong attemptNumber,
        string method,
        string safePath,
        int status,
        double? firstByteSeconds,
        double elapsed,
        string details)
    {
        return $"[{requestId}] {method} {safePath} -> {UpstreamStatus(status)}{RetriedNote(attemptNumber, status is >= 200 and < 300)}{details}，{TimingText(firstByteSeconds, elapsed)}";
    }

    /// <summary>
    /// 日志里的上游状态：非 2xx 时在括号里写明含义，优先用上游错误码翻译出的具体原因，其次是状态码的通用含义；都不认识时只写状态码。
    /// 例：<c>500</c> + 错误码 get_channel_failed → <c>上游 HTTP 500（当前需求量高，模型负载已达上限）</c>；<c>502</c> → <c>上游 HTTP 502（上游网关错误）</c>；<c>200</c> / <c>599</c> → <c>上游 HTTP 200</c> / <c>上游 HTTP 599</c>。
    /// </summary>
    public static string UpstreamStatus(int status, string? summary = null) => "上游 " + HttpStatus(status, summary);

    /// <summary>不带“上游”前缀的状态写法，例：重试耗尽，返回客户端最后一次完整上游响应 HTTP 500（上游服务内部错误）。</summary>
    public static string HttpStatus(int status, string? summary = null)
    {
        var meaning = status is >= 200 and < 300 ? null : summary ?? StatusMeaning(status);
        return meaning is null ? $"HTTP {status}" : $"HTTP {status}（{meaning}）";
    }

    /// <summary>常见 HTTP 状态码的大白话含义（含 Cloudflare 的 52x）；不认识的返回 null。</summary>
    public static string? StatusMeaning(int status) => status switch
    {
        400 => "请求参数有误",
        401 => "身份验证失败",
        402 => "余额不足或需要付费",
        403 => "没有访问权限",
        404 => "地址或模型不存在",
        405 => "请求方法不被允许",
        408 => "上游等待请求超时",
        409 => "请求冲突",
        413 => "请求内容过大",
        415 => "不支持的内容类型",
        422 => "请求内容无法处理",
        429 => "请求过于频繁",
        499 => "请求被提前关闭",
        500 => "上游服务内部错误",
        501 => "上游不支持该功能",
        502 => "上游网关错误",
        503 => "上游服务暂不可用",
        504 => "上游网关超时",
        520 => "上游返回了未知错误",
        521 => "上游服务器已关闭",
        522 => "连接上游超时",
        523 => "上游不可达",
        524 => "上游响应超时",
        525 => "上游 TLS 握手失败",
        526 => "上游证书无效",
        529 => "上游服务过载",
        _ => null,
    };
}
