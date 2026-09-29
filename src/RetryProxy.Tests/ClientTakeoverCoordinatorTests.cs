using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Client;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

public sealed class ClientTakeoverCoordinatorTests
{
    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void CancelTakeover_IsPersistentAcrossRefreshAndRestart(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.StartTakenOver(client);
        Assert.Null(fixture.Coordinator.CancelTakeover(client));
        Assert.False(fixture.State(client).Enabled);
        Assert.Equal(fixture.Snapshot(client).ApiKey, fixture.Profile(client).ApiKey);
        var writes = fixture.Editor(client).Writes;
        fixture.SetState(client, ServiceState.Stopped);
        fixture.Coordinator.Refresh();
        fixture.SetState(client, ServiceState.Starting);
        fixture.Coordinator.Refresh();
        fixture.SetState(client, ServiceState.Running);
        fixture.Coordinator.Refresh();
        Assert.Equal(writes, fixture.Editor(client).Writes);
        Assert.False(fixture.State(client).Enabled);
        Assert.Equal(ClientConnectionStatus.Direct, fixture.Coordinator.Connection(client).Status);
        Assert.False(fixture.Saved!.ClientTakeover[client].Enabled);
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void StopRoute_RestoresDirectButKeepsPersistentTakeover(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.StartTakenOver(client);
        var backup = fixture.State(client).BackupPath;
        Assert.Null(fixture.Coordinator.StopRoute(fixture.Route(client).Id));
        Assert.Equal(ServiceState.Stopping, fixture.Service(client).State);
        Assert.True(fixture.State(client).Enabled);
        Assert.Equal(fixture.Snapshot(client).ApiKey, fixture.Profile(client).ApiKey);
        Assert.Equal(backup, fixture.State(client).BackupPath);
        fixture.SetState(client, ServiceState.Stopped);
        fixture.Coordinator.Refresh();
        fixture.SetState(client, ServiceState.Starting);
        fixture.Coordinator.Refresh();
        Assert.Equal(fixture.Snapshot(client).ApiKey, fixture.Profile(client).ApiKey);
        fixture.SetState(client, ServiceState.Running);
        fixture.Coordinator.Refresh();
        Assert.Equal(fixture.Route(client).LocalToken, fixture.Profile(client).ApiKey);
        Assert.True(fixture.State(client).Enabled);
    }

    [Fact]
    public void Shutdown_RestoresBothClientsWithoutClearingEnabledAndIgnoresLaterRefresh()
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.StartTakenOver(ClientType.Claude);
        fixture.StartTakenOver(ClientType.Codex);
        fixture.Coordinator.Shutdown();
        foreach (var client in Enum.GetValues<ClientType>())
        {
            Assert.True(fixture.State(client).Enabled);
            Assert.Equal(fixture.Snapshot(client).ApiKey, fixture.Profile(client).ApiKey);
        }
        var writes = fixture.TotalWrites;
        fixture.Coordinator.Refresh();
        fixture.Coordinator.Detect();
        Assert.Equal(writes, fixture.TotalWrites);
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void Startup_OnlySuccessfulRunningStateWritesProxyConfiguration(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.MarkManaged(client);
        fixture.State(client).Enabled = true;
        fixture.SetState(client, ServiceState.Starting);
        fixture.Coordinator.Refresh();
        Assert.Equal(0, fixture.Editor(client).Writes);
        Assert.Equal(0, fixture.SaveCalls);
        Assert.NotNull(fixture.Coordinator.TakeOver(client));
        fixture.SetState(client, ServiceState.Running);
        fixture.Coordinator.Refresh();
        Assert.Equal(1, fixture.Editor(client).Writes);
        Assert.Equal(fixture.Route(client).LocalToken, fixture.Profile(client).ApiKey);
        Assert.Equal(ClientConnectionStatus.TakenOver, fixture.Coordinator.Connection(client).Status);
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void StartupFailure_RestoresStaleProxyFileAndKeepsEnabled(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.State(client).Enabled = true;
        fixture.SeedProxy(client);
        fixture.SetState(client, ServiceState.Starting);
        fixture.Coordinator.Refresh();
        Assert.Equal(0, fixture.Editor(client).Writes);
        fixture.SetState(client, ServiceState.Error);
        fixture.Coordinator.Refresh();
        Assert.Equal(fixture.Snapshot(client).ApiKey, fixture.Profile(client).ApiKey);
        Assert.True(fixture.State(client).Enabled);
        Assert.Equal(ClientConnectionStatus.Direct, fixture.Coordinator.Connection(client).Status);
    }

    [Fact]
    public void StartupFailure_RestoreSaveFailureKeepsOriginalFileAndReportsUnavailable()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Codex;
        fixture.State(client).Enabled = true;
        fixture.SeedProxy(client);
        fixture.SetState(client, ServiceState.Starting);
        fixture.Coordinator.Refresh();
        fixture.FailSave = true;
        var previous = fixture.Workspace.Config.Clone();
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        fixture.SetState(client, ServiceState.Error);
        fixture.Coordinator.Refresh();
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
        Assert.Equal(previous, fixture.Workspace.Config);
        Assert.Equal(ClientConnectionStatus.Unavailable, fixture.Coordinator.Connection(client).Status);
        Assert.NotNull(fixture.Workspace.Notice);
    }

    [Fact]
    public void UnchangedRefreshAndChangesHiddenByKeyOverrideDoNotSyncModels()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.Key(client).ModelOverride = new KeyModelOverride { Model = "redacted-override", Context1M = true };
        fixture.StartTakenOver(client);
        var writes = fixture.Editor(client).Writes;
        var reads = fixture.Editor(client).Reads;
        var saves = fixture.SaveCalls;
        fixture.Coordinator.Refresh();
        fixture.Provider(client).Models.Model = "redacted-provider-model-changed";
        fixture.Provider(client).Models.Context1M = true;
        fixture.Provider(client).Keys[1].ModelOverride = fixture.Key(client).ModelOverride!.Clone();
        fixture.Route(client).CurrentKeyId = fixture.Provider(client).Keys[1].Id;
        fixture.Coordinator.Refresh();
        Assert.Equal(writes, fixture.Editor(client).Writes);
        Assert.Equal(reads, fixture.Editor(client).Reads);
        Assert.Equal(saves, fixture.SaveCalls);
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void ChangedEffectiveModels_UsesModelsOnlyWithoutChangingUrlOrToken(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.StartTakenOver(client);
        var original = fixture.Profile(client);
        var writes = fixture.Editor(client).Writes;
        fixture.Key(client).ModelOverride = new KeyModelOverride { Model = "redacted-new-model", Context1M = true };
        fixture.Coordinator.Refresh();
        Assert.Equal(writes + 1, fixture.Editor(client).Writes);
        Assert.True(fixture.Editor(client).Requests.Last().ModelsOnly);
        var changed = fixture.Profile(client);
        Assert.Equal(original.BaseUrl, changed.BaseUrl);
        Assert.Equal(original.ApiKey, changed.ApiKey);
        Assert.Equal(original.AuthMode, changed.AuthMode);
        Assert.Equal("redacted-new-model", changed.Models.Model);
        Assert.True(changed.Models.Context1M);
        Assert.Equal("kept-user-value", fixture.Document(client)["unmanaged"]!.GetValue<string>());
    }

    [Fact]
    public void Detect_ExternalChangesAreReportedWithoutWritingOrReclaiming()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        fixture.ChangeDocument(client, document => document["url"] = "https://external-edit.invalid");
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        var writes = fixture.Editor(client).Writes;
        fixture.Coordinator.Detect();
        Assert.Equal(ClientConnectionStatus.Modified, fixture.Coordinator.Connection(client).Status);
        fixture.Coordinator.Refresh();
        Assert.Equal(writes, fixture.Editor(client).Writes);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
    }

