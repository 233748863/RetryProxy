using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Storage;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>SQLite 基础设施：建库建表、结构版本、WAL、写队列串行化与失败传播。</summary>
public sealed class SqliteStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-proxy-storage-tests", Guid.NewGuid().ToString("N"));

    public SqliteStorageTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        // 连接池会保持文件句柄，先清池再删目录。
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string ConfigPath => Path.Combine(_directory, "config.db");

    private string DataPath => Path.Combine(_directory, "data.db");

    private SqliteDatabase OpenConfig() => SqliteDatabase.Open(ConfigPath, DatabaseSchemas.ConfigVersion, DatabaseSchemas.EnsureConfig);

    private SqliteDatabase OpenData() => SqliteDatabase.Open(DataPath, DatabaseSchemas.DataVersion, DatabaseSchemas.EnsureData);

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    [Fact]
    public void OpeningCreatesFileSchemaWalAndMeta()
    {
        var database = OpenConfig();
        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(DatabaseSchemas.ConfigVersion, database.ReadSchemaVersion());
        using var connection = database.Connect();
        Assert.Equal("wal", Text(connection, "PRAGMA journal_mode;"));
        Assert.Equal("1", Text(connection, "SELECT value FROM meta WHERE key = 'schema_version';"));
        Assert.NotNull(Text(connection, "SELECT value FROM meta WHERE key = 'created_at';"));
        Assert.Equal("config", Text(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'config';"));
    }

    [Fact]
    public void OpeningCreatesMissingDirectories()
    {
        var path = Path.Combine(_directory, "nested", "deeper", "data.db");
        var database = SqliteDatabase.Open(path, DatabaseSchemas.DataVersion, DatabaseSchemas.EnsureData);
        Assert.True(File.Exists(path));
        Assert.Equal(DatabaseSchemas.DataVersion, database.ReadSchemaVersion());
        using var connection = database.Connect();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'daily_requests';"));
    }

    [Fact]
    public async Task ReopeningKeepsDataAndVersion()
    {
        var first = OpenConfig();
        using (var queue = first.CreateWriteQueue())
        {
            await queue.ExecuteAsync(c => Exec(c, "INSERT INTO config (key, value, updated_at) VALUES ('proxy', '{\"schema_version\":7}', 1);"));
        }

        var second = OpenConfig();
        Assert.Equal(DatabaseSchemas.ConfigVersion, second.ReadSchemaVersion());
        using var connection = second.Connect();
        Assert.Equal("{\"schema_version\":7}", Text(connection, "SELECT value FROM config WHERE key = 'proxy';"));
    }

    [Fact]
    public void DiagnosticRequestMarkUpgradePreservesVersionTwoEventsAndLegacyMarks()
    {
        var database = OpenData();
        using (var connection = database.Connect())
        {
            // 精确还原 v2 的表集合与版本；已有事件、旧日期标记应原样保留。
            Exec(connection, """
                DROP TABLE diagnostic_request_marks;
                UPDATE meta SET value = '2' WHERE key = 'schema_version';
                INSERT INTO diagnostic_events (session_id, request_id, date, sequence, kind, entry_json, payload_bytes, created_at_ms)
                VALUES ('original-session', 'original-request', '2026-10-08', 1, 'started', '{"kind":"started"}', 18, 123);
                INSERT INTO diagnostic_marks (session_id, date) VALUES ('original-session', '2026-10-08');
                """);
        }
        database.Dispose();

        var upgraded = OpenData();
        Assert.Equal(3, upgraded.ReadSchemaVersion());
        using (var connection = upgraded.Connect())
        {
            Assert.Equal("{\"kind\":\"started\"}", Text(connection, "SELECT entry_json FROM diagnostic_events;"));
            Assert.Equal("original-session", Text(connection, "SELECT session_id FROM diagnostic_marks;"));
            Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM diagnostic_request_marks;"));
            Exec(connection, "INSERT INTO diagnostic_request_marks (session_id, request_id, date) VALUES ('new-session', 'affected-request', '2026-10-08');");
        }
        upgraded.Dispose();
        var reopened = OpenData();
        using var check = reopened.Connect();
        Assert.Equal(1, Scalar(check, "SELECT COUNT(*) FROM diagnostic_events;"));
        Assert.Equal(1, Scalar(check, "SELECT COUNT(*) FROM diagnostic_marks;"));
        Assert.Equal(1, Scalar(check, "SELECT COUNT(*) FROM diagnostic_request_marks;"));
    }

    [Fact]
    public void NewerSchemaVersionIsRejected()
    {
        var database = OpenConfig();
        using (var connection = database.Connect())
        {
            Exec(connection, "UPDATE meta SET value = '99' WHERE key = 'schema_version';");
        }

        var error = Assert.Throws<InvalidDataException>(() => OpenConfig());
        Assert.Contains("99", error.Message);
    }

    [Fact]
    public void ForeignDatabaseWithoutMetaIsTreatedAsNew()
    {
        // 存在但不是本程序建的库文件：meta 缺失按版本 0 处理，照常建表。
        using (var connection = new SqliteConnection($"Data Source={ConfigPath}"))
        {
            connection.Open();
            Exec(connection, "CREATE TABLE foreign_table (id INTEGER);");
        }

        var database = OpenConfig();
        Assert.Equal(DatabaseSchemas.ConfigVersion, database.ReadSchemaVersion());
        using var check = database.Connect();
        Assert.Equal(1L, Scalar(check, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'config';"));
    }

    [Fact]
    public async Task WriteQueueSerializesActions()
    {
        var database = OpenConfig();
        using var queue = database.CreateWriteQueue();
        var counter = 0;
        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(queue.ExecuteAsync(_ =>
            {
                var value = counter;
                Thread.Sleep(1);
                counter = value + 1;
            }));
        }

        await Task.WhenAll(tasks);
        Assert.Equal(50, counter);
    }

    [Fact]
    public async Task WriteQueuePropagatesFailureAndKeepsRunning()
    {
        var database = OpenConfig();
        using var queue = database.CreateWriteQueue();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.ExecuteAsync(_ => throw new InvalidOperationException("模拟写失败")));
        Assert.Equal("模拟写失败", failure.Message);

        await queue.ExecuteAsync(c => Exec(c, "INSERT INTO config (key, value, updated_at) VALUES ('other', '{}', 1);"));
        using var connection = database.Connect();
        Assert.Equal("{}", Text(connection, "SELECT value FROM config WHERE key = 'other';"));
    }

    [Fact]
    public async Task FlushWaitsForQueuedWrites()
    {
        var database = OpenConfig();
        using var queue = database.CreateWriteQueue();
        var written = false;
        _ = queue.ExecuteAsync(c =>
        {
            Exec(c, "INSERT INTO config (key, value, updated_at) VALUES ('common', '{}', 1);");
            written = true;
        });
        await queue.FlushAsync();
        Assert.True(written);
        using var connection = database.Connect();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM config;"));
    }

    [Fact]
    public async Task ConcurrentCallersAllLand()
    {
        var database = OpenConfig();
        using var queue = database.CreateWriteQueue();
        var tasks = new List<Task>();
        for (var worker = 0; worker < 4; worker++)
        {
            var id = worker;
            tasks.Add(Task.Run(async () =>
            {
                for (var i = 0; i < 25; i++)
                {
                    var key = $"k-{id}-{i}";
                    await queue.ExecuteAsync(c =>
                    {
                        using var command = c.CreateCommand();
                        command.CommandText = "INSERT INTO config (key, value, updated_at) VALUES ($key, '{}', 1);";
                        command.Parameters.AddWithValue("$key", key);
                        command.ExecuteNonQuery();
                    });
                }
            }));
        }

        await Task.WhenAll(tasks);
        using var connection = database.Connect();
        Assert.Equal(100L, Scalar(connection, "SELECT COUNT(*) FROM config;"));
    }

    [Fact]
    public async Task ChineseTextRoundTrips()
    {
        var database = OpenConfig();
        using var queue = database.CreateWriteQueue();
        const string value = "供应商·中文与 emoji 😀 \"引号\"";
        await queue.ExecuteAsync(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "INSERT INTO config (key, value, updated_at) VALUES ('preparations', $value, 1);";
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        });

        using var connection = database.Connect();
        Assert.Equal(value, Text(connection, "SELECT value FROM config WHERE key = 'preparations';"));
    }

    [Fact]
    public async Task DailyRequestsUpsertIsIdempotent()
    {
        var database = OpenData();
        using var queue = database.CreateWriteQueue();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var outcome = attempt == 0 ? "进行中" : "成功";
            await queue.ExecuteAsync(c =>
            {
                using var command = c.CreateCommand();
                command.CommandText = """
                    INSERT INTO daily_requests (route_id, date, request_id, updated_at_ms, sequence, outcome, retry_count, key_id, key_name, record_json)
                    VALUES ('route-1', '2026-10-04', 'req-1', $updated, $sequence, $outcome, 0, 'key-1', '账号', '{}')
                    ON CONFLICT(route_id, date, request_id) DO UPDATE SET
                        updated_at_ms = excluded.updated_at_ms,
                        sequence = excluded.sequence,
                        outcome = excluded.outcome,
                        record_json = excluded.record_json;
                    """;
                command.Parameters.AddWithValue("$updated", 100 + attempt);
                command.Parameters.AddWithValue("$sequence", attempt + 1);
                command.Parameters.AddWithValue("$outcome", outcome);
                command.ExecuteNonQuery();
            });
        }

        using var connection = database.Connect();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM daily_requests;"));
        Assert.Equal("成功", Text(connection, "SELECT outcome FROM daily_requests WHERE route_id = 'route-1' AND date = '2026-10-04' AND request_id = 'req-1';"));
    }

    [Fact]
    public async Task DiagnosticEventsEnforceUniqueSequence()
    {
        var database = OpenData();
        using var queue = database.CreateWriteQueue();
        await queue.ExecuteAsync(c => InsertDiagnostic(c, 1));
        await queue.ExecuteAsync(c => InsertDiagnostic(c, 2));

        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => queue.ExecuteAsync(c => InsertDiagnostic(c, 1)));
        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        using var connection = database.Connect();
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM diagnostic_events;"));
    }

    private static void InsertDiagnostic(SqliteConnection connection, long sequence)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO diagnostic_events (session_id, request_id, date, sequence, kind, entry_json, payload_bytes, created_at_ms)
            VALUES ('session-1', 'req-1', '2026-10-04', $sequence, 'send_started', '{}', 10, 1);
            """;
        command.Parameters.AddWithValue("$sequence", sequence);
        command.ExecuteNonQuery();
    }
}
