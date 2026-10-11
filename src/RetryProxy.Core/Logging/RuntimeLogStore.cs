using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Storage;

namespace RetryProxy.Core.Logging;

public sealed record RuntimeLogEntry(long Id, DateOnly Date, long TimestampMs, string Level, LogSource Source,
    string RouteName, string? RequestId, DateOnly? RequestDate, string Line);

public sealed class RuntimeLogOptions
{
    public int QueueCapacity { get; init; } = 4096;
    public int BatchSize { get; init; } = 256;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);
    public bool DeferCleanup { get; init; }
    internal Func<Task>? BeforeWriteForTest { get; init; }
    internal Func<DateOnly, Action<SqliteConnection>, Task>? WriteForTest { get; init; }
}

/// <summary>运行日志的按日存储：调用线程仅入队，后台批量保存；数据库失败转到有容量上限的应急文本。</summary>
public sealed partial class RuntimeLogStore : IDisposable
{
    public const int DefaultRetentionDays = 7;
    public const int MaxRetentionDays = 3650;
    private readonly TimeProvider _time;
    private readonly RuntimeLogOptions _options;
    private readonly Channel<WorkItem> _queue;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly PartitionedDatabase _files;
    private readonly Task _worker;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly EmergencyLog _emergency;
    private long _sequence;
    private long _dropped;
    private long _fallbackLines;
    private long _unreportedDropped;
    private int _retentionDays = DefaultRetentionDays;
    private int _cleanupEnabled;
    private int _cleanupRequested = 1;
    private int _disposed;
    private string? _warning;
    private string? _migrationWarning;
    private string? _cleanupWarning;
    private DateOnly _cleanedDate;

