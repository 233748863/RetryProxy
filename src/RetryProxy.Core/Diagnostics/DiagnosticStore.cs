using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Storage;

namespace RetryProxy.Core.Diagnostics;

/// <summary>代码默认值及测试注入；不属于用户配置。</summary>
public sealed class DiagnosticStoreOptions
{
    public int QueueCapacity { get; init; } = 4096;
    public int MaxEventBytes { get; init; } = 2048;
    public long MaxDailyBytes { get; init; } = 64L * 1024 * 1024;
    public int FlushEventCount { get; init; } = 256;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxLiveRequests { get; init; } = 4096;
    internal Func<Action<SqliteConnection>, Task>? WriteForTest { get; init; }
    internal Action<long>? BytesReadForTest { get; init; }
}

/// <summary>独立、尽力而为的诊断旁路；采集线程只做序列化与入队，落库由后台批量事务完成（logs\data.db 的 diagnostic_events）。</summary>
public sealed partial class DiagnosticStore : IDiagnosticRepository
{
    public const string IncompleteWarning = "诊断记录不完整，不影响请求转发";
    private const int ControlReserveBytes = 256;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly DataDatabase _data;
    private readonly TimeProvider _time;
    private readonly DiagnosticStoreOptions _options;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly Channel<WriteItem> _queue;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writer;
    private readonly object _liveGate = new();
    private readonly Dictionary<string, RequestHandle> _live = new(StringComparer.Ordinal);
    private readonly Queue<string> _liveOrder = new();
    private readonly ConcurrentDictionary<DateOnly, byte> _damagedDates = new();
    // 只由写循环线程访问：每个日期已计入容量的 payload_bytes 合计。
    private readonly Dictionary<DateOnly, long> _dayPayload = new();
    private readonly HashSet<DateOnly> _markedDates = [];
    private int _disposed;
    private int _warning;
    private int _notificationScheduled;

