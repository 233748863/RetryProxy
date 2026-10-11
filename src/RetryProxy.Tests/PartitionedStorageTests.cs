using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Storage;
using Xunit;

namespace RetryProxy.Tests;

public sealed class PartitionedStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retry-proxy-partition-tests", Guid.NewGuid().ToString("N"));
    private DataDatabase Data() => new(_root);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root) && Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "retry-proxy-partition-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(_root, true);
    }

    private static void Sql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static long Count(PartitionedDatabase files, DateOnly date, string table)
    {
        using var lease = files.OpenRead(date);
        if (lease is null) return 0;
        using var command = lease.Connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }
    private static void Statistic(SqliteConnection connection, string date, string id)
        => Sql(connection, $"INSERT INTO daily_requests VALUES ('route','{date}','{id}',1,1,'success',0,'key','账号','{{}}');");

    [Fact]
    public void StatisticsSpanMonthsAndMissingDatesDoNotCreateFiles()
    {
        using var data = Data();
        var first = new DateOnly(2026, 10, 31);
        var second = first.AddDays(1);
        data.Statistics.Execute(first, c => Statistic(c, "2026-10-31", "one"));
        data.Statistics.Execute(second, c => Statistic(c, "2026-11-01", "two"));
        var history = DailyHistory.ReadRange(data, "route", first, 3);
        Assert.Equal(new ulong[] { 1, 1, 0 }, history.Select(day => day.TotalRequests));
        Assert.Equal(2, data.Statistics.Dates().Count);
        Assert.False(File.Exists(data.LegacyPath));
        Assert.Null(data.Diagnostics.OpenRead(first));
        Assert.False(Directory.Exists(data.Diagnostics.DirectoryPath));
    }

    [Fact]
    public async Task CleanupWaitsForReadsAndOnlyDeletesExactExpiredDateFiles()
    {
        using var data = Data();
        var old = new DateOnly(2026, 10, 1);
        data.Diagnostics.Execute(old, _ => { });
        data.Diagnostics.Execute(old.AddDays(1), _ => { });
        File.WriteAllText(Path.Combine(data.Diagnostics.DirectoryPath, "notes.db"), "keep");
        using var lease = data.Diagnostics.OpenRead(old);
        var deleting = data.Diagnostics.DeleteBeforeAsync(old.AddDays(1));
        Assert.False(deleting.IsCompleted);
        lease!.Dispose();
        Assert.Equal(1, await deleting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(data.Diagnostics.GetPath(old)));
        Assert.True(File.Exists(data.Diagnostics.GetPath(old.AddDays(1))));
        Assert.True(File.Exists(Path.Combine(data.Diagnostics.DirectoryPath, "notes.db")));
    }

    [Fact]
    public async Task CancellationAfterEnqueueDoesNotReleaseAFileStillBeingWritten()
    {
        using var data = Data();
        var date = new DateOnly(2026, 10, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var writing = data.Diagnostics.ExecuteAsync(date, _ => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(5)); }, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var deleting = data.Diagnostics.DeleteBeforeAsync(date.AddDays(1));
        Assert.False(writing.IsCompleted);
        Assert.False(deleting.IsCompleted);
        release.Set();
        await writing;
        Assert.Equal(1, await deleting);
    }

    [Fact]
    public void FailedWriteReopensTheConnectionForTheNextBatch()
    {
        using var data = Data();
        var date = new DateOnly(2026, 10, 1);
        Assert.Throws<IOException>(() => data.Statistics.Execute(date, connection =>
        {
            connection.Close();
            throw new IOException("connection no longer available");
        }));
        data.Statistics.Execute(date, connection => Statistic(connection, "2026-10-01", "recovered"));
        Assert.Equal(1, Count(data.Statistics, date, "daily_requests"));
    }

    private void SeedLegacy()
    {
        var path = Path.Combine(_root, "data.db");
        var database = SqliteDatabase.Open(path, DatabaseSchemas.DataVersion, DatabaseSchemas.EnsureData);
        using (var connection = database.Connect())
        {
            Statistic(connection, "2026-10-31", "one");
            Statistic(connection, "2026-11-01", "two");
            Sql(connection, """
                INSERT INTO daily_imports VALUES ('route','2026-10-31');
                INSERT INTO diagnostic_events VALUES (42,'session','request','2026-10-31',1,'started','{"unchanged":"中文"}',NULL,20,1);
                INSERT INTO diagnostic_events VALUES (43,'session','request','2026-11-01',2,'finished','{}',NULL,2,2);
                INSERT INTO diagnostic_marks VALUES ('session','2026-10-31');
                INSERT INTO diagnostic_request_marks VALUES ('session','request','2026-11-01');
                """);
        }
        database.Dispose();
    }

    [Fact]
    public void MigrationPreservesAllTablesAndArchivesCompleteSource()
    {
        SeedLegacy();
        using var data = Data();
        data.EnsureMigrated();
        Assert.False(File.Exists(data.LegacyPath));
        Assert.True(File.Exists(data.LegacyPath + ".migrated.bak"));
        Assert.Equal(1, Count(data.Statistics, new(2026, 10, 31), "daily_requests"));
        Assert.Equal(1, Count(data.Statistics, new(2026, 11, 1), "daily_requests"));
        Assert.Equal(1, Count(data.Statistics, new(2026, 10, 31), "daily_imports"));
        Assert.Equal(1, Count(data.Diagnostics, new(2026, 10, 31), "diagnostic_marks"));
        Assert.Equal(1, Count(data.Diagnostics, new(2026, 11, 1), "diagnostic_request_marks"));
        using var lease = data.Diagnostics.OpenRead(new(2026, 10, 31));
        using var command = lease!.Connection.CreateCommand();
        command.CommandText = "SELECT entry_json FROM diagnostic_events WHERE id=42;";
        Assert.Equal("{\"unchanged\":\"中文\"}", command.ExecuteScalar());
        using var source = new SqliteConnection($"Data Source={data.LegacyPath}.migrated.bak;Mode=ReadOnly;Pooling=False");
        source.Open();
        using var check = source.CreateCommand(); check.CommandText = "SELECT COUNT(*) FROM diagnostic_events;";
        Assert.Equal(2L, check.ExecuteScalar());
    }

    [Fact]
    public void InterruptedMigrationRetriesWithoutDuplicatingPreviouslyCopiedRows()
    {
        SeedLegacy();
        var blocked = Path.Combine(_root, "diagnostics");
        File.WriteAllText(blocked, "occupied");
        using (var data = Data()) Assert.Throws<IOException>(() => data.EnsureMigrated());
        Assert.True(File.Exists(Path.Combine(_root, "data.db")));
        Assert.False(File.Exists(Path.Combine(_root, "data.db.migrated.bak")));
        File.Delete(blocked);
        using var retried = Data();
        retried.EnsureMigrated();
        Assert.Equal(1, Count(retried.Statistics, new(2026, 10, 31), "daily_requests"));
        Assert.Equal(1, Count(retried.Diagnostics, new(2026, 10, 31), "diagnostic_events"));
        Assert.Equal(1, Count(retried.Diagnostics, new(2026, 11, 1), "diagnostic_events"));
    }

    [Fact]
    public void InvalidLegacyDatesStopMigrationWithoutArchivingOrDiscardingSource()
    {
        SeedLegacy();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "data.db")};Pooling=False"))
        {
            connection.Open(); Sql(connection, "UPDATE daily_requests SET date='invalid' WHERE request_id='one';");
        }
        using var data = Data();
        Assert.Throws<InvalidDataException>(() => data.EnsureMigrated());
        Assert.True(File.Exists(data.LegacyPath));
        Assert.False(File.Exists(data.LegacyPath + ".migrated.bak"));
    }
}
