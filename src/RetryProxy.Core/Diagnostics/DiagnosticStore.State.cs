using System;
using System.Collections.Generic;

namespace RetryProxy.Core.Diagnostics;

public sealed partial class DiagnosticStore
{
    // 每个活动请求只有摘要；日期查询缓存只保留归并状态与重放游标，不持有事件对象。
    private sealed class SummaryState(DiagnosticRequestInfo request, string session)
    {
        internal readonly DiagnosticRequestInfo Request = request;
        internal readonly string Session = session;
        internal readonly HashSet<DiagnosticTarget> Targets = [];
        internal long LastSequence;
        internal bool Incomplete;
        internal bool Finished;
        private DiagnosticTarget? _lastTarget;
        private DiagnosticOutcome _outcome = DiagnosticOutcome.Pending;
        private DiagnosticDelivery _delivery = DiagnosticDelivery.NotStarted;
        private bool _outcomeObserved;
        private bool _deliveryObserved;
        private ulong _sendCount;
        private ulong _retryCount;
        private double? _firstContent;
        private double? _total;
        private double _elapsed;
        private int? _status;
        private string? _reason;

        internal bool Apply(DiagnosticEvent value)
        {
            if (value.Sequence == LastSequence) return false;
            if (value.Sequence < LastSequence) { Incomplete = true; return false; }
            if (value.Sequence != LastSequence + 1) Incomplete = true;
            LastSequence = value.Sequence;
            var entry = value.Entry;
            _elapsed = entry.ElapsedSeconds;
            if (entry.RetryCount.HasValue) _retryCount = entry.RetryCount.Value;
            if (entry.FirstContentSeconds.HasValue) _firstContent = entry.FirstContentSeconds;
            if (entry.StatusCode.HasValue) _status = entry.StatusCode;
            switch (entry.Kind)
            {
                case DiagnosticEventKind.SendStarted:
                    _sendCount++;
                    _lastTarget = entry.Target;
                    _firstContent = null;
                    _status = null;
                    break;
                case DiagnosticEventKind.ResponseReady:
                    _firstContent = entry.FirstContentSeconds;
                    break;
                case DiagnosticEventKind.Outcome:
                    _firstContent = entry.FirstContentSeconds;
                    if (entry.Outcome.HasValue)
                    {
                        _outcome = entry.Outcome.Value;
                        _outcomeObserved = true;
                        _reason = _outcome == DiagnosticOutcome.Failure ? entry.Reason : null;
                    }
                    break;
                case DiagnosticEventKind.Delivery:
                    if (entry.Delivery.HasValue)
                    {
                        _delivery = entry.Delivery.Value;
                        _deliveryObserved = true;
                    }
                    break;
                case DiagnosticEventKind.Finished:
                    Finished = true;
                    _total = entry.ElapsedSeconds;
                    break;
            }
            return true;
        }

        internal DiagnosticSummary Snapshot(bool previous)
        {
            var terminal = previous || Finished;
            return new DiagnosticSummary(Request)
            {
                LastTarget = _lastTarget,
                Outcome = terminal && !_outcomeObserved ? DiagnosticOutcome.Unknown : _outcome,
                Delivery = terminal && !_deliveryObserved ? DiagnosticDelivery.Unknown : _delivery,
                SendCount = _sendCount,
                RetryCount = _retryCount,
                FirstContentSeconds = _firstContent,
                TotalSeconds = _total,
                LastElapsedSeconds = _elapsed,
                StatusCode = _status,
                FailureReason = _reason,
                Finished = Finished,
                Incomplete = Incomplete || (previous && !Finished) || (terminal && (!_outcomeObserved || !_deliveryObserved)),
                PreviousSession = previous,
            };
        }
    }

    private readonly record struct RequestKey(string Session, string RequestId);
    private sealed record QueryRow(DiagnosticSummary Summary, string Session, HashSet<DiagnosticTarget> Targets);
    private sealed class DayCache(DateOnly date)
    {
        internal readonly DateOnly Date = date;
        internal readonly Dictionary<RequestKey, SummaryState> Requests = [];
        internal readonly HashSet<RequestKey> IncompleteRequests = [];
        internal bool LegacyLoss;
        internal bool UnattributedLoss;
        internal bool ReadTruncated;
        /// <summary>已重放到的最大行号；只增不改的追加表让增量重放按行号游标即可。</summary>
        internal long Cursor;
        /// <summary>已读行的 payload_bytes 合计，配合单日读预算。</summary>
        internal long PayloadBytes;
    }
}
