using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RetryProxy.Core.Cache;

namespace RetryProxy.Core.Metrics;

/// <summary>
/// 当日统计状态（对应 daily.rs 的 MetricsState）：按本地日历日累计，跨日自动切换。
/// </summary>
internal sealed class MetricsState
{
    private DateOnly _date;
    private readonly SortedDictionary<string, DailyRequest> _records = new(StringComparer.Ordinal);
    private MetricsSnapshot _totals;
    private ulong _sequence;
    private readonly IDailyStorage? _storage;
    private IDailyJournal? _journal;
    private readonly SortedSet<string> _dirty = new(StringComparer.Ordinal);
    private string? _readWarning;
    private string? _writeWarning;

    public SortedDictionary<string, ActiveRequest> Active { get; private set; } = new(StringComparer.Ordinal);

    public MetricsState(DateTime now, IDailyStorage? storage)
        : this(DateOnly.FromDateTime(now), storage, importLegacy: true)
    {
    }

    private MetricsState(DateOnly date, IDailyStorage? storage, bool importLegacy)
    {
        _date = date;
        _storage = storage;
        _totals = new MetricsSnapshot { StatisticsDate = date.ToString("yyyy-MM-dd") };
        if (storage is null)
        {
            return;
        }

        DailyJournalOpenResult opened;
        try
        {
            opened = storage.Open(date, importLegacy);
        }
        catch (IOException error)
        {
            opened = new DailyJournalOpenResult { OpenErrorKind = error.GetType().Name };
        }
        catch (UnauthorizedAccessException)
        {
            opened = new DailyJournalOpenResult { OpenErrorKind = "PermissionDenied" };
        }

        if (opened.OpenErrorKind is not null)
        {
            _readWarning = $"当日统计日志恢复失败（{opened.OpenErrorKind}），当前仅显示本次运行数据";
            return;
        }

        _journal = opened.Journal;
        _readWarning = opened.Warning;
        _totals.RestoredFromLegacyLogs = opened.RestoredFromLegacyLogs;
        // 最近 20 条按完成顺序而不是请求 ID 顺序恢复。
        var ordered = opened.Records.Values
            .OrderBy(record => record.Cache?.CompletedAtUnixMs ?? long.MinValue)
            .ThenBy(record => record.Sequence)
            .ToList();
        foreach (var record in ordered)
        {
            _sequence = Math.Max(_sequence, record.Sequence);
            Account(new DailyRequest(), record, isNew: true);
            _records[record.RequestId] = record;
        }
    }

    public void Rollover(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (_date == today)
        {
            return;
        }

        Flush();
        var unsaved = _dirty.Count > 0;
        var carried = Active.Keys
            .Where(id => _records.TryGetValue(id, out var record) && record.Outcome is null)
            .ToList();
        var next = new MetricsState(today, _storage, importLegacy: false);
        next.Active = Active;
        Active = new SortedDictionary<string, ActiveRequest>(StringComparer.Ordinal);
        if (unsaved)
        {
            next._readWarning = "上一日部分统计未能写入日志，请检查日志目录是否可写";
        }

        CopyFrom(next);
        // 跨越午夜仍在处理的请求也算新一天的工作量；已完成但仍在交付的只保留活动阶段。
        var nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        foreach (var id in carried)
        {
            Change(id, new DailyChange.Started(), nowMs);
        }
    }

    private void CopyFrom(MetricsState next)
    {
        _date = next._date;
        _records.Clear();
        foreach (var pair in next._records)
        {
            _records[pair.Key] = pair.Value;
        }

        _totals = next._totals;
        _sequence = next._sequence;
        Active = next.Active;
        _journal = next._journal;
        _dirty.Clear();
        foreach (var id in next._dirty)
        {
            _dirty.Add(id);
        }

        _readWarning = next._readWarning;
        _writeWarning = next._writeWarning;
    }

