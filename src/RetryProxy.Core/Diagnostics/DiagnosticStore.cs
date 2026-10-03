using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

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
    internal Func<string, Stream>? OpenWriteForTest { get; init; }
    internal Action<long>? BytesReadForTest { get; init; }
}

/// <summary>独立、尽力而为的诊断旁路；采集线程不做文件操作或等待队列容量。</summary>
public sealed partial class DiagnosticStore : IDiagnosticRepository
{
    public const string IncompleteWarning = "诊断记录不完整，不影响请求转发";
    private const int ControlReserveBytes = 256;
    private const int MaxFilesPerDay = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _directory;
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
    private readonly ConcurrentDictionary<string, long> _publishedLengths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DateOnly, WriterDay> _writerDays = new();
    private readonly HashSet<DateOnly> _markedDates = [];
    private int _disposed;
    private int _warning;
    private int _notificationScheduled;

    public DiagnosticStore(string directory, TimeProvider? timeProvider = null, DiagnosticStoreOptions? options = null)
    {
        _directory = directory;
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
            OpenWriteForTest = supplied.OpenWriteForTest,
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
    private static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private string JournalPath(DateOnly date) => Path.Combine(_directory, $"{DateText(date)}-{_session}.jsonl");

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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length + 1 > _options.MaxEventBytes || !_queue.Writer.TryWrite(new WriteItem(bytes, value.Date, null)))
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
        var pending = 0;
        try
        {
            while (true)
            {
                if (cleanupDate != Today)
                {
                    Cleanup();
                    cleanupDate = Today;
                    NotifyChanged();
                }
                var read = 0;
                while (read++ < _options.FlushEventCount && _queue.Reader.TryRead(out var item))
                {
                    if (item.Completion is not null)
                    {
                        await WriteControlsAsync().ConfigureAwait(false);
                        await FlushStreamsAsync().ConfigureAwait(false);
                        pending = 0;
                        lastFlush = _time.GetTimestamp();
                        item.Completion.TrySetResult();
                    }
                    else if (Retained(item.Date))
                    {
                        await AppendAsync(item.Date, item.Bytes!, control: false).ConfigureAwait(false);
                        pending++;
                        if (pending >= _options.FlushEventCount)
                        {
                            await FlushStreamsAsync().ConfigureAwait(false);
                            pending = 0;
                            lastFlush = _time.GetTimestamp();
                        }
                    }
                }
                await WriteControlsAsync().ConfigureAwait(false);
                if (pending > 0 && (pending >= _options.FlushEventCount || _time.GetElapsedTime(lastFlush) >= _options.FlushInterval))
                {
                    await FlushStreamsAsync().ConfigureAwait(false);
                    pending = 0;
                    lastFlush = _time.GetTimestamp();
                }
                if (_queue.Reader.Completion.IsCompleted) break;
                if (_queue.Reader.TryPeek(out _)) continue;
                // 每秒刷盘，同时在下一本地自然日第一次唤醒时复核七天留存。
                var untilMidnight = TimeSpan.FromDays(1) - _time.GetLocalNow().TimeOfDay;
                var delay = untilMidnight < _options.FlushInterval ? untilMidnight : _options.FlushInterval;
                await _wake.WaitAsync(delay, _stop.Token).ConfigureAwait(false);
            }
            await WriteControlsAsync().ConfigureAwait(false);
            await FlushStreamsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { SetWarning(); }
        catch { SetWarning(); }
        finally
        {
            _queue.Writer.TryComplete();
            foreach (var day in _writerDays.Values) CloseStream(day);
            while (_queue.Reader.TryRead(out var item)) item.Completion?.TrySetResult();
        }
    }

