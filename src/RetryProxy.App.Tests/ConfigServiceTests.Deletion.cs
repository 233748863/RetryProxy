using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using RetryProxy.Core.Config;
using RetryProxy.Service;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed partial class ConfigServiceTests
{
    [Fact]
    public void DeletingKeyExportsAllNodesBeforeChangingMemoryOrDatabase()
    {
        Seed(Rows(Sample()));
        var service = Open();
        var original = LegacyJson(service.Get());
        using var workspace = Workspace(service);

        Assert.Null(workspace.DeleteKey("provider-1", "key-2"));

        var backup = Assert.Single(Backups());
        Assert.Equal(original, File.ReadAllText(backup));
        var exported = JsonSerializer.Deserialize<AllConfig>(File.ReadAllText(backup), ConfigService.JsonOptions)!;
        Assert.Equal(Secret, exported.Proxy!.ProviderById("provider-1")!.KeyById("key-2")!.ApiKey);
        Assert.Equal("sk-preparation-secret", Assert.Single(exported.Preparations!).ApiKey);
        Assert.Null(workspace.Config.ProviderById("provider-1")!.KeyById("key-2"));
        Assert.DoesNotContain(Secret, ReadRows()["proxy"]);
    }

    [Fact]
    public void ExportFailureAbortsDeletionAndLeavesKeyInMemoryAndDatabase()
    {
        Seed(Rows(Sample()));
        var service = Open();
        var original = Rows(service.Get());
        using var workspace = Workspace(service);
        BlockBackupDirectory();

        Assert.Contains("未删除", workspace.DeleteKey("provider-1", "key-2"));

        Assert.Equal(Secret, workspace.Config.ProviderById("provider-1")!.KeyById("key-2")!.ApiKey);
        AssertRows(original, ReadRows());
        AssertRows(original, Rows(service.Get()));
        Assert.Throws<ConfigException>(service.BackupBeforeClientTakeover);
    }

    [Fact]
    public void ExportRetainsLatestFiveCompleteBackups()
    {
        Seed(Rows(Sample()));
        var service = Open();
        var config = service.Get();
        for (var i = 0; i < 8; i++)
        {
            config.CommonConfig.RunForVersion = i.ToString();
            service.BackupBeforeDeletion();
        }
        var versions = Backups().Select(path => JsonSerializer.Deserialize<AllConfig>(
            File.ReadAllText(path), ConfigService.JsonOptions)!.CommonConfig.RunForVersion);
        Assert.Equal(new[] { "3", "4", "5", "6", "7" }, versions);
        Assert.Equal(5, Directory.GetFiles(BackupDirectory).Length);
    }
}