    [Fact]
    public void Detect_InvalidExternalFileReportsUnavailableWithoutRewriting()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        const string invalid = "{redacted-unparseable-client-file";
        File.WriteAllText(fixture.PathFor(client), invalid);
        var writes = fixture.Editor(client).Writes;
        fixture.Coordinator.Detect();
        Assert.Equal(ClientConnectionStatus.Unavailable, fixture.Coordinator.Connection(client).Status);
        Assert.DoesNotContain("redacted-unparseable", fixture.Coordinator.Connection(client).Error);
        Assert.Equal(writes, fixture.Editor(client).Writes);
        Assert.Equal(invalid, File.ReadAllText(fixture.PathFor(client)));
    }

    [Fact]
    public void CodexConflictingSettings_AreNotDetectedAsTakenOverOrAutomaticallySynced()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Codex;
        fixture.StartTakenOver(client);
        fixture.ChangeDocument(client, document => document["conflicting"] = true);
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        var writes = fixture.Editor(client).Writes;
        fixture.Coordinator.Detect();
        Assert.Equal(ClientConnectionStatus.Modified, fixture.Coordinator.Connection(client).Status);
        fixture.Key(client).ModelOverride = new KeyModelOverride { Model = "redacted-new-model" };
        fixture.Coordinator.Refresh();
        Assert.Equal(writes, fixture.Editor(client).Writes);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
    }

    [Fact]
    public void RunningModelChange_DoesNotOverwriteExternallyChangedAddressOrCredentials()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        fixture.ChangeDocument(client, document =>
        {
            document["url"] = "https://external-edit.invalid";
            document["key"] = "redacted-external-key";
        });
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        var writes = fixture.Editor(client).Writes;
        fixture.Key(client).ModelOverride = new KeyModelOverride { Model = "redacted-new-model" };
        fixture.Coordinator.Refresh();
        Assert.Equal(writes, fixture.Editor(client).Writes);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
        Assert.Contains("改动", fixture.Workspace.Notice);
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void AutomaticStartup_DoesNotReclaimExternallyChangedManagedFile(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.MarkManaged(client);
        fixture.State(client).Enabled = true;
        fixture.ChangeDocument(client, document =>
        {
            document["url"] = "https://external-edit.invalid";
            document["key"] = "redacted-external-key";
        });
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        fixture.SetState(client, ServiceState.Starting);
        fixture.Coordinator.Refresh();
        fixture.SetState(client, ServiceState.Running);
        fixture.Coordinator.Refresh();
        Assert.Equal(0, fixture.Editor(client).Writes);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
        Assert.True(fixture.State(client).Enabled);
        Assert.Contains("改动", fixture.Workspace.Notice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticStartup_MergesUnmanagedEditsWithoutRejectingOwnedCredentials(bool observeStarting)
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.MarkManaged(client);
        fixture.State(client).Enabled = true;
        fixture.ChangeDocument(client, document => document["unmanaged"] = "external-unmanaged-value");
        if (observeStarting)
        {
            fixture.SetState(client, ServiceState.Starting);
            fixture.Coordinator.Refresh();
        }
        fixture.SetState(client, ServiceState.Running);
        fixture.Coordinator.Refresh();
        Assert.Equal(fixture.Route(client).LocalToken, fixture.Profile(client).ApiKey);
        Assert.Equal("external-unmanaged-value", fixture.Document(client)["unmanaged"]!.GetValue<string>());
        Assert.Equal(1, fixture.Editor(client).Writes);
    }

    [Theory]
    [InlineData(ServiceState.Stopped)]
    [InlineData(ServiceState.Error)]
    public void AutomaticStopOrFailure_DoesNotOverwriteExternalConfiguration(ServiceState state)
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        fixture.ChangeDocument(client, document =>
        {
            document["url"] = "https://external-edit.invalid";
            document["key"] = "redacted-external-key";
        });
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        var writes = fixture.Editor(client).Writes;
        fixture.SetState(client, state);
        fixture.Coordinator.Refresh();
        Assert.Equal(writes, fixture.Editor(client).Writes);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
        Assert.True(fixture.State(client).Enabled);
        Assert.Contains("改动", fixture.Workspace.Notice);
    }

    [Fact]
    public void Shutdown_DoesNotOverwriteExternalConfiguration()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        fixture.ChangeDocument(client, document =>
        {
            document["url"] = "https://external-edit.invalid";
            document["key"] = "redacted-external-key";
        });
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        var writes = fixture.Editor(client).Writes;
        fixture.Coordinator.Shutdown();
        Assert.Equal(writes, fixture.Editor(client).Writes);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
        Assert.True(fixture.State(client).Enabled);
    }

    [Fact]
    public void StopRoute_ExternalConfigurationIsKeptWhileServiceStillStops()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        fixture.ChangeDocument(client, document =>
        {
            document["url"] = "https://external-edit.invalid";
            document["key"] = "redacted-external-key";
        });
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        var writes = fixture.Editor(client).Writes;
        fixture.Coordinator.StopRoute(fixture.Route(client).Id);
        Assert.Equal(ServiceState.Stopping, fixture.Service(client).State);
        Assert.Equal(writes, fixture.Editor(client).Writes);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
        Assert.True(fixture.State(client).Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitUserAction_CanReplaceOwnedFieldsAfterExternalModification(bool cancel)
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        fixture.ChangeDocument(client, document =>
        {
            document["url"] = "https://external-edit.invalid";
            document["key"] = "redacted-external-key";
            document["unmanaged"] = "external-unmanaged-value";
        });
        var error = cancel ? fixture.Coordinator.CancelTakeover(client) : fixture.Coordinator.TakeOver(client);
        Assert.Null(error);
        Assert.Equal(cancel ? fixture.Key(client).ApiKey : fixture.Route(client).LocalToken, fixture.Profile(client).ApiKey);
        Assert.Equal(!cancel, fixture.State(client).Enabled);
        Assert.Equal("external-unmanaged-value", fixture.Document(client)["unmanaged"]!.GetValue<string>());
    }

    [Fact]
    public void StoppedKeyChange_SyncsOnlyPreviouslyManagedDirectConfiguration()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        fixture.SetState(client, ServiceState.Stopped);
        fixture.Coordinator.Refresh();
        var writes = fixture.Editor(client).Writes;
        fixture.Route(client).CurrentKeyId = fixture.Provider(client).Keys[1].Id;
        fixture.Coordinator.Refresh();
        Assert.Equal(writes + 1, fixture.Editor(client).Writes);
        Assert.Equal(fixture.Key(client).ApiKey, fixture.Profile(client).ApiKey);
        Assert.True(fixture.State(client).Enabled);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("key")]
    [InlineData("auth")]
    [InlineData("helper")]
    public void StoppedKeyChange_DoesNotOverwriteExternalEdits(string field)
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        fixture.SetState(client, ServiceState.Stopped);
        fixture.Coordinator.Refresh();
        fixture.ChangeDocument(client, document =>
        {
            if (field == "auth") document[field] = (int)ClaudeAuthMode.ApiKey;
            else if (field == "helper") document[field] = true;
            else document[field] = field == "url" ? "https://external-edit.invalid" : "redacted-external-key";
        });
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        var writes = fixture.Editor(client).Writes;
        fixture.Route(client).CurrentKeyId = fixture.Provider(client).Keys[1].Id;
        fixture.Coordinator.Refresh();
        Assert.Equal(writes, fixture.Editor(client).Writes);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
        Assert.Contains("改动", fixture.Workspace.Notice);
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public void NoKey_ManualTakeoverAndCancellationFailWithoutWriting(ClientType client)
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.Provider(client).Keys.Clear();
        fixture.Route(client).CurrentKeyId = string.Empty;
        fixture.SetState(client, ServiceState.Running);
        var before = fixture.Workspace.Config.Clone();
        Assert.Contains("Key", fixture.Coordinator.TakeOver(client));
        Assert.Contains("Key", fixture.Coordinator.CancelTakeover(client));
        fixture.Coordinator.Refresh();
        Assert.Equal(0, fixture.TotalWrites);
        Assert.Equal(0, fixture.SaveCalls);
        Assert.Equal(before, fixture.Workspace.Config);
    }

    [Fact]
    public void Takeover_ConfigBackupFailureDoesNotTouchClientOrMetadata()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.SetState(client, ServiceState.Running);
        fixture.FailBackup = true;
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        Assert.Contains("备份失败", fixture.Coordinator.TakeOver(client));
        Assert.Equal(0, fixture.TotalWrites);
        Assert.Equal(0, fixture.SaveCalls);
        Assert.False(fixture.State(client).Enabled);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Takeover_FileOrMetadataFailureLeavesOriginalClientAndConfig(bool failPersist)
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.SetState(client, ServiceState.Running);
        fixture.FailSave = failPersist;
        fixture.Editor(client).FailWrite = !failPersist;
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        var previous = fixture.Workspace.Config.Clone();
        Assert.NotNull(fixture.Coordinator.TakeOver(client));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
        Assert.Equal(previous, fixture.Workspace.Config);
        Assert.False(fixture.State(client).Enabled);
    }

    [Fact]
    public void CancelTakeover_PersistFailureRollsBackProxyFileAndEnabledChoice()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Codex;
        fixture.StartTakenOver(client);
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        var previous = fixture.Workspace.Config.Clone();
        fixture.FailSave = true;
        Assert.NotNull(fixture.Coordinator.CancelTakeover(client));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
        Assert.Equal(previous, fixture.Workspace.Config);
        Assert.True(fixture.State(client).Enabled);
    }

    [Fact]
    public void StopRoute_RestoreFailureKeepsServiceRunningAndProxyFileUnchanged()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        fixture.FailSave = true;
        var bytes = File.ReadAllBytes(fixture.PathFor(client));
        Assert.NotNull(fixture.Coordinator.StopRoute(fixture.Route(client).Id));
        Assert.Equal(ServiceState.Running, fixture.Service(client).State);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PathFor(client)));
        Assert.True(fixture.State(client).Enabled);
    }

    [Fact]
    public void ExplicitTakeover_AfterCancellationEnablesFutureTakeoverAgain()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Claude;
        fixture.StartTakenOver(client);
        var originalBackup = fixture.State(client).BackupPath;
        Assert.Null(fixture.Coordinator.CancelTakeover(client));
        Assert.False(fixture.State(client).Enabled);
        Assert.Null(fixture.Coordinator.TakeOver(client));
        Assert.True(fixture.State(client).Enabled);
        Assert.Equal(originalBackup, fixture.State(client).BackupPath);
        Assert.Equal(fixture.Route(client).LocalToken, fixture.Profile(client).ApiKey);
        Assert.Equal(1, fixture.BackupCalls);
    }

    [Fact]
    public void Shutdown_PersistentCancellationDoesNotWriteAgain()
    {
        using var fixture = new ClientLifecycleFixture();
        const ClientType client = ClientType.Codex;
        fixture.StartTakenOver(client);
        Assert.Null(fixture.Coordinator.CancelTakeover(client));
        var writes = fixture.TotalWrites;
        var saves = fixture.SaveCalls;
        fixture.Coordinator.Shutdown();
        Assert.Equal(writes, fixture.TotalWrites);
        Assert.Equal(saves, fixture.SaveCalls);
        Assert.False(fixture.State(client).Enabled);
    }

    [Fact]
    public void Shutdown_OneRestoreFailureDoesNotPreventOtherClientRestoration()
    {
        using var fixture = new ClientLifecycleFixture();
        fixture.StartTakenOver(ClientType.Claude);
        fixture.StartTakenOver(ClientType.Codex);
        fixture.Editor(ClientType.Claude).FailWrite = true;
        fixture.Coordinator.Shutdown();
        Assert.Equal(fixture.Route(ClientType.Claude).LocalToken, fixture.Profile(ClientType.Claude).ApiKey);
        Assert.Equal(fixture.Snapshot(ClientType.Codex).ApiKey, fixture.Profile(ClientType.Codex).ApiKey);
        Assert.True(fixture.State(ClientType.Claude).Enabled);
        Assert.True(fixture.State(ClientType.Codex).Enabled);
    }
}

