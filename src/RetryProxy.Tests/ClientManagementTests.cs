using System;
using System.IO;
using System.Linq;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Client;
using RetryProxy.Core.Config;
using RetryProxy.Core.Service;
using Xunit;

namespace RetryProxy.Tests;

public sealed class ClientManagementTests
{
    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void SaveClientTakeover_CommitsOnceAndClonesInput(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        var otherClient = client == ClientType.Claude ? ClientType.Codex : ClientType.Claude;
        var other = fixture.State(otherClient).Clone();
        var state = new ClientTakeoverState
        {
            Enabled = true, ConfigPath = fixture.PathFor(client), BackupPath = Path.Combine(fixture.Root, "redacted-backup"),
            LastWrittenHash = "redacted-hash",
        };
        Assert.Null(fixture.Workspace.SaveClientTakeover(client, state));
        Assert.Equal(1, fixture.SaveCalls);
        Assert.Equal(state, fixture.State(client));
        Assert.NotSame(state, fixture.State(client));
        Assert.Equal(other, fixture.State(otherClient));
        state.Enabled = false;
        state.LastWrittenHash = "modified-caller";
        Assert.True(fixture.State(client).Enabled);
        Assert.Equal("redacted-hash", fixture.State(client).LastWrittenHash);
        Assert.Equal(0, fixture.TotalWrites);
    }

