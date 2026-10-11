using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace RetryProxy.Core.Storage;

/// <summary>按日或月保存的数据库。读、写、清理共用租约，只缓存最近写入的一个库。</summary>
public sealed class PartitionedDatabase : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _monthly;
    private readonly int _version;
    private readonly Action<SqliteConnection, int> _schema;
    private SqliteDatabase? _database;
    private SqliteWriteQueue? _writer;

    public PartitionedDatabase(string directory, bool monthly, int version, Action<SqliteConnection, int> schema)
    {
        DirectoryPath = Path.GetFullPath(directory);
        _monthly = monthly;
        _version = version;
        _schema = schema;
    }

    public string DirectoryPath { get; }
    public string GetPath(DateOnly date) => Path.Combine(DirectoryPath, date.ToString(_monthly ? "yyyy-MM" : "yyyy-MM-dd", CultureInfo.InvariantCulture) + ".db");

    public IReadOnlyList<DateOnly> Dates()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var result = new List<DateOnly>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.db", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var text = _monthly ? name + "-01" : name;
            if (name.Length == (_monthly ? 7 : 10) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                result.Add(date);
        }
        result.Sort();
        return result;
    }

    /// <summary>日期不存在时返回 null，不创建空库；租约持有期间清理与切库等待。</summary>
    public ReadLease? OpenRead(DateOnly date, CancellationToken token = default)
    {
        _gate.Wait(token);
        SqliteConnection? connection = null;
        try
        {
            var path = GetPath(date);
            if (!File.Exists(path)) { _gate.Release(); return null; }
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            if (!int.TryParse(command.ExecuteScalar() as string, out var version) || version != _version)
                throw new InvalidDataException("日志数据库版本不受支持：" + path);
            return new ReadLease(connection, _gate);
        }
        catch { connection?.Dispose(); _gate.Release(); throw; }
    }

    public void Execute(DateOnly date, Action<SqliteConnection> action) => ExecuteAsync(date, action).GetAwaiter().GetResult();

    public async Task ExecuteAsync(DateOnly date, Action<SqliteConnection> action, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var path = GetPath(date);
            if (_database?.Path != path)
            {
                CloseWriter();
                _database = SqliteDatabase.Open(path, _version, _schema);
            }
            _writer ??= _database.CreateWriteQueue();
            // 入队后等真实写入结束再释放租约，取消等待不能使清理越过仍在写的动作。
            try { await _writer.ExecuteAsync(action).ConfigureAwait(false); }
            catch
            {
                // 连接可能已失效；释放当前连接，下一批重新打开，避免之后的写入持续失败。
                CloseWriter();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<int> DeleteBeforeAsync(DateOnly cutoff, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var count = 0;
            foreach (var date in Dates().Where(date => date < cutoff))
            {
                token.ThrowIfCancellationRequested();
                var path = GetPath(date);
                if (_database?.Path == path) CloseWriter();
                Checkpoint(path);
                // 别的进程仍在使用时，独占句柄会拒绝删除；所有旁路页已完整写回主库。
                using var guard = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                File.Delete(path + "-wal");
                File.Delete(path + "-shm");
                File.Delete(path);
                count++;
            }
            return count;
        }
        finally { _gate.Release(); }
    }

    internal static void Checkpoint(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        if (reader.Read() && reader.GetInt64(0) != 0) throw new IOException("数据库仍在使用，稍后重试清理");
    }

    private void CloseWriter()
    {
        _writer?.Dispose();
        _database?.Dispose();
        _writer = null;
        _database = null;
    }

    public void Dispose()
    {
        _gate.Wait();
        try { CloseWriter(); }
        finally { _gate.Release(); }
    }

    public sealed class ReadLease : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _disposed;
        internal ReadLease(SqliteConnection connection, SemaphoreSlim gate) { Connection = connection; _gate = gate; }
        public SqliteConnection Connection { get; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Connection.Dispose(); }
            finally { _gate.Release(); }
        }
    }
}
