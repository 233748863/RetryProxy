using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using RetryProxy.Core.Config;
using RetryProxy.Core.Diagnostics;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Stats;

namespace RetryProxy.Core.Proxy;

/// <summary>旁路记录原管线的决定；既不参与返回值判断，也不把诊断异常传回代理。</summary>
internal sealed class RequestDiagnosticTrace
{
    private readonly IDiagnosticRequest _request;
    private readonly MonotonicInstant _startedAt;
    private readonly object _sync = new();
    private DiagnosticTarget? _target;
    private DiagnosticResponseData? _response;
    private ulong _attempt;
    private ulong _sends;
    private ulong _retries;
    private int? _status;
    private double _sendStarted;
    private double _waitStarted;
    private bool _sendOpen;
    private bool _waitOpen;
    private bool _finished;
    private bool _hasOutcome;
    private DiagnosticDelivery? _delivery;
    private string _nextReason = "首次发送";
    private readonly List<(int Length, byte[] Hash)> _credentialHashes = new(2);
    private bool _privacyUnavailable;

    private RequestDiagnosticTrace(IDiagnosticRequest request, MonotonicInstant startedAt)
    {
        _request = request;
        _startedAt = startedAt;
    }

    public static RequestDiagnosticTrace? Begin(IRequestDiagnostics? diagnostics, DiagnosticRequestInfo info, MonotonicInstant startedAt)
    {
        if (diagnostics is null) return null;
        try
        {
            var request = diagnostics.Begin(DiagnosticSafety.Sanitize(info));
            return request is null ? null : new RequestDiagnosticTrace(request, startedAt);
        }
        catch (Exception) { return null; }
    }

    public void SetAttempt(ulong attempt) { lock (_sync) _attempt = attempt; }

    public void SelectTarget(ChannelSnapshot snapshot, string? model)
    {
        lock (_sync)
        {
            RememberCredentials(snapshot);
            _target = DiagnosticSafety.Target(snapshot, model);
            _response = null;
            _status = null;
        }
    }

    public void SendStarted(ChannelSnapshot snapshot, string? model)
    {
        lock (_sync)
        {
            if (_finished) return;
            RememberCredentials(snapshot);
            _target = DiagnosticSafety.Target(snapshot, model);
            _response = null;
            _status = null;
            _sends = Saturating.Add(_sends, 1);
            _sendStarted = _startedAt.ElapsedSeconds;
            _sendOpen = true;
            Write(New(DiagnosticEventKind.SendStarted) with { Reason = _nextReason });
        }
    }

    public void Headers(int status)
    {
        lock (_sync)
        {
            _status = status;
            Write(New(DiagnosticEventKind.ResponseHeaders));
        }
    }

    public void Observe(ResponseStats stats)
    {
        lock (_sync) _response = stats.DiagnosticSnapshot();
    }

    public void WaitingGeneration()
    {
        lock (_sync) Write(New(DiagnosticEventKind.WaitingGeneration));
    }

    public void ResponseReady(ResponseStats stats)
    {
        lock (_sync)
        {
            _response = stats.DiagnosticSnapshot();
            Write(New(DiagnosticEventKind.ResponseReady) with { Reason = "响应已就绪" });
        }
    }

    public void EndSend(string? reason = null, ResponseStats? stats = null)
    {
        lock (_sync)
        {
            if (stats is not null) _response = stats.DiagnosticSnapshot();
            if (!_sendOpen) return;
            _sendOpen = false;
            Write(New(DiagnosticEventKind.SendFinished) with
            {
                Reason = reason ?? _response?.Summary,
                DurationSeconds = Math.Max(0, _startedAt.ElapsedSeconds - _sendStarted),
            });
        }
    }

    public void Compatibility(string reason)
    {
        lock (_sync)
        {
            EndSend(reason);
            _nextReason = reason;
            Write(New(DiagnosticEventKind.CompatibilityResend) with { Reason = reason });
        }
    }

    public void Retry(ulong retries, string reason)
    {
        lock (_sync)
        {
            _retries = retries;
            _nextReason = reason;
        }
    }

    public void BeginWait(double delay)
    {
        lock (_sync)
        {
            _waitStarted = _startedAt.ElapsedSeconds;
            _waitOpen = true;
            Write(New(DiagnosticEventKind.RetryWaiting) with { PlannedWaitSeconds = delay, Reason = _nextReason });
        }
    }

    public void EndWait(bool completed)
    {
        lock (_sync)
        {
            if (!_waitOpen) return;
            _waitOpen = false;
            Write(New(DiagnosticEventKind.RetryWaitFinished) with
            {
                DurationSeconds = Math.Max(0, _startedAt.ElapsedSeconds - _waitStarted),
                Reason = completed ? "等待结束" : "等待被中断",
            });
        }
    }

