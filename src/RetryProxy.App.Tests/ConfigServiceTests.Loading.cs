using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using RetryProxy.Core.Config;
using RetryProxy.Core.Storage;
using RetryProxy.Service;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed partial class ConfigServiceTests
{
    [Fact]
    public void LegacyBackupFailureAbortsImportAndLeavesOriginalUnchanged()
    {
        File.WriteAllText(LegacyPath, LegacyJson(Sample()));
        var original = File.ReadAllBytes(LegacyPath);
        var modified = File.GetLastWriteTimeUtc(LegacyPath);
        BlockBackupDirectory();
        var registryReads = 0;
        var service = Open(registryReader: () => { registryReads++; return null; });

        Assert.Empty(service.Get().Proxy!.Providers);
        AssertBlocked(service);

        Assert.Equal(0, registryReads);
        Assert.False(File.Exists(DbPath));
        Assert.False(File.Exists(LegacyPath + ".migrated.bak"));
        Assert.Equal(original, File.ReadAllBytes(LegacyPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(LegacyPath));
        Assert.Contains(_notifications, message => message.Contains("已中止导入") && message.Contains("备份未完成"));
        Assert.DoesNotContain("原文件已备份", ReadLog());
    }

    [Fact]
    public void LegacyImportPreservesAllNodesAndRenamesOnlyAfterSaving()
    {
        var original = Sample();
        var json = LegacyJson(original);
        File.WriteAllText(LegacyPath, json);
        var service = Open();
        var loaded = service.Get();

        AssertRows(Rows(original), ReadRows());
        Assert.Equal(original.Proxy!.Routes.Select(route => route.Id), loaded.Proxy!.Routes.Select(route => route.Id));
        Assert.Equal(json, File.ReadAllText(Assert.Single(Backups())));
        Assert.Equal(json, File.ReadAllText(LegacyPath + ".migrated.bak"));
        Assert.False(File.Exists(LegacyPath));
        service.Dispose();
        AssertRows(Rows(loaded), Rows(Open().Get()));
        Assert.Single(Backups());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyFileWithoutProxyStillImports(bool fromRegistry)
    {
        var original = Sample();
        var registryJson = JsonSerializer.Serialize(original.Proxy, ConfigService.JsonOptions);
        original.Proxy = null;
        File.WriteAllText(LegacyPath, LegacyJson(original));
        var service = Open(registryReader: () => fromRegistry ? registryJson : null);

        var loaded = service.Get();
        service.SaveChecked();

        Assert.Equal("before", loaded.CommonConfig.RunForVersion);
        Assert.Equal(fromRegistry ? 1 : 0, loaded.Proxy!.Providers.Count);
        Assert.Equal(4, ReadRows().Count);
        Assert.False(File.Exists(LegacyPath));
    }

    [Fact]
    public void EmptyConfigTableStillImportsLegacyFile()
    {
        using (var database = new ConfigDatabase(UserDirectory)) _ = database.Database;
        var original = Sample();
        File.WriteAllText(LegacyPath, LegacyJson(original));
        Open().Get();
        AssertRows(Rows(original), ReadRows());
        Assert.Single(Backups());
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("common")]
    [InlineData("other")]
    [InlineData("preparations")]
    public void MissingRequiredRowBlocksEverySave(string key)
    {
        var rows = Rows(Sample());
        rows.Remove(key);
        Seed(rows);
        var service = Open(registryReader: () => throw new InvalidOperationException("不应回退注册表"));

        AssertBlocked(service);

        AssertRows(rows, ReadRows());
        AssertRows(rows, ReadRows(Assert.Single(Backups("config_*.db.bak"))));
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("common")]
    [InlineData("other")]
    [InlineData("preparations")]
    public void InvalidJsonInAnyRowBlocksEverySave(string key)
    {
        var rows = Rows(Sample());
        rows[key] = "{";
        Seed(rows);
        AssertBlocked(Open());
        AssertRows(rows, ReadRows());
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("common")]
    [InlineData("other")]
    public void NullRequiredObjectBlocksSaveAndDoesNotReadRegistry(string key)
    {
        var rows = Rows(Sample());
        rows[key] = "null";
        Seed(rows);
        var registryReads = 0;
        var service = Open(registryReader: () => { registryReads++; return Rows(Sample())["proxy"]; });
        AssertBlocked(service);
        Assert.Equal(0, registryReads);
        AssertRows(rows, ReadRows());
    }

    [Fact]
    public void NullablePreparationListRemainsCompatible()
    {
        var rows = Rows(Sample());
        rows["preparations"] = "null";
        Seed(rows);
        var service = Open();
        Assert.Null(service.Get().Preparations);
        service.SaveChecked();
        AssertRows(rows, ReadRows());
        Assert.Empty(_notifications);
    }

    [Fact]
    public void ReadOnlyDatabaseNeverOverwritesSavedConfiguration()
    {
        var rows = Rows(Sample());
        Seed(rows);
        var original = ReadSharedBytes(DbPath);
        File.SetAttributes(DbPath, FileAttributes.ReadOnly);
        AssertBlocked(Open());
        AssertRows(rows, ReadRows());
        Assert.Equal(original, ReadSharedBytes(DbPath));
    }
}
