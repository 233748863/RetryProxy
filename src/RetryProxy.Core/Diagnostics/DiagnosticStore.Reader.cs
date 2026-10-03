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
    // 仅缓存一个选定日期；总输入受每天 64 MiB 限制，事件只保留文件偏移。
    private DayCache? _cache;
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
                if (!RetainedForQuery(query.Date)) return new DiagnosticPage([], false, DiagnosticFilters.Empty, Warning);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
                if (!RetainedForQuery(query.Date)) return new DiagnosticPage([], false, DiagnosticFilters.Empty, Warning);
                await RefreshAsync(query.Date, cancellationToken).ConfigureAwait(false);
                if (!RetainedForQuery(query.Date))
                {
                    _cache = null;
                    return new DiagnosticPage([], false, DiagnosticFilters.Empty, Warning);
                }
                var rows = SnapshotRows(query.Date, query.Client, cancellationToken);
                var filters = BuildFilters(rows);
                var size = Math.Clamp(query.PageSize, 1, 100);
                var offset = Math.Max(0, query.Offset);
                var items = rows.Where(row => Matches(row, query))
                    .OrderByDescending(row => row.Summary.Request.StartedAt)
                    .ThenBy(row => row.Summary.Request.RequestId, StringComparer.Ordinal)
                    .Skip(offset).Take(size + 1).Select(row => row.Summary).ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                if (!RetainedForQuery(query.Date))
                {
                    _cache = null;
                    return new DiagnosticPage([], false, DiagnosticFilters.Empty, Warning);
                }
                return new DiagnosticPage(items.Take(size).ToArray(), items.Length > size, filters, Warning);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { SetWarning(); return new DiagnosticPage([], false, DiagnosticFilters.Empty, Warning); }
            finally { _queryGate.Release(); }
        }, cancellationToken);

    public Task<DiagnosticDetail> ReadDetailAsync(DateOnly date, string requestId, int offset = 0,
        int pageSize = 100, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await _queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!RetainedForQuery(date)) return new DiagnosticDetail(null, [], false, Warning);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
                if (!RetainedForQuery(date)) return new DiagnosticDetail(null, [], false, Warning);
                await RefreshAsync(date, cancellationToken).ConfigureAwait(false);
                if (!RetainedForQuery(date))
                {
                    _cache = null;
                    return new DiagnosticDetail(null, [], false, Warning);
                }
                var row = SnapshotRows(date, null, cancellationToken)
                    .Where(row => string.Equals(row.Summary.Request.RequestId, requestId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(row => row.Summary.Request.StartedAt)
                    .ThenByDescending(row => row.Session == _session).FirstOrDefault();
                if (row is null) return new DiagnosticDetail(null, [], false, Warning);
                var size = Math.Clamp(pageSize, 1, 100);
                offset = Math.Max(0, offset);
                if (!_cache!.Requests.TryGetValue(new RequestKey(row.Session, row.Summary.Request.RequestId), out var state))
                    return new DiagnosticDetail(row.Summary, [], false, Warning);
                var locations = state.Locations.Skip(offset).Take(size).ToArray();
                var events = new List<DiagnosticEvent>(locations.Length);
                var incomplete = false;
                foreach (var location in locations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await using var file = OpenRead(location.Path);
                        file.Position = location.Offset;
                        var bytes = new byte[location.Length];
                        await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                        _options.BytesReadForTest?.Invoke(bytes.Length);
                        var value = ReadEvent(bytes, date, row.Session);
                        if (value is null || value.RequestId != row.Summary.Request.RequestId || value.Sequence != location.Sequence)
                            throw new InvalidDataException();
                        events.Add(value);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch { SetWarning(); incomplete = true; }
                }
                if (!RetainedForQuery(date))
                {
                    _cache = null;
                    return new DiagnosticDetail(null, [], false, Warning);
                }
                return new DiagnosticDetail(row.Summary with { Incomplete = row.Summary.Incomplete || incomplete },
                    events, state.Locations.Count - offset > size, Warning);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { SetWarning(); return new DiagnosticDetail(null, [], false, Warning); }
            finally { _queryGate.Release(); }
        }, cancellationToken);

    private async Task RefreshAsync(DateOnly date, CancellationToken token)
    {
        if (_cache?.Date != date) _cache = new DayCache(date);
        var cache = _cache;
        if (!Directory.Exists(_directory)) return;
        var files = new Dictionary<string, (string Session, long Length)>(StringComparer.OrdinalIgnoreCase);
        var remaining = _options.MaxDailyBytes;
        foreach (var path in Directory.EnumerateFiles(_directory, $"{DateText(date)}-*.jsonl"))
        {
            token.ThrowIfCancellationRequested();
            if (!TryFileIdentity(path, out var fileDate, out var session) || fileDate != date) continue;
            if (files.Count >= MaxFilesPerDay) { SetWarning(); break; }
            var length = new FileInfo(path).Length;
            // 只读取本写入器已完整写出的字节，避免把一次并发写入误报为残行。
            if (session == _session && _publishedLengths.TryGetValue(path, out var published)) length = Math.Min(length, published);
            if (length > remaining) { length = remaining; SetWarning(); cache.IncompleteSessions.Add(session); }
            remaining -= length;
            files.Add(path, (session, length));
        }
        if (cache.Files.Any(pair => !files.TryGetValue(pair.Key, out var file) || file.Length < pair.Value.Offset))
        {
            SetWarning();
            _cache = cache = new DayCache(date);
        }
        foreach (var pair in files)
        {
            token.ThrowIfCancellationRequested();
            if (!cache.Files.TryGetValue(pair.Key, out var cursor))
            {
                cursor = new FileCursor(pair.Key, pair.Value.Session);
                cache.Files.Add(pair.Key, cursor);
            }
            if (pair.Value.Length > cursor.Offset) await ReadIncrementAsync(cache, cursor, pair.Value.Length, token).ConfigureAwait(false);
            if (cursor.Incomplete) cache.IncompleteSessions.Add(cursor.Session);
        }
    }

    private async Task ReadIncrementAsync(DayCache cache, FileCursor cursor, long length, CancellationToken token)
    {
        try
        {
            await using var file = OpenRead(cursor.Path);
            file.Position = cursor.Offset;
            var buffer = new byte[16 * 1024];
            var line = new byte[_options.MaxEventBytes];
            var used = 0;
            var overlong = false;
            var lineStart = cursor.Offset;
            while (file.Position < length)
            {
                token.ThrowIfCancellationRequested();
                var before = file.Position;
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - before)), token).ConfigureAwait(false);
                if (read == 0) break;
                _options.BytesReadForTest?.Invoke(read);
                for (var index = 0; index < read; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var value = buffer[index];
                    if (value == '\n')
                    {
                        if (overlong || used == 0) BadLine(cursor);
                        else ApplyLine(cache, cursor, line.AsSpan(0, used), lineStart);
                        cursor.Offset = before + index + 1;
                        lineStart = cursor.Offset;
                        used = 0;
                        overlong = false;
                    }
                    else if (used < line.Length - 1) line[used++] = value;
                    else overlong = true;
                }
            }
            if (used != 0 || overlong)
            {
                // 旧会话的完整末行即使缺换行也可恢复；半条 JSON 仍只标记缺失。
                if (overlong) BadLine(cursor);
                else ApplyLine(cache, cursor, line.AsSpan(0, used), lineStart);
                cursor.Offset = length;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { BadLine(cursor); }
    }

    private void ApplyLine(DayCache cache, FileCursor cursor, ReadOnlySpan<byte> bytes, long offset)
    {
        try
        {
            var value = ReadEvent(bytes, cache.Date, cursor.Session);
            if (value is null)
            {
                var control = JsonSerializer.Deserialize<ControlRecord>(bytes, JsonOptions);
                if (control is not { Version: 1, Control: "incomplete" } || control.Date != cache.Date || control.SessionId != cursor.Session)
                    throw new InvalidDataException();
                BadLine(cursor);
                return;
            }
            var key = new RequestKey(value.SessionId, value.RequestId);
            if (!cache.Requests.TryGetValue(key, out var state))
            {
                // 每个合法事件的固定身份字段至少占 64 字节；防御手工构造的异常文件。
                if (value.Request is null || cache.Requests.Count >= _options.MaxDailyBytes / 64) throw new InvalidDataException();
                state = new SummaryState(value.Request, value.SessionId);
                cache.Requests.Add(key, state);
            }
            if (value.Request is not null && state.Request != value.Request) { state.Incomplete = true; SetWarning(); }
            if (value.Sequence <= state.LastSequence && state.Locations.Exists(location => location.Sequence == value.Sequence)) return;
            if (!state.Apply(value)) return;
            state.Locations.Add(new EventLocation(cursor.Path, offset, bytes.Length, value.Sequence));
            if (value.Entry.Kind == DiagnosticEventKind.SendStarted && value.Entry.Target is not null) state.Targets.Add(value.Entry.Target);
            if (state.Incomplete) SetWarning();
        }
        catch { BadLine(cursor); }
    }

    private static DiagnosticEvent? ReadEvent(ReadOnlySpan<byte> bytes, DateOnly date, string session)
    {
        var value = JsonSerializer.Deserialize<DiagnosticEvent>(bytes, JsonOptions);
        if (value?.Entry is null) return null;
        if (value.Version != 1 || value.Date != date || value.SessionId != session || value.Sequence <= 0 ||
            !Guid.TryParse(value.RequestId, out _) || !Enum.IsDefined(value.Entry.Kind) ||
            (value.Request is null && value.Entry.Kind == DiagnosticEventKind.Started) ||
            (value.Request is not null && (value.Request.RequestId != value.RequestId || value.Request.Date != date || !Enum.IsDefined(value.Request.Client))) ||
            (value.Entry.Outcome.HasValue && !Enum.IsDefined(value.Entry.Outcome.Value)) ||
            (value.Entry.Delivery.HasValue && !Enum.IsDefined(value.Entry.Delivery.Value))) throw new InvalidDataException();
        // 磁盘内容同样不可信，查询返回前再次经过公共白名单。
        return value with { Request = value.Request is null ? null : DiagnosticSafety.Sanitize(value.Request), Entry = DiagnosticSafety.Sanitize(value.Entry) };
    }

    private void BadLine(FileCursor cursor)
    {
        cursor.Incomplete = true;
        SetWarning();
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private List<QueryRow> SnapshotRows(DateOnly date, ClientType? client, CancellationToken token)
    {
        var rows = new Dictionary<RequestKey, QueryRow>();
        foreach (var pair in _cache!.Requests)
        {
            token.ThrowIfCancellationRequested();
            var state = pair.Value;
            if (client.HasValue && state.Request.Client != client.Value) continue;
            var summary = state.Snapshot(state.Session != _session);
            var damaged = _cache.IncompleteSessions.Contains(state.Session) ||
                (state.Session == _session && _damagedDates.ContainsKey(date));
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
            summary = summary with { Incomplete = summary.Incomplete || previous?.Summary.Incomplete == true || _damagedDates.ContainsKey(date) };
            rows[key] = new QueryRow(summary, _session, targets);
        }
        if (rows.Values.Any(row => row.Summary.Incomplete)) SetWarning();
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
