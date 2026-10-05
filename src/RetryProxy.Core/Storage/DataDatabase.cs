using System;
using System.IO;

namespace RetryProxy.Core.Storage;

/// <summary>
/// logs\data.db（每日统计 + 请求诊断）的共享入口：每个日志目录一个实例，惰性打开。
/// 读取用 <see cref="Database"/>；写入用 <see cref="Writer"/>（每库一个写线程）。
/// 打开失败不缓存：下次访问会重试，调用方把异常映射成各自的警告文案。
/// 例：<c>var data = new DataDatabase(logDirectory); using var c = data.Database.Connect();</c>
/// </summary>
public sealed class DataDatabase : IDisposable
{
    private readonly object _lock = new();
    private SqliteDatabase? _database;
    private SqliteWriteQueue? _writer;

    public DataDatabase(string logDirectory)
    {
        LogDirectory = System.IO.Path.GetFullPath(logDirectory);
        Path = System.IO.Path.Combine(LogDirectory, "data.db");
    }

    /// <summary>本库所在的日志目录（旧运行日志与它同级，一次性导入时要用）。</summary>
    public string LogDirectory { get; }

    public string Path { get; }

    public SqliteDatabase Database
    {
        get
        {
            lock (_lock)
            {
                return _database ??= SqliteDatabase.Open(Path, DatabaseSchemas.DataVersion, DatabaseSchemas.EnsureData);
            }
        }
    }

    public SqliteWriteQueue Writer
    {
        get
        {
            lock (_lock)
            {
                return _writer ??= Database.CreateWriteQueue();
            }
        }
    }

    /// <summary>停止写队列并释放文件句柄；之后仍可继续使用（会重新打开）。</summary>
    public void Dispose()
    {
        SqliteWriteQueue? writer;
        SqliteDatabase? database;
        lock (_lock)
        {
            writer = _writer;
            database = _database;
            _writer = null;
            _database = null;
        }

        writer?.Dispose();
        database?.Dispose();
    }
}