    private async Task AppendAsync(DateOnly date, byte[] bytes, bool control)
    {
        try
        {
            if (!Retained(date)) return;
            var day = GetWriterDay(date);
            var reserve = control ? 0 : ControlReserveBytes;
            if (day.Bytes + bytes.Length + 1 + reserve > _options.MaxDailyBytes)
            {
                if (!control) Damage(date);
                return;
            }
            if (day.Stream is null)
            {
                Directory.CreateDirectory(_directory);
                day.Stream = _options.OpenWriteForTest?.Invoke(day.Path) ?? new FileStream(day.Path,
                    FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                    1, FileOptions.Asynchronous);
                // 一次失败写入可能留下半行；下一条控制/事件从新行开始，恢复端会标记残行。
                if (day.NeedsSeparator)
                {
                    if (day.Bytes + bytes.Length + 2 + reserve > _options.MaxDailyBytes) return;
                    await day.Stream.WriteAsync(new byte[] { (byte)'\n' }, _stop.Token).ConfigureAwait(false);
                    day.Bytes++;
                    day.Length++;
                    day.NeedsSeparator = false;
                }
            }
            var line = new byte[bytes.Length + 1];
            bytes.CopyTo(line, 0);
            line[^1] = (byte)'\n';
            await day.Stream.WriteAsync(line, _stop.Token).ConfigureAwait(false);
            day.Bytes += line.Length;
            day.Length += line.Length;
            _publishedLengths[day.Path] = day.Stream.CanSeek ? day.Stream.Length : day.Length;
            if (control) _markedDates.Add(date);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
        catch
        {
            if (_writerDays.TryGetValue(date, out var day))
            {
                CloseStream(day);
                day.NeedsSeparator = true;
                // 失败写入的实际字节同样计入每天上限，下次重开时重新统计。
                _writerDays.Remove(date);
            }
            Damage(date);
        }
    }

    private WriterDay GetWriterDay(DateOnly date)
    {
        if (_writerDays.TryGetValue(date, out var day)) return day;
        Directory.CreateDirectory(_directory);
        var bytes = 0L;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, $"{DateText(date)}-*.jsonl"))
        {
            if (++count > MaxFilesPerDay) throw new IOException();
            bytes = checked(bytes + new FileInfo(path).Length);
        }
        var ownPath = JournalPath(date);
        var length = File.Exists(ownPath) ? new FileInfo(ownPath).Length : 0;
        var published = _publishedLengths.TryGetValue(ownPath, out var previousLength) ? previousLength : 0;
        day = new WriterDay(ownPath, bytes, length) { NeedsSeparator = length > published };
        _writerDays.Add(date, day);
        return day;
    }

    private async Task WriteControlsAsync()
    {
        foreach (var date in _damagedDates.Keys)
        {
            if (!Retained(date) || _markedDates.Contains(date)) continue;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new ControlRecord(1, "incomplete", _session, date), JsonOptions);
            await AppendAsync(date, bytes, control: true).ConfigureAwait(false);
        }
    }

    private async Task FlushStreamsAsync()
    {
        foreach (var pair in _writerDays.ToArray())
        {
            try
            {
                if (pair.Value.Stream is not null) await pair.Value.Stream.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
            catch { CloseStream(pair.Value); Damage(pair.Key); }
        }
    }

    private void Cleanup()
    {
        try
        {
            if (_queryGate.Wait(0))
            {
                try { if (_cache is not null && !Retained(_cache.Date)) _cache = null; }
                finally { _queryGate.Release(); }
            }
            foreach (var date in _writerDays.Keys.Where(date => !Retained(date)).ToArray())
            {
                CloseStream(_writerDays[date]);
                _writerDays.Remove(date);
            }
            foreach (var date in _damagedDates.Keys.Where(date => !Retained(date))) _damagedDates.TryRemove(date, out _);
            _markedDates.RemoveWhere(date => !Retained(date));
            foreach (var path in _publishedLengths.Keys)
            {
                if (TryFileIdentity(path, out var date, out _) && !Retained(date)) _publishedLengths.TryRemove(path, out _);
            }
            lock (_liveGate)
            {
                foreach (var key in _live.Where(pair => !Retained(pair.Value.Request.Date)).Select(pair => pair.Key).ToArray()) _live.Remove(key);
                // 防止已清理日期的排队键在进程运行期间无限累积。
                var keys = _liveOrder.Where(_live.ContainsKey).ToArray();
                _liveOrder.Clear();
                foreach (var key in keys) _liveOrder.Enqueue(key);
            }
            if (!Directory.Exists(_directory)) return;
            foreach (var path in Directory.EnumerateFiles(_directory, "*.jsonl"))
            {
                if (!TryFileIdentity(path, out var date, out _) || Retained(date)) continue;
                try { File.Delete(path); _publishedLengths.TryRemove(path, out _); }
                catch { SetWarning(); }
            }
        }
        catch { SetWarning(); }
    }

    private static bool TryFileIdentity(string path, out DateOnly date, out string session)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        date = default;
        session = string.Empty;
        if (name.Length != 43 || name[10] != '-' ||
            !DateOnly.TryParseExact(name[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
            !Guid.TryParseExact(name[11..], "N", out _)) return false;
        session = name[11..];
        return true;
    }

    private void CloseStream(WriterDay day)
    {
        try { day.Stream?.Dispose(); }
        catch { SetWarning(); }
        day.Stream = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        Wake();
        try
        {
            // 所有文件关闭和清理仍由后台执行；调用者最多等两秒。
            if (!_writer.Wait(TimeSpan.FromSeconds(2))) { SetWarning(); _stop.Cancel(); }
        }
        catch { SetWarning(); }
    }

    private sealed record WriteItem(byte[]? Bytes, DateOnly Date, TaskCompletionSource? Completion);
    private sealed record ControlRecord(int Version, string Control, string SessionId, DateOnly Date);
    private sealed class WriterDay(string path, long bytes, long length)
    {
        internal readonly string Path = path;
        internal long Bytes = bytes;
        internal long Length = length;
        internal Stream? Stream;
        internal bool NeedsSeparator;
    }

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
