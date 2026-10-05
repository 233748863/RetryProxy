using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using RetryProxy.Core.Config;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed partial class ConfigServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InjectionDoesNotCreateOrModifyDatabaseOrLegacyFile(bool existingDatabase)
    {
        var original = Rows(Sample());
        if (existingDatabase) Seed(original);
        File.WriteAllText(LegacyPath, "注入模式不应解析或导入这个文件");
        var legacyBytes = File.ReadAllBytes(LegacyPath);
        var service = Open(new Dictionary<string, string> { [ProxyConfigLoader.TestConfigEnv] = original["proxy"] },
            registryReader: () => throw new InvalidOperationException("不应读取真实注册表"));

        Assert.Single(service.Get().Proxy!.Providers);
        service.Get().CommonConfig.RunForVersion = "注入修改";
        service.Save();
        service.SaveChecked();
        service.BackupBeforeDeletion();
        service.BackupBeforeClientTakeover();

        Assert.Equal(existingDatabase, File.Exists(DbPath));
        if (existingDatabase) AssertRows(original, ReadRows());
        Assert.Equal(legacyBytes, File.ReadAllBytes(LegacyPath));
        Assert.False(Directory.Exists(BackupDirectory));
        Assert.Empty(_notifications);
    }

    [Fact]
    public void InjectionLeavesExistingZeroByteDatabaseUninitialized()
    {
        File.WriteAllBytes(DbPath, Array.Empty<byte>());
        File.SetLastWriteTimeUtc(DbPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var modified = File.GetLastWriteTimeUtc(DbPath);
        var service = Open(new Dictionary<string, string>
        {
            [ProxyConfigLoader.TestConfigEnv] = Rows(Sample())["proxy"],
        });

        Assert.Single(service.Get().Proxy!.Providers);
        service.Get().CommonConfig.RunForVersion = "注入修改";
        service.Save();
        service.SaveChecked();
        service.BackupBeforeDeletion();
        service.BackupBeforeClientTakeover();

        using var connection = Connect(readOnly: true);
        var tables = Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('meta', 'config');");
        var length = new FileInfo(DbPath).Length;
        Assert.True(length == 0 && tables == 0,
            $"注入读取不得初始化既有空库：实际文件长度 {length} 字节，配置表数量 {tables}");
        Assert.Equal(modified, File.GetLastWriteTimeUtc(DbPath));
        Assert.False(Directory.Exists(BackupDirectory));
        Assert.Empty(_notifications);
    }

    [Fact]
    public void InjectionReadsExistingPreferencesWithoutUpgradingSchema()
    {
        var original = Rows(Sample());
        Seed(original);
        using (var connection = Connect())
            Execute(connection, "UPDATE meta SET value='0' WHERE key='schema_version';");
        var bytes = ReadSharedBytes(DbPath);
        var modified = File.GetLastWriteTimeUtc(DbPath);
        var service = Open(new Dictionary<string, string>
        {
            [ProxyConfigLoader.TestConfigEnv] = original["proxy"],
        });

        Assert.Equal("before", service.Get().CommonConfig.RunForVersion);
        Assert.Single(service.Get().Proxy!.Providers);
        service.SaveChecked();

        using var check = Connect(readOnly: true);
        Assert.Equal(0, Scalar(check, "SELECT value FROM meta WHERE key='schema_version';"));
        AssertRows(original, ReadRows());
        Assert.Equal(bytes, ReadSharedBytes(DbPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(DbPath));
        Assert.False(Directory.Exists(BackupDirectory));
        Assert.Empty(_notifications);
    }

    [Fact]
    public void InjectionKeepsDeleteJournalModeUnchanged()
    {
        var original = Rows(Sample());
        Seed(original);
        using (var connection = Connect())
            Execute(connection, "PRAGMA journal_mode=DELETE;");
        var bytes = ReadSharedBytes(DbPath);
        var modified = File.GetLastWriteTimeUtc(DbPath);
        var service = Open(new Dictionary<string, string>
        {
            [ProxyConfigLoader.TestConfigEnv] = original["proxy"],
        });

        Assert.Equal("before", service.Get().CommonConfig.RunForVersion);
        Assert.Single(service.Get().Proxy!.Providers);
        service.Get().CommonConfig.RunForVersion = "注入修改";
        service.Save();
        service.SaveChecked();

        using var check = Connect(readOnly: true);
        using var command = check.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("delete", command.ExecuteScalar());
        AssertRows(original, ReadRows());
        Assert.Equal(bytes, ReadSharedBytes(DbPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(DbPath));
        Assert.False(File.Exists(DbPath + "-wal"));
        Assert.False(File.Exists(DbPath + "-shm"));
        Assert.False(Directory.Exists(BackupDirectory));
        Assert.Empty(_notifications);
    }

    [Fact]
    public void RegistryImportsIntoDatabaseOnlyOnce()
    {
        var proxy = Rows(Sample())["proxy"];
        var reads = 0;
        var service = Open(registryReader: () => { reads++; return proxy; });
        Assert.Single(service.Get().Proxy!.Providers);
        Assert.Equal(4, ReadRows().Count);
        service.Dispose();
        Assert.Single(Open(registryReader: () => { reads++; return null; }).Get().Proxy!.Providers);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void SaveCheckedRollsBackAllFourRowsAndDoesNotExposeSqliteException()
    {
        var original = Rows(Sample());
        Seed(original);
        using var connection = Connect();
        // 第三行失败：前两行已执行的更新也必须回滚；SQLite 原始错误故意带敏感占位值。
        Execute(connection, $"""
            CREATE TRIGGER fail_other BEFORE UPDATE ON config WHEN NEW.key='other'
            BEGIN SELECT RAISE(ABORT, '{Secret}'); END;
            """);
        var service = Open();
        var config = service.Get();
        config.Proxy!.Providers[0].Keys[0].ApiKey = "sk-changed";
        config.CommonConfig.RunForVersion = "changed";
        config.OtherConfig.UiCultureInfoName = "en";
        config.Preparations![0].Model = "changed";

        var error = Assert.Throws<ConfigException>(service.SaveChecked);
        service.Save();

        AssertRows(original, ReadRows());
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(Secret, ReadLog());
        Assert.All(_notifications, message => Assert.DoesNotContain(Secret, message));
        Assert.NotEmpty(_notifications);
        Execute(connection, "DROP TRIGGER fail_other;");
        service.SaveChecked();
        AssertRows(Rows(config), ReadRows());
    }

    [Fact]
    public void InvalidProxyValidationDoesNotExposeUserValuesInErrors()
    {
        var config = Sample();
        config.Proxy!.Providers[0].Name = Secret;
        config.Proxy.Providers[0].Keys[1].Name = config.Proxy.Providers[0].Keys[0].Name;
        var rows = Rows(config);
        Seed(rows);
        AssertBlocked(Open());
        AssertRows(rows, ReadRows());
        Assert.All(_notifications, message => Assert.DoesNotContain(Secret, message));
        Assert.DoesNotContain(Secret, ReadLog());
    }
}
