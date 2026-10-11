using System;
using System.IO;

namespace RetryProxy.Core.Storage;

/// <summary>统计按月、诊断按日保存。首次访问迁移旧 data.db，迁移完成前不允许新写入。</summary>
public sealed class DataDatabase : IDisposable
{
    private static readonly object MigrationGate = new();
    private readonly PartitionedDatabase _statistics;
    private readonly PartitionedDatabase _diagnostics;
    private volatile bool _migrated;

    public DataDatabase(string logDirectory)
    {
        LogDirectory = System.IO.Path.GetFullPath(logDirectory);
        LegacyPath = System.IO.Path.Combine(LogDirectory, "data.db");
        _statistics = new PartitionedDatabase(System.IO.Path.Combine(LogDirectory, "statistics"), true, 1, DatabaseSchemas.EnsureStatistics);
        _diagnostics = new PartitionedDatabase(System.IO.Path.Combine(LogDirectory, "diagnostics"), false, DatabaseSchemas.DataVersion, DatabaseSchemas.EnsureDiagnostics);
    }

    public string LogDirectory { get; }
    public string LegacyPath { get; }
    public PartitionedDatabase Statistics { get { EnsureMigrated(); return _statistics; } }
    public PartitionedDatabase Diagnostics { get { EnsureMigrated(); return _diagnostics; } }

    public void EnsureMigrated()
    {
        if (_migrated) return;
        lock (MigrationGate)
        {
            if (_migrated) return;
            if (File.Exists(LegacyPath)) LegacyDataMigration.Run(LegacyPath, _statistics, _diagnostics);
            _migrated = true;
        }
    }

    public void Dispose()
    {
        _statistics.Dispose();
        _diagnostics.Dispose();
    }
}
