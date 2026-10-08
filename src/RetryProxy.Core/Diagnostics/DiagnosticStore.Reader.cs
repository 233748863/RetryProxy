using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Diagnostics;

public sealed partial class DiagnosticStore
{
    private readonly SemaphoreSlim _queryGate = new(1, 1);
    // 仅缓存一个选定日期；总输入受每天 64 MiB 限制，事件只保留归并状态与重放游标。
    private DayCache? _cache;
    private const int ReadBatchSize = 256;
    internal int CachedDateCountForTest => _cache is null ? 0 : 1;

    private bool RetainedForQuery(DateOnly date)
    {
        if (_cache is not null && !Retained(_cache.Date)) _cache = null;
        return Retained(date);
    }

    public Task<DiagnosticPage> QueryAsync(DiagnosticQuery query, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await _queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!RetainedForQuery(query.Date)) return new DiagnosticPage([], false, DiagnosticFilters.Empty, null);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
                if (!RetainedForQuery(query.Date)) return new DiagnosticPage([], false, DiagnosticFilters.Empty, null);
                var readComplete = Refresh(query.Date, cancellationToken);
                if (!RetainedForQuery(query.Date))
                {
                    _cache = null;
                    return new DiagnosticPage([], false, DiagnosticFilters.Empty, null);
                }
                var allRows = SnapshotRows(query.Date, null, cancellationToken);
                var rows = allRows.Where(row => row.Summary.Request.Client == query.Client).ToArray();
                var filters = BuildFilters(rows);
                var size = Math.Clamp(query.PageSize, 1, 100);
                var offset = Math.Max(0, query.Offset);
                var matches = rows.Where(row => Matches(row, query)).ToArray();
                var items = matches
                    .OrderByDescending(row => row.Summary.Request.StartedAt)
                    .ThenBy(row => row.Summary.Request.RequestId, StringComparer.Ordinal)
                    .Skip(offset).Take(size + 1).Select(row => row.Summary).ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                if (!RetainedForQuery(query.Date))
                {
                    _cache = null;
                    return new DiagnosticPage([], false, DiagnosticFilters.Empty, null);
                }
                // 提示按整组筛选结果计算（包含后续分页），不借用其他日期/请求留下的全局状态。
                var warning = matches.Any(row => row.Summary.Incomplete) ? IncompleteWarning
                    : !readComplete ? ReadWarning
                    : HasUnattributedLoss(query, allRows) ? UnattributedWarning : BackgroundWarning;
                return new DiagnosticPage(items.Take(size).ToArray(), items.Length > size, filters, warning);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { SetWarning(); return new DiagnosticPage([], false, DiagnosticFilters.Empty, ReadWarning); }
            finally { _queryGate.Release(); }
        }, cancellationToken);

    public Task<DiagnosticDetail> ReadDetailAsync(DateOnly date, string requestId, int offset = 0,
        int pageSize = 100, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await _queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!RetainedForQuery(date)) return new DiagnosticDetail(null, [], false, null);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
                if (!RetainedForQuery(date)) return new DiagnosticDetail(null, [], false, null);
                var readComplete = Refresh(date, cancellationToken);
                if (!RetainedForQuery(date))
                {
                    _cache = null;
                    return new DiagnosticDetail(null, [], false, null);
                }
                var row = SnapshotRows(date, null, cancellationToken)
                    .Where(row => string.Equals(row.Summary.Request.RequestId, requestId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(row => row.Summary.Request.StartedAt)
                    .ThenByDescending(row => row.Session == _session).FirstOrDefault();
                if (row is null)
                    return new DiagnosticDetail(null, [], false, !readComplete ? ReadWarning
                        : HasRequestLoss(date, requestId) ? IncompleteWarning : null);
                var size = Math.Clamp(pageSize, 1, 100);
                offset = Math.Max(0, offset);
                List<EventRow> rows;
                try
                {
                    rows = ReadDetailRows(date, row.Session, row.Summary.Request.RequestId, offset, size + 1);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch
                {
                    SetWarning();
                    return new DiagnosticDetail(row.Summary, [], false, ReadWarning);
                }
                var events = new List<DiagnosticEvent>(Math.Min(rows.Count, size));
                var incomplete = false;
                foreach (var item in rows.Take(size))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _options.BytesReadForTest?.Invoke(item.PayloadBytes);
                    try
                    {
                        events.Add(ReadRowEvent(date, row.Session, row.Summary.Request.RequestId, item));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch { SetWarning(); incomplete = true; }
                }
                if (!RetainedForQuery(date))
                {
                    _cache = null;
                    return new DiagnosticDetail(null, [], false, null);
                }
                var summary = row.Summary with { Incomplete = row.Summary.Incomplete || incomplete };
                return new DiagnosticDetail(summary, events, rows.Count > size,
                    summary.Incomplete ? IncompleteWarning : !readComplete ? ReadWarning : null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { SetWarning(); return new DiagnosticDetail(null, [], false, ReadWarning); }
            finally { _queryGate.Release(); }
        }, cancellationToken);

    /// <summary>按行号游标增量重放当日事件（追加表只增不改），并合并当天的"不完整"标记。</summary>
    private bool Refresh(DateOnly date, CancellationToken token)
    {
        if (_cache?.Date != date) _cache = new DayCache(date);
        var cache = _cache;
        var complete = true;
        // 旧版日期标记只用于列表的当日提示；新标记只影响所指向的请求。
        try
        {
            using var connection = _data.Database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM diagnostic_marks WHERE date = $date);";
            command.Parameters.AddWithValue("$date", DateText(date));
            cache.LegacyLoss = (long)command.ExecuteScalar()! != 0;
            command.CommandText = "SELECT session_id, request_id FROM diagnostic_request_marks WHERE date = $date;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) MarkReadLoss(cache, new RequestKey(reader.GetString(0), reader.GetString(1)));
        }
        catch { SetWarning(); complete = false; }

        if (cache.ReadTruncated) return complete;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            List<EventRow> batch;
            try
            {
                batch = ReadBatch(date, cache.Cursor);
            }
            catch
            {
                SetWarning();
                return false;
            }
            foreach (var row in batch)
            {
                token.ThrowIfCancellationRequested();
                // 读预算用尽或日期在读取期间过期都尽早停下。
                if (!Retained(date)) return complete;
                if (cache.PayloadBytes + row.PayloadBytes > _options.MaxDailyBytes)
                {
                    TruncateRemaining(cache, date, row.Id);
                    return complete;
                }
                cache.PayloadBytes += row.PayloadBytes;
                _options.BytesReadForTest?.Invoke(row.PayloadBytes);
                ApplyRow(cache, row);
                cache.Cursor = row.Id;
            }
            if (batch.Count < ReadBatchSize) return complete;
        }
    }

    private List<EventRow> ReadBatch(DateOnly date, long afterId)
    {
        using var connection = _data.Database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, request_id, sequence, kind, entry_json, request_json, payload_bytes
            FROM diagnostic_events WHERE date = $date AND id > $after ORDER BY id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$date", DateText(date));
        command.Parameters.AddWithValue("$after", afterId);
        command.Parameters.AddWithValue("$limit", ReadBatchSize);
        return ReadRows(command);
    }

    private List<EventRow> ReadDetailRows(DateOnly date, string session, string requestId, int offset, int limit)
    {
        using var connection = _data.Database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, request_id, sequence, kind, entry_json, request_json, payload_bytes
            FROM diagnostic_events
            WHERE date = $date AND session_id = $session AND request_id = $request
            ORDER BY sequence LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$date", DateText(date));
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$request", requestId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        return ReadRows(command);
    }

    private static List<EventRow> ReadRows(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        var rows = new List<EventRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new EventRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7)));
        }
        return rows;
    }

    /// <summary>读预算用尽：只标记未读完的请求；列表保留当日读取不完整的提示。</summary>
    private void TruncateRemaining(DayCache cache, DateOnly date, long fromId)
    {
        SetWarning();
        cache.ReadTruncated = true;
        try
        {
            using var connection = _data.Database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT session_id, request_id FROM diagnostic_events WHERE date = $date AND id >= $after LIMIT $limit;";
            command.Parameters.AddWithValue("$date", DateText(date));
            command.Parameters.AddWithValue("$after", fromId);
            command.Parameters.AddWithValue("$limit", MaxRequestMarksPerDate);
            using var reader = command.ExecuteReader();
            while (reader.Read()) MarkReadLoss(cache, new RequestKey(reader.GetString(0), reader.GetString(1)));
        }
        catch { SetWarning(); }
    }

    private void ApplyRow(DayCache cache, EventRow row)
    {
        try
        {
            var value = ReadRowEvent(cache.Date, row.Session, row.RequestId, row);
            var key = new RequestKey(row.Session, row.RequestId);
            if (!cache.Requests.TryGetValue(key, out var state))
            {
                // 每个合法请求的首行必须携带元数据；同时防御手工构造的异常库。
                if (value.Request is null || cache.Requests.Count >= _options.MaxDailyBytes / 64) throw new InvalidDataException();
                state = new SummaryState(value.Request, row.Session);
                cache.Requests.Add(key, state);
            }
            if (value.Request is not null && state.Request != value.Request) { state.Incomplete = true; SetWarning(); }
            if (!state.Apply(value)) return;
            if (value.Entry.Kind == DiagnosticEventKind.SendStarted && value.Entry.Target is not null) state.Targets.Add(value.Entry.Target);
            if (state.Incomplete) SetWarning();
        }
        catch
        {
            MarkReadLoss(cache, new RequestKey(row.Session, row.RequestId));
            SetWarning();
        }
    }

    private DiagnosticEvent ReadRowEvent(DateOnly date, string session, string requestId, EventRow row)
    {
        if (row.PayloadBytes > _options.MaxEventBytes || row.Sequence <= 0 || row.Session != session ||
            !string.Equals(row.RequestId, requestId, StringComparison.Ordinal) ||
            !Guid.TryParse(row.RequestId, out _) || !Guid.TryParseExact(row.Session, "N", out _)) throw new InvalidDataException();
        var entry = JsonSerializer.Deserialize<DiagnosticEntry>(row.EntryJson, JsonOptions);
        if (entry is null || !Enum.IsDefined(entry.Kind) || KindText(entry.Kind) != row.Kind ||
            (entry.Outcome.HasValue && !Enum.IsDefined(entry.Outcome.Value)) ||
            (entry.Delivery.HasValue && !Enum.IsDefined(entry.Delivery.Value))) throw new InvalidDataException();
        DiagnosticRequestInfo? request = null;
        if (row.RequestJson is not null)
        {
            request = JsonSerializer.Deserialize<DiagnosticRequestInfo>(row.RequestJson, JsonOptions);
            if (request is null || request.RequestId != row.RequestId || request.Date != date || !Enum.IsDefined(request.Client))
                throw new InvalidDataException();
        }
        // 库内容同样不可信，查询返回前再次经过公共白名单。
        return new DiagnosticEvent(row.Session, row.RequestId, date, row.Sequence,
            request is null ? null : DiagnosticSafety.Sanitize(request), DiagnosticSafety.Sanitize(entry));
    }

    private sealed record EventRow(long Id, string Session, string RequestId, long Sequence, string Kind,
        string EntryJson, string? RequestJson, long PayloadBytes);

    private static void MarkReadLoss(DayCache cache, RequestKey key)
    {
        if (Guid.TryParseExact(key.Session, "N", out _) && Guid.TryParse(key.RequestId, out _))
            cache.IncompleteRequests.Add(key);
        else
            cache.UnattributedLoss = true;
    }

    private HashSet<RequestKey> PendingRequestLosses(DateOnly date)
    {
        lock (_damageGate)
            return _pendingRequestMarks.Where(mark => mark.Date == date)
                .Select(mark => new RequestKey(_session, mark.RequestId)).ToHashSet();
    }

    private bool HasRequestLoss(DateOnly date, string requestId) =>
        _cache!.IncompleteRequests.Concat(PendingRequestLosses(date))
            .Any(key => string.Equals(key.RequestId, requestId, StringComparison.OrdinalIgnoreCase));

    private bool HasUnattributedLoss(DiagnosticQuery query, IReadOnlyList<QueryRow> rows)
    {
        var cache = _cache!;
        if (cache.LegacyLoss || cache.UnattributedLoss || cache.ReadTruncated || _damagedDates.ContainsKey(query.Date)) return true;
        var known = rows.Select(row => new RequestKey(row.Session, row.Summary.Request.RequestId)).ToHashSet();
        return cache.IncompleteRequests.Concat(PendingRequestLosses(query.Date)).Any(key => !known.Contains(key) &&
            (string.IsNullOrWhiteSpace(query.RequestId) || key.RequestId.StartsWith(query.RequestId.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    private List<QueryRow> SnapshotRows(DateOnly date, ClientType? client, CancellationToken token)
    {
        var rows = new Dictionary<RequestKey, QueryRow>();
        var pendingLosses = PendingRequestLosses(date);
        foreach (var pair in _cache!.Requests)
        {
            token.ThrowIfCancellationRequested();
            var state = pair.Value;
            if (client.HasValue && state.Request.Client != client.Value) continue;
            var summary = state.Snapshot(state.Session != _session);
            var damaged = _cache.IncompleteRequests.Contains(pair.Key) || pendingLosses.Contains(pair.Key);
            rows.Add(pair.Key, new QueryRow(summary with { Incomplete = summary.Incomplete || damaged }, state.Session, state.Targets));
        }
        RequestHandle[] live;
        lock (_liveGate) live = _live.Values.Where(handle => handle.Request.Date == date &&
            (!client.HasValue || handle.Request.Client == client.Value)).ToArray();
        foreach (var handle in live)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = handle.Snapshot();
            var key = new RequestKey(_session, handle.Request.RequestId);
            rows.TryGetValue(key, out var previous);
            var targets = previous is null ? [] : new HashSet<DiagnosticTarget>(previous.Targets);
            if (snapshot.LastSendTarget is not null) targets.Add(snapshot.LastSendTarget);
            var summary = snapshot.Summary;
            summary = summary with { Incomplete = summary.Incomplete || previous?.Summary.Incomplete == true ||
                _cache.IncompleteRequests.Contains(key) || pendingLosses.Contains(key) };
            rows[key] = new QueryRow(summary, _session, targets);
        }
        return rows.Values.ToList();
    }

    private static bool Matches(QueryRow row, DiagnosticQuery query)
    {
        if (query.Outcome.HasValue && row.Summary.Outcome != query.Outcome.Value) return false;
        if (!string.IsNullOrWhiteSpace(query.RequestId) &&
            !row.Summary.Request.RequestId.StartsWith(query.RequestId.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(query.ProviderId) && string.IsNullOrEmpty(query.KeyId) && string.IsNullOrEmpty(query.Model)) return true;
        return row.Targets.Any(target =>
            (string.IsNullOrEmpty(query.ProviderId) || target.ProviderId == query.ProviderId) &&
            (string.IsNullOrEmpty(query.KeyId) || target.KeyId == query.KeyId) &&
            (string.IsNullOrEmpty(query.Model) || target.Model == query.Model));
    }

    private static DiagnosticFilters BuildFilters(IEnumerable<QueryRow> rows)
    {
        var providers = new Dictionary<string, DiagnosticChoice>(StringComparer.Ordinal);
        var keys = new Dictionary<(string Provider, string Key), DiagnosticChoice>();
        var models = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        foreach (var target in row.Targets)
        {
            if (!string.IsNullOrEmpty(target.ProviderId)) providers[target.ProviderId] = new DiagnosticChoice(target.ProviderId, target.ProviderName);
            if (!string.IsNullOrEmpty(target.KeyId)) keys[(target.ProviderId, target.KeyId)] = new DiagnosticChoice(target.KeyId, target.KeyName, target.ProviderId);
            if (!string.IsNullOrEmpty(target.Model)) models.Add(target.Model);
        }
        return new DiagnosticFilters(providers.Values.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray(),
            keys.Values.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray(), models.Order(StringComparer.Ordinal).ToArray());
    }
}