    public DiagnosticStore(DataDatabase data, TimeProvider? timeProvider = null, DiagnosticStoreOptions? options = null)
    {
        _data = data;
        _time = timeProvider ?? TimeProvider.System;
        var supplied = options ?? new DiagnosticStoreOptions();
        _options = new DiagnosticStoreOptions
        {
            QueueCapacity = Math.Clamp(supplied.QueueCapacity, 1, 4096),
            MaxEventBytes = Math.Clamp(supplied.MaxEventBytes, ControlReserveBytes, 2048),
            MaxDailyBytes = Math.Clamp(supplied.MaxDailyBytes, ControlReserveBytes, 64L * 1024 * 1024),
            FlushEventCount = Math.Clamp(supplied.FlushEventCount, 1, 256),
            FlushInterval = supplied.FlushInterval > TimeSpan.Zero && supplied.FlushInterval < TimeSpan.FromSeconds(1)
                ? supplied.FlushInterval : TimeSpan.FromSeconds(1),
            MaxLiveRequests = Math.Clamp(supplied.MaxLiveRequests, 1, 4096),
            WriteForTest = supplied.WriteForTest,
            BytesReadForTest = supplied.BytesReadForTest,
        };
        _queue = Channel.CreateBounded<WriteItem>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public event Action? Changed;
    public DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
    public string? Warning => Volatile.Read(ref _warning) == 0 ? null : IncompleteWarning;
    internal string SessionIdForTest => _session;
    internal int LiveCountForTest { get { lock (_liveGate) return _live.Count; } }

    private bool Retained(DateOnly date)
    {
        var today = Today;
        return date <= today && date.DayNumber >= today.DayNumber - 6;
    }

    internal static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>kind 列的写法与 entry_json 里的枚举名一致（camelCase）。</summary>
    internal static string KindText(DiagnosticEventKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    public IDiagnosticRequest? Begin(DiagnosticRequestInfo request)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !Guid.TryParse(request.RequestId, out _) || !Retained(request.Date)) return null;
            request = DiagnosticSafety.Sanitize(request);
            RequestHandle handle;
            lock (_liveGate)
            {
                if (_live.TryGetValue(request.RequestId, out var existing)) return existing;
                while (_live.Count >= _options.MaxLiveRequests && _liveOrder.TryDequeue(out var oldest)) _live.Remove(oldest);
                handle = new RequestHandle(this, request);
                _live.Add(request.RequestId, handle);
                _liveOrder.Enqueue(request.RequestId);
            }
            handle.Record(new DiagnosticEntry(DiagnosticEventKind.Started, 0));
            return handle;
        }
        catch
        {
            SetWarning();
            return null;
        }
    }

    private void Enqueue(RequestHandle handle, DiagnosticEvent value)
    {
        if (!Retained(value.Date) || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            // 首行携带请求元数据，其余事件省略（与旧格式"后续行可省略 request"的恢复语义一致）。
            var entry = JsonSerializer.Serialize(value.Entry, JsonOptions);
            var info = value.Sequence == 1 && value.Request is not null ? JsonSerializer.Serialize(value.Request, JsonOptions) : null;
            var payload = Encoding.UTF8.GetByteCount(entry) + (info is null ? 0 : Encoding.UTF8.GetByteCount(info));
            var row = new PendingRow(_session, value.RequestId, value.Date, value.Sequence, KindText(value.Entry.Kind), entry, info, payload,
                _time.GetUtcNow().ToUnixTimeMilliseconds());
            if (payload > _options.MaxEventBytes || !_queue.Writer.TryWrite(new WriteItem(row, value.Date, null)))
            {
                handle.State.Incomplete = true;
                Damage(value.Date);
            }
        }
        catch
        {
            handle.State.Incomplete = true;
            Damage(value.Date);
        }
        Wake();
        NotifyChanged();
    }

    private void Damage(DateOnly date)
    {
        var added = Retained(date) && _damagedDates.TryAdd(date, 0);
        SetWarning();
        if (added) Wake();
    }

    private void SetWarning()
    {
        if (Interlocked.Exchange(ref _warning, 1) == 0) NotifyChanged();
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    // 通知在采集/写入之外执行，订阅者的阻塞与异常均不能拖住网络或后台写入器。
    private void NotifyChanged()
    {
        if (Interlocked.CompareExchange(ref _notificationScheduled, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var callbacks = Changed;
                if (callbacks is null) return;
                foreach (var callback in callbacks.GetInvocationList())
                {
                    try { ((Action)callback)(); }
                    catch { }
                }
            }
            finally { Interlocked.Exchange(ref _notificationScheduled, 0); }
        });
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
        {
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(new WriteItem(null, default, completion), cancellationToken).ConfigureAwait(false);
            Wake();
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteLoopAsync()
    {
        var cleanupDate = DateOnly.MinValue;
        var lastFlush = _time.GetTimestamp();
        var pending = new List<PendingRow>();
        try
        {
            while (true)
            {
                if (cleanupDate != Today)
                {
                    await CleanupAsync().ConfigureAwait(false);
                    cleanupDate = Today;
                    NotifyChanged();
                }
                var read = 0;
                while (read++ < _options.FlushEventCount && _queue.Reader.TryRead(out var item))
                {
                    if (item.Completion is not null)
                    {
                        await CommitAsync(pending).ConfigureAwait(false);
                        await WriteMarksAsync().ConfigureAwait(false);
                        lastFlush = _time.GetTimestamp();
                        item.Completion.TrySetResult();
                    }
                    else if (item.Row is { } row && Retained(row.Date) && Accept(row))
                    {
                        pending.Add(row);
                        if (pending.Count >= _options.FlushEventCount)
                        {
                            await CommitAsync(pending).ConfigureAwait(false);
                            lastFlush = _time.GetTimestamp();
                        }
                    }
                }
                await WriteMarksAsync().ConfigureAwait(false);
                if (pending.Count > 0 && _time.GetElapsedTime(lastFlush) >= _options.FlushInterval)
                {
                    await CommitAsync(pending).ConfigureAwait(false);
                    lastFlush = _time.GetTimestamp();
                }
                if (_queue.Reader.TryPeek(out _)) continue;
                if (_queue.Reader.Completion.IsCompleted) break;
                // 每秒刷一批，同时在下一本地自然日第一次唤醒时复核七天留存。
                var untilMidnight = TimeSpan.FromDays(1) - _time.GetLocalNow().TimeOfDay;
                var delay = untilMidnight < _options.FlushInterval ? untilMidnight : _options.FlushInterval;
                await _wake.WaitAsync(delay, _stop.Token).ConfigureAwait(false);
            }
            await CommitAsync(pending).ConfigureAwait(false);
            await WriteMarksAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { SetWarning(); }
        catch { SetWarning(); }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var item)) item.Completion?.TrySetResult();
        }
    }

    /// <summary>单日容量按库中 payload_bytes 汇总控制；超过上限的事件丢弃并标记损坏。</summary>
    private bool Accept(PendingRow row)
    {
        try
        {
            if (!_dayPayload.TryGetValue(row.Date, out var used))
            {
                using var connection = _data.Database.Connect();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COALESCE(SUM(payload_bytes), 0) FROM diagnostic_events WHERE date = $date;";
                command.Parameters.AddWithValue("$date", DateText(row.Date));
                used = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                _dayPayload[row.Date] = used;
            }
            if (used + row.PayloadBytes > _options.MaxDailyBytes)
            {
                Damage(row.Date);
                return false;
            }
            _dayPayload[row.Date] = used + row.PayloadBytes;
            return true;
        }
        catch
        {
            Damage(row.Date);
            return false;
        }
    }

    private async Task CommitAsync(List<PendingRow> pending)
    {
        if (pending.Count == 0) return;
        var rows = pending.ToArray();
        pending.Clear();
        try
        {
            await WriteAsync(connection => InsertRows(connection, rows)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
        catch
        {
            // 批次整体失败：丢弃并标记损坏；已计入的日容量回退，下次按库中实际字节重新汇总。
            foreach (var date in rows.Select(row => row.Date).Distinct())
            {
                _dayPayload.Remove(date);
                Damage(date);
            }
        }
    }

    private Task WriteAsync(Action<SqliteConnection> action) =>
        _options.WriteForTest is { } custom ? custom(action) : _data.Writer.ExecuteAsync(action, _stop.Token);

    private static void InsertRows(SqliteConnection connection, IReadOnlyList<PendingRow> rows)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO diagnostic_events
                (session_id, request_id, date, sequence, kind, entry_json, request_json, payload_bytes, created_at_ms)
            VALUES ($session, $request, $date, $sequence, $kind, $entry, $requestJson, $payload, $created);
            """;
        var session = command.Parameters.Add("$session", SqliteType.Text);
        var request = command.Parameters.Add("$request", SqliteType.Text);
        var date = command.Parameters.Add("$date", SqliteType.Text);
        var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var entry = command.Parameters.Add("$entry", SqliteType.Text);
        var requestJson = command.Parameters.Add("$requestJson", SqliteType.Text);
        var payload = command.Parameters.Add("$payload", SqliteType.Integer);
        var created = command.Parameters.Add("$created", SqliteType.Integer);
        foreach (var row in rows)
        {
            session.Value = row.Session;
            request.Value = row.RequestId;
            date.Value = DateText(row.Date);
            sequence.Value = row.Sequence;
            kind.Value = row.Kind;
            entry.Value = row.EntryJson;
            requestJson.Value = (object?)row.RequestJson ?? DBNull.Value;
            payload.Value = row.PayloadBytes;
            created.Value = row.CreatedAtMs;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>把"该会话该天记录不完整"落库（幂等）；重启后的重放据此继续标记。</summary>
    private async Task WriteMarksAsync()
    {
        foreach (var date in _damagedDates.Keys)
        {
            if (!Retained(date) || _markedDates.Contains(date)) continue;
            var text = DateText(date);
            try
            {
                await WriteAsync(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO diagnostic_marks (session_id, date) VALUES ($session, $date) ON CONFLICT(session_id, date) DO NOTHING;";
                    command.Parameters.AddWithValue("$session", _session);
                    command.Parameters.AddWithValue("$date", text);
                    command.ExecuteNonQuery();
                }).ConfigureAwait(false);
                _markedDates.Add(date);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
            catch { SetWarning(); }
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            if (_queryGate.Wait(0))
            {
                try { if (_cache is not null && !Retained(_cache.Date)) _cache = null; }
                finally { _queryGate.Release(); }
            }
            foreach (var date in _damagedDates.Keys.Where(date => !Retained(date)).ToArray()) _damagedDates.TryRemove(date, out _);
            _markedDates.RemoveWhere(date => !Retained(date));
            foreach (var date in _dayPayload.Keys.Where(date => !Retained(date)).ToArray()) _dayPayload.Remove(date);
            lock (_liveGate)
            {
                foreach (var key in _live.Where(pair => !Retained(pair.Value.Request.Date)).Select(pair => pair.Key).ToArray()) _live.Remove(key);
                // 防止已清理日期的排队键在进程运行期间无限累积。
                var keys = _liveOrder.Where(_live.ContainsKey).ToArray();
                _liveOrder.Clear();
                foreach (var key in keys) _liveOrder.Enqueue(key);
            }
            var cutoff = DateText(Today.AddDays(-6));
            await WriteAsync(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM diagnostic_events WHERE date < $cutoff; DELETE FROM diagnostic_marks WHERE date < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", cutoff);
                command.ExecuteNonQuery();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { SetWarning(); }
        catch { SetWarning(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        Wake();
        try
        {
            // 剩余批次落库与标记仍由后台执行；调用者最多等两秒。
            if (!_writer.Wait(TimeSpan.FromSeconds(2))) { SetWarning(); _stop.Cancel(); }
        }
        catch { SetWarning(); }
    }

    private sealed record WriteItem(PendingRow? Row, DateOnly Date, TaskCompletionSource? Completion);

    private sealed record PendingRow(string Session, string RequestId, DateOnly Date, long Sequence, string Kind,
        string EntryJson, string? RequestJson, long PayloadBytes, long CreatedAtMs);

    private sealed class RequestHandle(DiagnosticStore owner, DiagnosticRequestInfo request) : IDiagnosticRequest
    {
        private readonly object _gate = new();
        internal DiagnosticRequestInfo Request { get; } = request;
        internal SummaryState State { get; } = new(request, owner._session);
        internal DiagnosticTarget? LastSendTarget;
        private long _sequence;

        public void Record(DiagnosticEntry entry)
        {
            try
            {
                lock (_gate)
                {
                    if (State.Finished || Volatile.Read(ref owner._disposed) != 0) return;
                    entry = DiagnosticSafety.Sanitize(entry);
                    var value = new DiagnosticEvent(owner._session, Request.RequestId, Request.Date,
                        ++_sequence, Request, entry);
                    State.Apply(value);
                    if (entry.Kind == DiagnosticEventKind.SendStarted) LastSendTarget = entry.Target;
                    owner.Enqueue(this, value);
                }
            }
            catch
            {
                lock (_gate) State.Incomplete = true;
                owner.Damage(Request.Date);
            }
        }

        internal LiveSnapshot Snapshot()
        {
            lock (_gate) return new LiveSnapshot(State.Snapshot(previous: false), _sequence, LastSendTarget);
        }
    }

    private sealed record LiveSnapshot(DiagnosticSummary Summary, long Sequence, DiagnosticTarget? LastSendTarget);
}