    public void Switched()
    {
        lock (_sync)
        {
            EndSend("手动切换后重发");
            _nextReason = "手动切换后重发";
            // 此处只记录切换事实，新目标以之后实际 SendStarted 的快照为准。
            Write(New(DiagnosticEventKind.Switched) with { Reason = _nextReason });
        }
    }

    public void Outcome(bool success, string? reason = null, ResponseStats? stats = null, int? status = null)
    {
        lock (_sync)
        {
            if (_hasOutcome) return;
            _hasOutcome = true;
            if (stats is not null) _response = stats.DiagnosticSnapshot();
            EndSend(reason);
            if (status is not null) _status = status;
            Write(New(DiagnosticEventKind.Outcome) with
            {
                Outcome = success ? DiagnosticOutcome.Success : DiagnosticOutcome.Failure,
                Reason = success ? "请求成功" : reason ?? _response?.Summary ?? "原请求判定为失败",
            });
        }
    }

    public void FinalResponse(int status)
    {
        lock (_sync)
        {
            _status = status;
            Write(New(DiagnosticEventKind.ResponseReady) with { Reason = "响应已就绪" });
        }
    }

    public void Delivered(bool complete)
    {
        lock (_sync)
        {
            _delivery = complete ? DiagnosticDelivery.Complete : DiagnosticDelivery.Interrupted;
            Write(New(DiagnosticEventKind.Delivery) with
            {
                Delivery = _delivery,
                Reason = complete ? "响应转发完成" : "响应转发中断",
            });
        }
    }

    public void Finish()
    {
        lock (_sync)
        {
            if (_finished) return;
            EndWait(false);
            EndSend();
            Write(New(DiagnosticEventKind.Finished) with { Delivery = _delivery });
            _finished = true;
            _credentialHashes.Clear();
        }
    }

    private DiagnosticEntry New(DiagnosticEventKind kind) => new(kind, _startedAt.ElapsedSeconds)
    {
        Target = _target,
        AttemptNumber = _attempt,
        SendNumber = _sends,
        RetryCount = _retries,
        StatusCode = _status,
        ErrorCode = _response?.ErrorCode,
        UpstreamRequestId = _response?.UpstreamRequestId,
        LastEvent = _response?.LastEvent,
        FirstContentSeconds = _response?.FirstContentSeconds,
    };

    private void RememberCredentials(ChannelSnapshot snapshot)
    {
        try
        {
            _credentialHashes.Clear();
            foreach (var value in new[] { snapshot.ApiKey, snapshot.LocalToken })
            {
                if (string.IsNullOrEmpty(value)) continue;
                var bytes = Encoding.UTF8.GetBytes(value);
                _credentialHashes.Add((bytes.Length, SHA256.HashData(bytes)));
            }
        }
        catch (Exception) { _privacyUnavailable = true; }
    }

    private string? WithoutCredentials(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> hash = stackalloc byte[32];
        foreach (var secret in _credentialHashes)
        {
            for (var offset = 0; offset <= bytes.Length - secret.Length; offset++)
            {
                SHA256.HashData(bytes.AsSpan(offset, secret.Length), hash);
                if (CryptographicOperations.FixedTimeEquals(hash, secret.Hash)) return null;
            }
        }
        return value;
    }

    private void Write(DiagnosticEntry entry)
    {
        if (_finished || _privacyUnavailable) return;
        try
        {
            entry = DiagnosticSafety.Sanitize(entry);
            entry = entry with
            {
                Reason = WithoutCredentials(entry.Reason),
                ErrorCode = WithoutCredentials(entry.ErrorCode),
                UpstreamRequestId = WithoutCredentials(entry.UpstreamRequestId),
                LastEvent = WithoutCredentials(entry.LastEvent),
                Target = entry.Target is { } target ? target with
                {
                    ProviderId = WithoutCredentials(target.ProviderId) ?? string.Empty,
                    ProviderName = WithoutCredentials(target.ProviderName) ?? "[已隐藏]",
                    KeyId = WithoutCredentials(target.KeyId) ?? string.Empty,
                    KeyName = WithoutCredentials(target.KeyName) ?? "[已隐藏]",
                    Model = WithoutCredentials(target.Model),
                } : null,
            };
            _request.Record(entry);
        }
        catch (Exception) { /* 诊断失败不影响转发，也不生成含原始异常的日志。 */ }
    }
}

internal sealed class RequestDiagnosticLifetime : IDisposable
{
    public RequestDiagnosticTrace? Trace { get; set; }
    public void Dispose() => Trace?.Finish();
}

internal sealed record DiagnosticResponseData(
    string? Summary,
    string? ErrorCode,
    string? UpstreamRequestId,
    string? LastEvent,
    double? FirstContentSeconds);