    [Fact]
    public void SaveClientTakeover_PersistFailureLeavesMemoryUnchanged()
    {
        using var fixture = new ClientLifecycleFixture();
        var before = fixture.Workspace.Config.Clone();
        fixture.FailSave = true;
        Assert.NotNull(fixture.Workspace.SaveClientTakeover(ClientType.Claude, new ClientTakeoverState { Enabled = true }));
        Assert.Equal(before, fixture.Workspace.Config);
        Assert.Equal(1, fixture.SaveCalls);
        Assert.Null(fixture.Saved);
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void ImportExternal_CommitsProviderKeyAndRouteTogetherInOneSave(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        var profile = External(client);
        var before = fixture.Workspace.Config.Clone();
        var initialProviders = before.Providers.Count;
        var initialSelectedClient = fixture.Workspace.SelectedClient;
        fixture.OnSave = candidate =>
        {
            // 保存回调执行时，整个候选对象引用完整，而旧内存仍未发布。
            Assert.Equal(before, fixture.Workspace.Config);
            Assert.Equal(initialProviders + 1, candidate.Providers.Count);
            var route = candidate.RouteFor(client)!;
            var provider = candidate.ProviderById(route.CurrentProviderId)!;
            Assert.Equal("导入测试", provider.Name);
            Assert.Equal(profile.ApiKey, provider.KeyById(route.CurrentKeyId)!.ApiKey);
            candidate.SelectedRouteId = "callback-mutation-must-not-leak";
        };
        Assert.Null(fixture.Workspace.ImportClientProvider(profile, "导入测试"));
        Assert.Equal(1, fixture.SaveCalls);
        Assert.Equal(initialProviders + 1, fixture.Workspace.Config.Providers.Count);
        var imported = fixture.Provider(client);
        Assert.Equal("导入测试", imported.Name);
        Assert.Equal(profile.BaseUrl, imported.BaseUrl);
        Assert.Equal(profile.ApiKey, fixture.Key(client).ApiKey);
        Assert.Equal(profile.Models, imported.Models);
        Assert.Equal(initialSelectedClient, fixture.Workspace.SelectedClient);
        Assert.NotEqual("callback-mutation-must-not-leak", fixture.Workspace.Config.SelectedRouteId);
        Assert.Equal(0, fixture.TotalWrites);
        Assert.All(fixture.Workspace.Services.Values, service => Assert.Equal(ServiceState.Stopped, service.State));
        profile.Models.Model = "modified-profile";
        Assert.Equal("redacted-import-model", imported.Models.Model);
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void ImportExternal_SaveFailureDoesNotChangeMemorySelectionOrClientFiles(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.FailSave = true;
        var before = fixture.Workspace.Config.Clone();
        var selected = (fixture.Workspace.SelectedClient, fixture.Workspace.SelectedProvider, fixture.Workspace.SelectedRoute);
        var files = Enum.GetValues<ClientType>().ToDictionary(type => type, type => File.ReadAllBytes(fixture.PathFor(type)));
        Assert.NotNull(fixture.Workspace.ImportClientProvider(External(client), "导入失败测试"));
        Assert.Equal(before, fixture.Workspace.Config);
        Assert.Equal(selected, (fixture.Workspace.SelectedClient, fixture.Workspace.SelectedProvider, fixture.Workspace.SelectedRoute));
        Assert.Equal(1, fixture.SaveCalls);
        Assert.Null(fixture.Saved);
        Assert.Equal(0, fixture.TotalWrites);
        foreach (var type in Enum.GetValues<ClientType>()) Assert.Equal(files[type], File.ReadAllBytes(fixture.PathFor(type)));
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void ImportOldChannel_AddsFirstKeyToExistingProviderAndSelectsIt(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        var provider = fixture.Provider(client);
        provider.Keys.Clear();
        provider.Models = new ProviderModels();
        fixture.Route(client).CurrentKeyId = string.Empty;
        var previousId = provider.Id;
        var previousUrl = provider.BaseUrl;
        var previousCount = fixture.Workspace.Config.Providers.Count;
        var profile = new ClientProfile
        {
            ClientType = client, BaseUrl = $"http://localhost:{fixture.Route(client).ListenPort}",
            ApiKey = "redacted-legacy-key", AuthMode = client == ClientType.Claude ? ClaudeAuthMode.ApiKey : ClaudeAuthMode.Bearer,
            Models = new ProviderModels { Model = "redacted-import-model" },
        };
        fixture.OnSave = candidate =>
        {
            var route = candidate.RouteFor(client)!;
            Assert.Equal(previousId, route.CurrentProviderId);
            var candidateProvider = candidate.ProviderById(previousId)!;
            Assert.Equal(route.CurrentKeyId, Assert.Single(candidateProvider.Keys).Id);
        };
        Assert.Null(fixture.Workspace.ImportClientProvider(profile, "旧通道不新建供应商"));
        Assert.Equal(1, fixture.SaveCalls);
        Assert.Equal(previousCount, fixture.Workspace.Config.Providers.Count);
        Assert.Equal(previousId, fixture.Provider(client).Id);
        Assert.Equal(previousUrl, fixture.Provider(client).BaseUrl);
        Assert.Equal("默认", fixture.Key(client).Name);
        Assert.Equal(profile.ApiKey, fixture.Key(client).ApiKey);
        Assert.Equal(profile.AuthMode, fixture.Provider(client).AuthMode);
        Assert.Equal(profile.Models, fixture.Provider(client).Models);
        Assert.NotSame(profile.Models, fixture.Provider(client).Models);
    }

    [Fact]
    public void ImportOldChannel_AddsUniqueDefaultKeyNameAndKeepsExistingModels()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        var old = fixture.Provider(client).Clone();
        var profile = new ClientProfile
        {
            ClientType = client, BaseUrl = $"http://127.0.0.1:{fixture.Route(client).ListenPort}",
            ApiKey = "redacted-third-key", Models = new ProviderModels { Model = "ignored-import-model" },
        };
        Assert.Null(fixture.Workspace.ImportClientProvider(profile, "不使用此名称"));
        Assert.Equal(1, fixture.SaveCalls);
        Assert.Equal("默认 2", fixture.Key(client).Name);
        Assert.Equal(old.Keys.Count + 1, fixture.Provider(client).Keys.Count);
        Assert.Equal(old.Models, fixture.Provider(client).Models);
        foreach (var key in old.Keys) Assert.Equal(key, fixture.Provider(client).KeyById(key.Id));
    }

    [Fact]
    public void ImportOldChannel_SaveFailureDoesNotAppendKeyOrChangeSelection()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        var before = fixture.Workspace.Config.Clone();
        fixture.FailSave = true;
        var profile = new ClientProfile
        {
            ClientType = client, BaseUrl = $"http://localhost:{fixture.Route(client).ListenPort}",
            ApiKey = "redacted-new-key", AuthMode = ClaudeAuthMode.ApiKey,
        };
        Assert.NotNull(fixture.Workspace.ImportClientProvider(profile, "test"));
        Assert.Equal(before, fixture.Workspace.Config);
        Assert.Equal(1, fixture.SaveCalls);
        Assert.Equal(0, fixture.TotalWrites);
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void ImportOldChannel_ExistingKeyDoesNotCreateDuplicateOrSaveAgain(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        var profile = new ClientProfile
        {
            ClientType = client, BaseUrl = $"http://localhost:{fixture.Route(client).ListenPort}", ApiKey = fixture.Key(client).ApiKey,
        };
        var before = fixture.Workspace.Config.Clone();
        Assert.NotNull(fixture.Workspace.ImportClientProvider(profile, "test"));
        Assert.Equal(0, fixture.SaveCalls);
        Assert.Equal(before, fixture.Workspace.Config);
    }

    [Theory]
    [InlineData(ClientType.Claude, false)]
    [InlineData(ClientType.Claude, true)]
    [InlineData(ClientType.Codex, false)]
    [InlineData(ClientType.Codex, true)]
    public void Import_LocalTokenIsNeverSavedAsRealKey(ClientType client, bool externalAddress)
    {
        using var fixture = new ClientLifecycleFixture();
        var before = fixture.Workspace.Config.Clone();
        var profile = new ClientProfile
        {
            ClientType = client,
            BaseUrl = externalAddress ? "https://supplier.invalid" : $"http://localhost:{fixture.Route(client).ListenPort}",
            ApiKey = fixture.Route(client).LocalToken,
        };
        Assert.Contains("本地口令", fixture.Workspace.ImportClientProvider(profile, "test"));
        Assert.Equal(0, fixture.SaveCalls);
        Assert.Equal(before, fixture.Workspace.Config);
    }

    [Theory]
    [InlineData("https://supplier.invalid", "")]
    [InlineData("https://supplier.invalid", "redacted key")]
    [InlineData("http://localhost:29999", "redacted-key")]
    [InlineData("http://[::1]:28081", "redacted-key")]
    [InlineData("", "")]
    public void Import_UnsupportedSourceDoesNotChangeConfiguration(string url, string key)
    {
        using var fixture = new ClientLifecycleFixture();
        var before = fixture.Workspace.Config.Clone();
        var profile = new ClientProfile { ClientType = ClientType.Claude, BaseUrl = url, ApiKey = key };
        Assert.NotNull(fixture.Workspace.ImportClientProvider(profile, "test"));
        Assert.Equal(0, fixture.SaveCalls);
        Assert.Equal(before, fixture.Workspace.Config);
    }

    [Fact]
    public void Import_DuplicateProviderNameFailsValidationBeforeSave()
    {
        using var fixture = new ClientLifecycleFixture();
        var before = fixture.Workspace.Config.Clone();
        Assert.NotNull(fixture.Workspace.ImportClientProvider(External(ClientType.Claude), fixture.Provider(ClientType.Claude).Name));
        Assert.Equal(0, fixture.SaveCalls);
        Assert.Equal(before, fixture.Workspace.Config);
    }

    [Fact]
    public void Import_PersistenceErrorDoesNotExposeRawKeyOrPath()
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.OnSave = _ => throw new IOException("redacted-secret-key in C:/redacted-private-path/config.json");
        var error = fixture.Workspace.ImportClientProvider(External(ClientType.Claude), "test");
        Assert.NotNull(error);
        Assert.DoesNotContain("redacted-secret-key", error);
        Assert.DoesNotContain("redacted-private-path", error);
    }

    private static ClientProfile External(ClientType client) => new()
    {
        ClientType = client, BaseUrl = "https://imported-supplier.invalid", ApiKey = "redacted-import-key",
        AuthMode = ClaudeAuthMode.Bearer, Models = new ProviderModels { Model = "redacted-import-model" },
    };
}
