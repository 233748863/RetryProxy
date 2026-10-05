using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Storage;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>配置库门面：读写 config 表、缺文件不建库、单事务覆盖与结构版本。</summary>
public sealed class ConfigDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-proxy-config-tests", Guid.NewGuid().ToString("N"));

    public ConfigDatabaseTests() => Directory.CreateDirectory(_directory);

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

    private ConfigDatabase Open() => new(_directory);

    private static Dictionary<string, string> Rows(params (string Key, string Value)[] items)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            rows[key] = value;
        }

        return rows;
    }

    [Fact]
    public void ReadRowsOnMissingFileReturnsEmptyWithoutCreatingFile()
    {
        using var database = Open();
        Assert.Empty(database.ReadRows());
        Assert.False(File.Exists(database.Path));
    }

    [Fact]
    public void WriteThenReadRoundTripsAllRows()
    {
        using var database = Open();
        var rows = Rows(
            ("proxy", @"{""schema_version"":7}"),
            ("common", @"{""currentThemeType"":1}"),
            ("other", "{}"),
            ("preparations", @"[{""id"":""任务一""}]"));
        database.WriteRows(rows);
        var stored = database.ReadRows();
        Assert.Equal(rows.Count, stored.Count);
        foreach (var row in rows)
        {
            Assert.Equal(row.Value, stored[row.Key]);
        }

        Assert.True(File.Exists(database.Path));
    }

    [Fact]
    public void WriteRowsOverwritesExistingKeysAndKeepsOthers()
    {
        using var database = Open();
        database.WriteRows(Rows(("proxy", "a"), ("common", "b")));
        database.WriteRows(Rows(("proxy", "c")));
        var stored = database.ReadRows();
        Assert.Equal(2, stored.Count);
        Assert.Equal("c", stored["proxy"]);
        Assert.Equal("b", stored["common"]);
    }

    [Fact]
    public void WriteRowsSetsUpdatedAtMilliseconds()
    {
        using var database = Open();
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        database.WriteRows(Rows(("proxy", "a")));
        using var connection = database.Database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT updated_at FROM config WHERE key = 'proxy';";
        var value = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        Assert.InRange(value, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void EmptyFileIsInitializedWithSchemaVersion()
    {
        File.WriteAllBytes(Path.Combine(_directory, "config.db"), Array.Empty<byte>());
        using var database = Open();
        Assert.Empty(database.ReadRows());
        Assert.Equal(DatabaseSchemas.ConfigVersion, database.Database.ReadSchemaVersion());
    }

    [Fact]
    public void CorruptFileThrowsInsteadOfSilentlyCreating()
    {
        File.WriteAllBytes(Path.Combine(_directory, "config.db"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
        using var database = Open();
        Assert.ThrowsAny<Exception>(() => database.ReadRows());
    }
}