    public void Change(string id, DailyChange change, long nowMs)
    {
        var existed = _records.TryGetValue(id, out var record);
        if (record is null)
        {
            record = new DailyRequest { RequestId = id };
            _records[id] = record;
        }

        var before = record.Clone();
        switch (change)
        {
            case DailyChange.Started:
                break;
            case DailyChange.Succeeded succeeded when record.Outcome is null:
                record.Outcome = RequestOutcome.Success;
                var cache = succeeded.Cache?.Clone();
                if (cache is not null)
                {
                    cache.RequestId = id;
                    cache.CompletedAtUnixMs = nowMs;
                }

                record.Cache = cache;
                break;
            case DailyChange.Failed when record.Outcome is null:
                record.Outcome = RequestOutcome.Failure;
                break;
            case DailyChange.Retry retry when record.Outcome is null:
                record.Retry(retry.Attempt);
                break;
            case DailyChange.CacheKey key when record.CacheKey is null:
                record.CacheKey = key.State;
                break;
            case DailyChange.CacheFallback:
                record.CacheFallback = true;
                break;
        }

        var changed = !existed
            || before.Outcome != record.Outcome
            || before.RetryCount != record.RetryCount
            || before.CacheKey != record.CacheKey
            || before.CacheFallback != record.CacheFallback;
        if (changed)
        {
            record.UpdatedAtUnixMs = nowMs;
            _sequence = Saturating.Add(_sequence, 1);
            record.Sequence = _sequence;
            Account(before, record, !existed);
            if (_storage is not null)
            {
                _dirty.Add(id);
            }
        }

        Flush();
    }

    private void Account(DailyRequest before, DailyRequest after, bool isNew)
    {
        if (isNew)
        {
            _totals.TotalRequests = Saturating.Add(_totals.TotalRequests, 1);
        }

        _totals.RetryCount = Saturating.Add(_totals.RetryCount, Saturating.Sub(after.RetryCount, before.RetryCount));
        if (before.Outcome is null)
        {
            switch (after.Outcome)
            {
                case RequestOutcome.Success:
                    _totals.SuccessfulRequests = Saturating.Add(_totals.SuccessfulRequests, 1);
                    if (after.Cache is { } cache)
                    {
                        _totals.Cache.Record(cache.Clone());
                        if (cache.InputAccounting == CacheInputAccounting.IncludesCached && PromptCache.IsGptModel(cache.Model))
                        {
                            _totals.GptCache.Record(cache.Clone());
                        }
                    }

                    break;
                case RequestOutcome.Failure:
                    _totals.FailedRequests = Saturating.Add(_totals.FailedRequests, 1);
                    break;
            }
        }

        foreach (var snapshot in new[] { _totals.Cache, _totals.GptCache })
        {
            if (before.CacheKey is null)
            {
                switch (after.CacheKey)
                {
                    case CacheKeyState.Client:
                        snapshot.ClientKeyRequests = Saturating.Add(snapshot.ClientKeyRequests, 1);
                        break;
                    case CacheKeyState.Added:
                        snapshot.AddedKeyRequests = Saturating.Add(snapshot.AddedKeyRequests, 1);
                        break;
                    case CacheKeyState.MissingSession:
                        snapshot.MissingSessionRequests = Saturating.Add(snapshot.MissingSessionRequests, 1);
                        break;
                    case CacheKeyState.Unsupported:
                        snapshot.UnsupportedKeyRequests = Saturating.Add(snapshot.UnsupportedKeyRequests, 1);
                        break;
                }
            }

            if (after.CacheFallback && !before.CacheFallback)
            {
                snapshot.CompatibilityFallbacks = Saturating.Add(snapshot.CompatibilityFallbacks, 1);
            }
        }
    }

    private void Flush()
    {
        if (_journal is null)
        {
            return;
        }

        while (_dirty.Count > 0)
        {
            var id = _dirty.Min!;
            try
            {
                _journal.Append(_records[id]);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _writeWarning = $"当日统计日志写入失败（{error.GetType().Name}），未保存数据将在下次更新时重试";
                return;
            }

            _dirty.Remove(id);
        }

        _writeWarning = null;
    }

    public MetricsSnapshot Snapshot()
    {
        var snapshot = _totals.Clone();
        snapshot.Requests = Active.Values.Select(request => request.Clone()).ToList();
        snapshot.ActiveRequests = (ulong)snapshot.Requests.Count;
        var liveUnfinished = (ulong)Active.Keys.Count(id => _records.TryGetValue(id, out var record) && record.Outcome is null);
        snapshot.HistoricalUnfinishedRequests = Saturating.Sub(
            Saturating.Sub(Saturating.Sub(snapshot.TotalRequests, snapshot.SuccessfulRequests), snapshot.FailedRequests),
            liveUnfinished);
        snapshot.StatisticsWarning = _writeWarning ?? _readWarning;
        return snapshot;
    }
}