/// <summary>只创建内存服务对象，用 ForceStateForTest 改状态；不调用任何启动、CLI 或端口监听方法。</summary>
internal sealed class ClientLifecycleFixture : IDisposable
{
    private readonly Dictionary<ClientType, LifecycleFakeEditor> _editors = new();
    private readonly Dictionary<ClientType, ClientConfigStore> _stores = new();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "RetryProxy.ClientLifecycle.Tests", Guid.NewGuid().ToString("N"));
    public ProxyLogger Logger { get; }
    public ProxyWorkspace Workspace { get; }
    public ClientTakeoverCoordinator Coordinator { get; }
    public bool FailSave { get; set; }
    public bool FailBackup { get; set; }
    public int SaveCalls { get; private set; }
    public int BackupCalls { get; private set; }
    public ProxyConfig? Saved { get; private set; }
    public Action<ProxyConfig>? OnSave { get; set; }
    public int TotalWrites => _editors.Values.Sum(editor => editor.Writes);

    public ClientLifecycleFixture()
    {
        Logger = ProxyLogger.Create(Path.Combine(Root, "logs"));
        var config = ProxyConfig.Builtin();
        foreach (var client in Enum.GetValues<ClientType>())
        {
            var route = config.RouteFor(client)!;
            route.ListenPort = client == ClientType.Codex ? 28080 : 28081;
            route.DesiredRunning = false;
            route.KeepaliveEnabled = false;
            route.LocalToken = $"redacted-local-token-{client}";
            var provider = new ProviderEndpoint
            {
                Id = $"provider-{client}", Name = $"测试供应商-{client}", ClientType = client,
                BaseUrl = $"https://{client.ToString().ToLowerInvariant()}.invalid",
                Models = new ProviderModels { Model = "redacted-model" },
                Keys =
                [
                    new ProviderKey { Id = $"key-1-{client}", Name = "默认", ApiKey = $"redacted-key-1-{client}" },
                    new ProviderKey { Id = $"key-2-{client}", Name = "备用", ApiKey = $"redacted-key-2-{client}" },
                ],
            };
            config.Providers.Add(provider);
            route.CurrentProviderId = provider.Id;
            route.CurrentKeyId = provider.Keys[0].Id;
        }
        Workspace = new ProxyWorkspace(Logger, config, candidate =>
        {
            SaveCalls++;
            OnSave?.Invoke(candidate);
            if (FailSave) throw new IOException("模拟保存失败");
            Saved = candidate.Clone();
        });
        Workspace.RefreshServices();
        foreach (var client in Enum.GetValues<ClientType>())
        {
            var editor = new LifecycleFakeEditor(client);
            _editors[client] = editor;
            _stores[client] = new ClientConfigStore(Path.Combine(Root, "backup"), editor, PathFor(client));
            Directory.CreateDirectory(Path.GetDirectoryName(PathFor(client))!);
            var text = editor.Write("{\"unmanaged\":\"kept-user-value\"}", new ClientConfigRequest
            {
                Channel = Snapshot(client), ListenPort = Route(client).ListenPort, UseProxy = false,
            });
            File.WriteAllText(PathFor(client), text);
            editor.ResetCounts();
        }
        Coordinator = new ClientTakeoverCoordinator(Workspace, (client, _) => _stores[client], () =>
        {
            BackupCalls++;
            if (FailBackup) throw new IOException("模拟备份失败");
        });
    }

    public string PathFor(ClientType client) => Path.Combine(Root, client.AsStr(), client == ClientType.Claude ? "settings.json" : "config.toml");
    public ProxyRoute Route(ClientType client) => Workspace.Config.RouteFor(client)!;
    public ProviderEndpoint Provider(ClientType client) => Workspace.Config.ProviderById(Route(client).CurrentProviderId)!;
    public ProviderKey Key(ClientType client) => Provider(client).KeyById(Route(client).CurrentKeyId)!;
    public ChannelSnapshot Snapshot(ClientType client) => Workspace.Config.SnapshotFor(Route(client).Id);
    public ClientTakeoverState State(ClientType client) => Workspace.Config.ClientTakeover[client];
    public ProxyService Service(ClientType client) => Workspace.Services[Route(client).Id];
    public LifecycleFakeEditor Editor(ClientType client) => _editors[client];
    public ClientProfile Profile(ClientType client) => _stores[client].ReadProfile();
    public JsonObject Document(ClientType client) => JsonNode.Parse(File.ReadAllText(PathFor(client)))!.AsObject();
    public void SetState(ClientType client, ServiceState state) => Service(client).ForceStateForTest(state);
    public void ChangeDocument(ClientType client, Action<JsonObject> change)
    {
        var document = Document(client);
        change(document);
        File.WriteAllText(PathFor(client), document.ToJsonString());
    }
    public void MarkManaged(ClientType client)
    {
        State(client).ConfigPath = PathFor(client);
        State(client).LastWrittenHash = _stores[client].CurrentHash();
    }
    public void StartTakenOver(ClientType client)
    {
        MarkManaged(client);
        State(client).Enabled = true;
        SetState(client, ServiceState.Running);
        Coordinator.Refresh();
        Assert.Equal(Route(client).LocalToken, Profile(client).ApiKey);
    }
    public void SeedProxy(ClientType client)
    {
        var text = Editor(client).Write(File.ReadAllText(PathFor(client)), new ClientConfigRequest
        {
            Channel = Snapshot(client), ListenPort = Route(client).ListenPort, UseProxy = true,
        });
        File.WriteAllText(PathFor(client), text);
        Editor(client).ResetCounts();
    }
    public void Dispose()
    {
        foreach (var service in Workspace.Services.Values) service.ForceStateForTest(ServiceState.Stopped);
        Logger.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal sealed class LifecycleFakeEditor(ClientType client) : IClientConfigEditor
{
    public ClientType ClientType => client;
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public bool FailWrite { get; set; }
    public List<ClientConfigRequest> Requests { get; } = new();
    public void ResetCounts() { Reads = 0; Writes = 0; Requests.Clear(); }
    public ClientProfile Read(string text, string? authText = null)
    {
        Reads++;
        var data = string.IsNullOrEmpty(text) ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
        return new ClientProfile
        {
            ClientType = client, BaseUrl = data["url"]?.GetValue<string>() ?? string.Empty,
            ApiKey = data["key"]?.GetValue<string>() ?? string.Empty,
            AuthMode = (ClaudeAuthMode)(data["auth"]?.GetValue<int>() ?? (int)ClaudeAuthMode.Bearer),
            Models = data["models"]?.Deserialize<ProviderModels>() ?? new ProviderModels(),
            WireApi = data["wire"]?.GetValue<string>() ?? string.Empty,
            HasApiKeyHelper = data["helper"]?.GetValue<bool>() ?? false,
            HasConflictingSettings = data["conflicting"]?.GetValue<bool>() ?? false,
        };
    }
    public string Write(string text, ClientConfigRequest request)
    {
        Writes++;
        Requests.Add(request);
        if (FailWrite) throw new ClientConfigException("模拟客户端编辑失败");
        var data = string.IsNullOrEmpty(text) ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
        if (!request.ModelsOnly)
        {
            data["url"] = request.UseProxy
                ? UrlRules.ClientBaseUrl(client, $"http://127.0.0.1:{request.ListenPort}")
                : client == ClientType.Codex ? UrlRules.CodexApiRoot(request.Channel.UpstreamBaseUrl) : request.Channel.UpstreamBaseUrl;
            data["key"] = request.UseProxy ? request.Channel.LocalToken : request.Channel.ApiKey;
            data["auth"] = (int)(request.UseProxy || client == ClientType.Codex ? ClaudeAuthMode.Bearer : request.Channel.AuthMode);
            data["wire"] = client == ClientType.Codex ? "responses" : string.Empty;
            data["helper"] = false;
        }
        data["models"] = JsonSerializer.SerializeToNode(request.EffectiveModels);
        return data.ToJsonString();
    }
}