    public RuntimeLogStore(string directory, TimeProvider? time = null, RuntimeLogOptions? options = null)
    {
        LogDirectory = Path.GetFullPath(directory);
        _time = time ?? TimeProvider.System;
        _options = options ?? new RuntimeLogOptions();
        if (_options.QueueCapacity < 1 || _options.BatchSize < 1 || _options.FlushInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        _files = new PartitionedDatabase(Path.Combine(LogDirectory, "runtime"), false, 1, EnsureSchema);
        _emergency = new EmergencyLog(Path.Combine(LogDirectory, "runtime-emergency.log"));
        try { Directory.CreateDirectory(LogDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StorageFailed("运行日志目录暂时不可写", error);
        }
        _cleanupEnabled = _options.DeferCleanup ? 0 : 1;
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(WriteLoopAsync);
    }

    public string LogDirectory { get; }
    public DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
    public string CurrentPath => _files.GetPath(Today);
    public string? Warning => Volatile.Read(ref _warning) ?? Volatile.Read(ref _migrationWarning) ?? Volatile.Read(ref _cleanupWarning)
        ?? (Interlocked.Read(ref _dropped) is > 0 and var count ? $"运行日志队列已满，本次运行有 {count} 条日志未保存" : null)
        ?? (Interlocked.Read(ref _fallbackLines) is > 0 and var fallback ? $"本次运行有 {fallback} 条日志未写入数据库，已尝试保存到应急文本" : null);
    public string? CleanupWarning => Volatile.Read(ref _cleanupWarning);
    internal DateTimeOffset Now => _time.GetLocalNow();

    public static int NormalizeRetentionDays(int value)
        => value is >= 1 and <= MaxRetentionDays ? value : DefaultRetentionDays;

    public int RetentionDays
    {
        get => Volatile.Read(ref _retentionDays);
        set
        {
            if (value < 1 || value > MaxRetentionDays) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _retentionDays, value);
            Volatile.Write(ref _cleanupEnabled, 1);
            Interlocked.Exchange(ref _cleanupRequested, 1);
            Wake();
        }
    }

    public bool Append(RuntimeLogEntry entry)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        var item = new WorkItem(new PendingEntry(_session, Interlocked.Increment(ref _sequence), entry), null);
        var accepted = _queue.Writer.TryWrite(item);
        if (!accepted)
        {
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _unreportedDropped);
        }
        Wake();
        return accepted;
    }

    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    public async Task FlushAsync(CancellationToken token = default)
    {
        if (Volatile.Read(ref _disposed) != 0) { await _worker.WaitAsync(token).ConfigureAwait(false); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(new WorkItem(null, completion), token).ConfigureAwait(false);
            Wake();
            await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (ChannelClosedException) { await _worker.WaitAsync(token).ConfigureAwait(false); }
    }

    private async Task WriteLoopAsync()
    {
        var pending = new List<PendingEntry>(_options.BatchSize);
        var lastFlush = _time.GetTimestamp();
        try
        {
            try { await ImportLegacyAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                const string message = "旧运行日志迁移失败，原文件已保留";
                Volatile.Write(ref _migrationWarning, message);
                _emergency.Write($"{Now:yyyy-MM-dd HH:mm:ss} ERROR [系统] {message}：{error.GetType().Name}");
            }
            while (true)
            {
                await CleanupAsync().ConfigureAwait(false);
                while (pending.Count < _options.BatchSize && _queue.Reader.TryRead(out var item))
                {
                    if (item.Completion is not null)
                    {
                        await CommitAsync(pending).ConfigureAwait(false);
                        lastFlush = _time.GetTimestamp();
                        await CleanupAsync().ConfigureAwait(false);
                        item.Completion.TrySetResult();
                    }
                    else if (item.Entry is not null) pending.Add(item.Entry);
                }
                var lost = Interlocked.Exchange(ref _unreportedDropped, 0);
                if (lost > 0) _emergency.Write($"{Now:yyyy-MM-dd HH:mm:ss} WARNING [系统] 运行日志队列已满，跳过 {lost} 条日志");
                if (_queue.Reader.Completion.IsCompleted)
                {
                    await CommitAsync(pending).ConfigureAwait(false);
                    break;
                }
                if (pending.Count >= _options.BatchSize || _time.GetElapsedTime(lastFlush) >= _options.FlushInterval)
                {
                    await CommitAsync(pending).ConfigureAwait(false);
                    lastFlush = _time.GetTimestamp();
                    continue;
                }
                await _wake.WaitAsync(_options.FlushInterval).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            StorageFailed("运行日志保存已停止", error);
            foreach (var entry in pending) _emergency.Write(WithFullRequestId(entry.Entry.Line, entry.Entry.RequestId));
            _queue.Writer.TryComplete(error);
            while (_queue.Reader.TryRead(out var item))
            {
                if (item.Entry is not null) _emergency.Write(WithFullRequestId(item.Entry.Entry.Line, item.Entry.Entry.RequestId));
                item.Completion?.TrySetException(error);
            }
        }
        finally { _files.Dispose(); }
    }

    private async Task CommitAsync(List<PendingEntry> pending)
    {
        if (pending.Count == 0) return;
        var groups = pending.GroupBy(value => value.Entry.Date).Select(group => (Date: group.Key, Rows: group.ToArray())).ToArray();
        pending.Clear();
        foreach (var group in groups)
        {
            if (Volatile.Read(ref _cleanupEnabled) != 0 && group.Date < Cutoff) continue;
            try
            {
                await WriteBatchAsync(group.Date, group.Rows).ConfigureAwait(false);
                Volatile.Write(ref _warning, null);
            }
            catch (Exception error)
            {
                Interlocked.Add(ref _fallbackLines, group.Rows.Length);
                StorageFailed("运行日志暂时无法写入数据库，已尝试保存到应急文本", error);
                foreach (var row in group.Rows) _emergency.Write(WithFullRequestId(row.Entry.Line, row.Entry.RequestId));
            }
        }
    }

    private DateOnly Cutoff => DateOnly.FromDayNumber(Math.Max(0, Today.DayNumber - RetentionDays + 1));

    private async Task CleanupAsync()
    {
        if (Volatile.Read(ref _cleanupEnabled) == 0) return;
        var requested = Interlocked.Exchange(ref _cleanupRequested, 0) != 0;
        if (_cleanedDate == Today && !requested) return;
        try
        {
            await _files.DeleteBeforeAsync(Cutoff).ConfigureAwait(false);
            Volatile.Write(ref _cleanupWarning, null);
        }
        catch (Exception error)
        {
            Volatile.Write(ref _cleanupWarning, "过期运行日志清理失败，请稍后重试");
            _emergency.Write($"{Now:yyyy-MM-dd HH:mm:ss} WARNING [系统] 过期运行日志清理失败：{error.GetType().Name}");
        }
        _cleanedDate = Today;
    }

    public async Task CleanupNowAsync(CancellationToken token = default)
    {
        Interlocked.Exchange(ref _cleanupRequested, 1);
        Wake();
        await FlushAsync(token).ConfigureAwait(false);
    }

    private void StorageFailed(string message, Exception error)
    {
        Volatile.Write(ref _warning, message);
        _emergency.Write($"{Now:yyyy-MM-dd HH:mm:ss} ERROR [系统] {message}：{error.GetType().Name}");
    }

    private async Task WriteBatchAsync(DateOnly date, IReadOnlyList<PendingEntry> rows)
    {
        void Write(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO runtime_logs (session_id, sequence, timestamp_ms, level, source, route_name, request_id, request_date, line)
                VALUES ($session, $sequence, $time, $level, $source, $route, $request, $date, $line)
                ON CONFLICT(session_id, sequence) DO NOTHING;
                """;
            foreach (var name in new[] { "$session", "$sequence", "$time", "$level", "$source", "$route", "$request", "$date", "$line" })
                command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
            foreach (var row in rows)
            {
                command.Parameters[0].Value = row.Session;
                command.Parameters[1].Value = row.Sequence;
                command.Parameters[2].Value = row.Entry.TimestampMs;
                command.Parameters[3].Value = row.Entry.Level;
                command.Parameters[4].Value = (int)row.Entry.Source;
                command.Parameters[5].Value = row.Entry.RouteName;
                command.Parameters[6].Value = (object?)row.Entry.RequestId ?? DBNull.Value;
                command.Parameters[7].Value = (object?)row.Entry.RequestDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value;
                command.Parameters[8].Value = row.Entry.Line;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        if (_options.BeforeWriteForTest is { } before) await before().ConfigureAwait(false);
        await (_options.WriteForTest is { } custom ? custom(date, Write) : _files.ExecuteAsync(date, Write)).ConfigureAwait(false);
    }

    private static void EnsureSchema(SqliteConnection connection, int fromVersion)
    {
        if (fromVersion >= 1) return;
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS runtime_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                timestamp_ms INTEGER NOT NULL,
                level TEXT NOT NULL,
                source INTEGER NOT NULL,
                route_name TEXT NOT NULL,
                request_id TEXT,
                request_date TEXT,
                line TEXT NOT NULL,
                UNIQUE(session_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS ix_runtime_logs_source_level ON runtime_logs (source, level, id);
            CREATE INDEX IF NOT EXISTS ix_runtime_logs_request ON runtime_logs (request_id, id);
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        Wake();
        if (!_worker.Wait(TimeSpan.FromSeconds(5)))
            _emergency.Write($"{Now:yyyy-MM-dd HH:mm:ss} WARNING [系统] 运行日志仍在后台保存，退出等待已超时");
    }

    private sealed record PendingEntry(string Session, long Sequence, RuntimeLogEntry Entry);
    private sealed record WorkItem(PendingEntry? Entry, TaskCompletionSource? Completion);
}

internal sealed class EmergencyLog(string path)
{
    private readonly object _gate = new();
    internal const long MaxBytes = 5 * 1024 * 1024;
    internal void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaxBytes)
                {
                    for (var index = 3; index >= 1; index--)
                    {
                        var source = index == 1 ? path : path + "." + (index - 1);
                        if (File.Exists(source)) File.Move(source, path + "." + index, overwrite: true);
                    }
                }
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                file.Write(bytes.AsSpan(0, (int)Math.Min(bytes.Length, MaxBytes)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
