using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

public class ProviderManagementTests
{
    private sealed class Fixture : IDisposable
    {
        public Fixture(bool empty = false)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
            Logger = ProxyLogger.Create(DirectoryPath);
            var config = ProxyConfig.Builtin();
            foreach (var route in config.Routes)
            {
                route.ListenPort = FreePort();
                route.DesiredRunning = false;
                route.KeepaliveEnabled = false;
            }

            if (!empty)
            {
                config.Providers.Add(Provider("a", ClientType.Codex, "a1", "a2", "a3"));
                config.Providers.Add(Provider("claude", ClientType.Claude, "c1"));
                config.Providers.Add(Provider("b", ClientType.Codex, "b1", "b2"));
                config.Providers.Add(Provider("c", ClientType.Codex, "d1"));
                var codex = config.RouteFor(ClientType.Codex)!;
                codex.CurrentProviderId = "a";
                codex.CurrentKeyId = "a1";
            }

            App = new ProxyWorkspace(Logger, config, candidate =>
            {
                Events.Add("save");
                if (FailSave)
                {
                    throw new IOException("模拟写入失败");
                }

                Saved = candidate.Clone();
            });
            App.BeforeDestructiveChange = () =>
            {
                Events.Add("backup");
                Assert.Equal(BeforeBackup ?? App.Config, App.Config);
                if (FailBackup)
                {
                    throw new IOException("模拟备份失败");
                }
            };
            App.RefreshServices();
        }

        public string DirectoryPath { get; }
        public ProxyLogger Logger { get; }
        public ProxyWorkspace App { get; }
        public bool FailSave { get; set; }
        public bool FailBackup { get; set; }
        public ProxyConfig? Saved { get; private set; }
        public ProxyConfig? BeforeBackup { get; set; }
        public List<string> Events { get; } = new();
        public ProxyRoute Codex => App.Config.RouteFor(ClientType.Codex)!;

        public async Task<ProxyService> StartCodex()
        {
            App.StartRoute(Codex.Id);
            var service = App.Services[Codex.Id];
            await TestClock.WaitUntil(() => service.State is ServiceState.Running or ServiceState.Error);
            Assert.Equal(ServiceState.Running, service.State);
            return service;
        }

        public void Dispose()
        {
            foreach (var service in App.Services.Values)
            {
                service.Stop(TimeSpan.FromSeconds(5));
            }

            Logger.Dispose();
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static ProviderEndpoint Provider(string id, ClientType client, params string[] keys) => new()
    {
        Id = id,
        Name = id,
        ClientType = client,
        BaseUrl = "http://127.0.0.1:9",
        Keys = keys.Select(key => new ProviderKey { Id = key, Name = key, ApiKey = $"sk-fixture-{key}" }).ToList(),
    };

    [Fact]
    public void NewProviderRequiresAKeyAndBindsTheFirstProviderAndKey()
    {
        using var fixture = new Fixture(empty: true);
        var app = fixture.App;
        var draft = Provider(string.Empty, ClientType.Claude);
        draft.Name = " Any ";
        Assert.Contains("至少", app.SaveProvider(draft, true));
        Assert.Empty(app.Config.Providers);
        Assert.Empty(fixture.Events);
        draft.Keys.Add(new ProviderKey { ApiKey = " sk-example ", Notes = " first " });
        Assert.Null(app.SaveProvider(draft, true));
        var saved = Assert.Single(app.Config.Providers);
        Assert.NotEmpty(saved.Id);
        Assert.Equal("Any", saved.Name);
        var key = Assert.Single(saved.Keys);
        Assert.NotEmpty(key.Id);
        Assert.Equal(("默认", "sk-example", "first"), (key.Name, key.ApiKey, key.Notes));
        var route = app.Config.RouteFor(ClientType.Claude)!;
        Assert.Equal((saved.Id, key.Id), (route.CurrentProviderId, route.CurrentKeyId));
        Assert.Equal(ClientType.Claude, app.SelectedClient);
        Assert.Equal(route.Id, fixture.Saved!.SelectedRouteId);
        Assert.Equal(new[] { "save" }, fixture.Events);
        Assert.Empty(draft.Id);
        Assert.Empty(draft.Keys[0].Id);
        Assert.Empty(draft.Keys[0].Name);
        draft.Keys[0].ApiKey = "changed-outside";
        Assert.Equal("sk-example", app.Config.CurrentKeyOf(route)!.ApiKey);
    }

    [Fact]
    public void EditingUsesIdsAndKeepsEveryProviderField()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var draft = app.Config.ProviderById("b")!.Clone();
        draft.Name = "renamed";
        draft.BaseUrl = "https://new.example/v1/";
        draft.WebsiteUrl = "https://new.example";
        draft.Notes = " notes ";
        draft.Models.Model = " new-model ";
        draft.Models.ContextWindow = 123456;
        draft.Models.AutoCompactTokenLimit = 100000;
        draft.FetchedModels.Add(" other-model ");
        draft.BalanceQuery.Mode = BalanceQueryMode.None;
        draft.Keys[0].ModelOverride = new KeyModelOverride { Model = " override ", Context1M = true };
        Assert.Null(app.MoveProvider("b", "a"));
        Assert.Null(app.SaveProvider(draft, false));
        var saved = app.Config.ProviderById("b")!;
        Assert.Equal("b", app.Config.Providers[0].Id);
        Assert.Equal(("renamed", "https://new.example/v1", "notes", "new-model"), (saved.Name, saved.BaseUrl, saved.Notes, saved.Models.Model));
        Assert.Equal(123456, saved.Models.ContextWindow);
        Assert.Equal(100000, saved.Models.AutoCompactTokenLimit);
        Assert.Equal("https://new.example", saved.WebsiteUrl);
        Assert.Equal(BalanceQueryMode.None, saved.BalanceQuery.Mode);
        Assert.Equal("override", saved.Keys[0].ModelOverride!.Model);
        Assert.True(saved.Keys[0].ModelOverride!.Context1M);
        Assert.Equal(new[] { "other-model" }, saved.FetchedModels);
        Assert.Equal("a", fixture.Codex.CurrentProviderId);
        Assert.Equal("a1", fixture.Codex.CurrentKeyId);
        draft.Models.Model = "outside";
        Assert.Equal("new-model", saved.Models.Model);
        Assert.NotNull(app.SaveProvider(draft, true));
        draft.Id = "missing";
        Assert.Equal("找不到该供应商", app.SaveProvider(draft, false));
    }

    [Fact]
    public void MigratedProviderCanStayEmptyAndItsFirstKeyIsAutomaticallyBound()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var existing = app.Config.ProviderById("a")!;
        existing.Keys.Clear();
        fixture.Codex.CurrentKeyId = string.Empty;
        var draft = existing.Clone();
        draft.Notes = "迁移后补资料";
        Assert.Null(app.SaveProvider(draft, false));
        Assert.Empty(app.Config.ProviderById("a")!.Keys);
        Assert.Null(app.SaveKey("a", new ProviderKey { ApiKey = "sk-first" }, true));
        var first = Assert.Single(app.Config.ProviderById("a")!.Keys);
        Assert.Equal("默认", first.Name);
        Assert.Equal(first.Id, fixture.Codex.CurrentKeyId);
        Assert.Equal("sk-first", app.Config.SnapshotFor(fixture.Codex.Id).ApiKey);
    }

    [Fact]
    public void WholeConfigValidationRejectsDuplicateNamesAndBrokenReferences()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var draft = Provider("new", ClientType.Codex, "new-key");
        draft.Name = " A ";
        Assert.Contains("名称重复", app.SaveProvider(draft, true));
        draft.ClientType = ClientType.Claude;
        Assert.Null(app.SaveProvider(draft, true));
        var edit = app.Config.ProviderById("a")!.Clone();
        edit.Keys[1].Name = "A1";
        Assert.Contains("名称重复", app.SaveProvider(edit, false));
        edit.Keys[1].Name = "new";
        edit.Keys[1].Id = "a1";
        Assert.Contains("ID 重复", app.SaveProvider(edit, false));
        edit = app.Config.ProviderById("a")!.Clone();
        edit.ClientType = ClientType.Claude;
        Assert.Contains("所属客户端", app.SaveProvider(edit, false));
        edit = app.Config.ProviderById("a")!.Clone();
        edit.BaseUrl = "https://example.com?token=1";
        Assert.NotNull(app.SaveProvider(edit, false));
        edit = app.Config.ProviderById("a")!.Clone();
        edit.Keys[0].ApiKey = " ";
        Assert.Contains("未填写密钥", app.SaveProvider(edit, false));
        fixture.Events.Clear();
        app.Config.RouteFor(ClientType.Claude)!.ListenPort = fixture.Codex.ListenPort;
        edit = app.Config.ProviderById("a")!.Clone();
        Assert.Contains("本地端口重复", app.SaveProvider(edit, false));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public void KeysSupportCrudAndEnforceCurrentAndLastKeyRestrictions()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        Assert.Contains("当前 Key", app.DeleteKey("a", "a1"));
        Assert.Contains("至少保留", app.DeleteKey("c", "d1"));
        Assert.Equal("找不到该供应商", app.DeleteKey("missing", "key"));
        Assert.Equal("找不到该 Key", app.DeleteKey("a", "missing"));
        Assert.Empty(fixture.Events);
        var draft = new ProviderKey { Name = " extra ", ApiKey = " sk-extra ", Notes = " note ", ModelOverride = new KeyModelOverride { Model = " o3 " } };
        Assert.Null(app.SaveKey("a", draft, true));
        Assert.Empty(draft.Id);
        var added = app.Config.ProviderById("a")!.Keys.Last().Clone();
        Assert.Equal(("extra", "sk-extra", "note", "o3"), (added.Name, added.ApiKey, added.Notes, added.ModelOverride!.Model));
        Assert.NotNull(app.SaveKey("a", added, true));
        added.Name = "renamed";
        added.ApiKey = "sk-changed";
        Assert.Null(app.SaveKey("a", added, false));
        Assert.Equal("sk-changed", app.Config.ProviderById("a")!.KeyById(added.Id)!.ApiKey);
        fixture.Events.Clear();
        fixture.BeforeBackup = app.Config.Clone();
        Assert.Null(app.DeleteKey("a", added.Id));
        Assert.Null(app.Config.ProviderById("a")!.KeyById(added.Id));
        Assert.Equal(new[] { "backup", "save" }, fixture.Events);
        Assert.Equal("a1", fixture.Codex.CurrentKeyId);
        Assert.Equal("找不到该 Key", app.SaveKey("a", added, false));
    }

    [Fact]
    public void SavingAProviderDraftCannotBypassDeletionRestrictionsOrBackup()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var draft = app.Config.ProviderById("a")!.Clone();
        draft.Keys.RemoveAt(0);
        Assert.Contains("当前 Key", app.SaveProvider(draft, false));
        draft = app.Config.ProviderById("c")!.Clone();
        draft.Keys.Clear();
        Assert.Contains("至少保留", app.SaveProvider(draft, false));
        Assert.Empty(fixture.Events);
        draft = app.Config.ProviderById("a")!.Clone();
        draft.Keys.RemoveAt(1);
        draft.Name = "renamed";
        fixture.BeforeBackup = app.Config.Clone();
        Assert.Null(app.SaveProvider(draft, false));
        Assert.Equal(new[] { "backup", "save" }, fixture.Events);
        Assert.Equal("renamed", app.Config.ProviderById("a")!.Name);
        Assert.Null(app.Config.ProviderById("a")!.KeyById("a2"));
    }

    [Fact]
    public void RemovingAProviderRequiresItToBeUnusedAndBacksUpOnlyValidDeletion()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        Assert.Contains("当前供应商", app.RemoveProvider("a"));
        Assert.Equal("找不到该供应商", app.RemoveProvider("missing"));
        Assert.Empty(fixture.Events);
        app.SelectClient(ClientType.Claude);
        fixture.Events.Clear();
        fixture.BeforeBackup = app.Config.Clone();
        Assert.Null(app.RemoveProvider("claude"));
        Assert.Equal(new[] { "backup", "save" }, fixture.Events);
        Assert.Null(app.Config.ProviderById("claude"));
        Assert.Equal(ClientType.Claude, app.SelectedClient);
        Assert.Empty(app.SelectedProvider);
        Assert.Equal(app.Config.RouteFor(ClientType.Claude)!.Id, app.SelectedRouteRef()!.Id);
    }

    [Theory]
    [InlineData("delete-key")]
    [InlineData("remove-provider")]
    [InlineData("edit-delete-key")]
    public void BackupFailureDoesNotSaveOrChangeMemory(string operation)
    {
        using var fixture = new Fixture();
        var before = fixture.App.Config.Clone();
        fixture.FailBackup = true;
        Assert.Contains("备份失败", Manage(fixture.App, operation));
        Assert.Equal(before, fixture.App.Config);
        Assert.Equal(new[] { "backup" }, fixture.Events);
    }

    [Theory]
    [InlineData("save-provider")]
    [InlineData("new-provider")]
    [InlineData("save-key")]
    [InlineData("delete-key")]
    [InlineData("remove-provider")]
    [InlineData("edit-delete-key")]
    [InlineData("duplicate")]
    [InlineData("move-provider")]
    [InlineData("move-key")]
    [InlineData("switch")]
    [InlineData("route")]
    public void SavingFailureRollsBackAllManagementOperations(string operation)
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var before = app.Config.Clone();
        var selected = (app.SelectedClient, app.SelectedProvider, app.SelectedRoute, app.KeepAliveMinutes);
        var service = app.Services[fixture.Codex.Id];
        fixture.FailSave = true;
        Assert.Contains("保存失败", Manage(app, operation));
        Assert.Equal(before, app.Config);
        Assert.Equal(selected, (app.SelectedClient, app.SelectedProvider, app.SelectedRoute, app.KeepAliveMinutes));
        Assert.Same(service, app.Services[fixture.Codex.Id]);
        Assert.Null(fixture.Saved);
    }

    private static string? Manage(ProxyWorkspace app, string operation)
    {
        var draft = app.Config.ProviderById("a")!.Clone();
        switch (operation)
        {
            case "save-provider":
                draft.Name = "renamed";
                return app.SaveProvider(draft, false);
            case "new-provider":
                return app.SaveProvider(Provider("new", ClientType.Claude, "new-key"), true);
            case "save-key":
                return app.SaveKey("a", new ProviderKey { Name = "new", ApiKey = "sk-new" }, true);
            case "delete-key":
                return app.DeleteKey("a", "a2");
            case "remove-provider":
                return app.RemoveProvider("b");
            case "edit-delete-key":
                draft.Keys.RemoveAt(1);
                return app.SaveProvider(draft, false);
            case "duplicate":
                return app.DuplicateProvider("a");
            case "move-provider":
                return app.MoveProvider("a", "b");
            case "move-key":
                return app.MoveKey("a", "a1", "a3");
            case "switch":
                return app.SwitchKey(app.Config.RouteFor(ClientType.Codex)!.Id, "b", "b1");
            case "route":
                var editor = app.OpenRouteEditor(0)!;
                editor.Retries = "42";
                return app.CommitRoute(editor);
            default:
                throw new InvalidOperationException(operation);
        }
    }

    [Fact]
    public void InvalidWholeConfigDoesNotInvokeDeletionBackup()
    {
        using var fixture = new Fixture();
        fixture.App.Config.ProviderById("c")!.BaseUrl = string.Empty;
        Assert.NotNull(fixture.App.DeleteKey("a", "a2"));
        Assert.Empty(fixture.Events);
        Assert.NotNull(fixture.App.Config.ProviderById("a")!.KeyById("a2"));
    }

    [Fact]
    public void DuplicateGetsIndependentIdsAndUniqueNameImmediatelyAfterTheSource()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.Config.ProviderById("a")!.FetchedModels.Add("known");
        app.Config.ProviderById("a")!.Keys[0].ModelOverride = new KeyModelOverride { Model = "model-a" };
        Assert.Null(app.DuplicateProvider("a"));
        var first = app.Config.Providers[1];
        Assert.Equal("a 副本", first.Name);
        Assert.Equal(first.Id, app.SelectedProvider);
        Assert.NotEqual("a", first.Id);
        Assert.Empty(first.Keys.Select(key => key.Id).Intersect(app.Config.Providers[0].Keys.Select(key => key.Id)));
        Assert.Null(app.DuplicateProvider("a"));
        Assert.Equal("a 副本 2", app.Config.Providers[1].Name);
        Assert.Equal(first.Id, app.Config.Providers[2].Id);
        Assert.Equal("a", fixture.Codex.CurrentProviderId);
        Assert.Equal("a1", fixture.Codex.CurrentKeyId);
        app.Config.Providers[1].FetchedModels.Add("only-copy");
        app.Config.Providers[1].Keys[0].ModelOverride!.Model = "only-copy";
        Assert.Equal(new[] { "known" }, app.Config.Providers[0].FetchedModels);
        Assert.Equal("model-a", app.Config.Providers[0].Keys[0].ModelOverride!.Model);
        Assert.DoesNotContain("backup", fixture.Events);
    }

    [Fact]
    public void ProviderAndKeyMovesStayInTheirClientAndCardAndPersistOrder()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        Assert.NotNull(app.MoveProvider("a", "claude"));
        Assert.NotNull(app.MoveProvider("a", "missing"));
        Assert.NotNull(app.MoveKey("a", "a1", "b1"));
        Assert.Empty(fixture.Events);
        Assert.Null(app.MoveProvider("a", "c"));
        Assert.Equal(new[] { "b", "claude", "c", "a" }, app.Config.Providers.Select(provider => provider.Id));
        Assert.Null(app.MoveProvider("a", "b"));
        Assert.Equal(new[] { "a", "claude", "b", "c" }, app.Config.Providers.Select(provider => provider.Id));
        Assert.Null(app.MoveKey("a", "a1", "a3"));
        Assert.Equal(new[] { "a2", "a3", "a1" }, app.Config.ProviderById("a")!.Keys.Select(key => key.Id));
        Assert.Null(app.MoveKey("a", "a1", "a2"));
        Assert.Equal(new[] { "a1", "a2", "a3" }, app.Config.ProviderById("a")!.Keys.Select(key => key.Id));
        Assert.Equal("a1", fixture.Codex.CurrentKeyId);
        var reloaded = ProxyConfigJson.Parse(ProxyConfigJson.ToCanonicalJson(app.Config)).Config;
        Assert.Equal(app.Config, reloaded);
        using var json = ProxyConfigJson.ToCanonicalDocument(app.Config);
        Assert.Equal(new[] { 0, 0, 1, 2 }, json.RootElement.GetProperty("providers").EnumerateArray().Select(provider => provider.GetProperty("sort_index").GetInt32()));
    }

    [Fact]
    public void SharedClientSelectionSurvivesEmptyProvidersRefreshAndReload()
    {
        using var fixture = new Fixture(empty: true);
        var app = fixture.App;
        app.SelectClient(ClientType.Claude);
        var claudeId = app.Config.RouteFor(ClientType.Claude)!.Id;
        app.Config.Providers.Add(Provider("only-codex", ClientType.Codex, "key"));
        app.SyncSelection();
        app.RefreshServices();
        Assert.Equal(ClientType.Claude, app.SelectedClient);
        Assert.Empty(app.SelectedProvider);
        Assert.Equal(claudeId, app.SelectedRouteRef()!.Id);
        Assert.Equal(claudeId, app.SelectedRoute);
        Assert.Equal(claudeId, Assert.Single(app.VisibleRoutes()).Id);
        var restored = new ProxyWorkspace(fixture.Logger, ProxyConfigJson.Parse(ProxyConfigJson.ToCanonicalJson(fixture.Saved!)).Config, null);
        restored.SyncSelection();
        Assert.Equal(ClientType.Claude, restored.SelectedClient);
        Assert.Equal(claudeId, restored.SelectedRouteRef()!.Id);
        app.SelectProvider("only-codex");
        Assert.Equal(ClientType.Codex, app.SelectedClient);
        app.SelectRoute(claudeId);
        Assert.Equal(ClientType.Claude, app.SelectedClient);
        Assert.Empty(app.SelectedProvider);
    }

    [Fact]
    public void SelectionSaveFailureKeepsTheOldClient()
    {
        using var fixture = new Fixture(empty: true);
        fixture.FailSave = true;
        var before = fixture.App.Config.Clone();
        fixture.App.SelectClient(ClientType.Claude);
        Assert.Equal(ClientType.Codex, fixture.App.SelectedClient);
        Assert.Contains("保存失败", fixture.App.Notice);
        Assert.Equal(before, fixture.App.Config);
    }

    [Theory]
    [InlineData("", "····")]
    [InlineData("a", "····")]
    [InlineData("abcd", "····")]
    [InlineData("abcdefgh", "····")]
    [InlineData("abcdefghi", "abcd····fghi")]
    [InlineData("sk-a123456c3f9", "sk-a····c3f9")]
    public void MaskingNeverRevealsTheEntireKey(string value, string expected)
    {
        Assert.Equal(expected, new ProviderKey { ApiKey = value }.MaskedKey);
    }

    [Fact]
    public void FetchedModelsAreNormalizedClonedPersistedAndIncludedInSnapshots()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.Config.ProviderById("a")!.Models.Model = "main";
        app.Config.ProviderById("a")!.Keys[1].ModelOverride = new KeyModelOverride { Model = "override" };
        app.RememberFetchedModels("a", new[] { " o3 ", "", "O3", "gpt-known" });
        Assert.Equal(new[] { "o3", "gpt-known" }, app.Config.ProviderById("a")!.FetchedModels);
        Assert.Equal(new[] { "save" }, fixture.Events);
        using var json = ProxyConfigJson.ToCanonicalDocument(fixture.Saved!);
        Assert.Equal(new[] { "o3", "gpt-known" }, json.RootElement.GetProperty("providers")[0].GetProperty("fetched_models").EnumerateArray().Select(model => model.GetString()));
        var restored = ProxyConfigJson.Parse(ProxyConfigJson.ToCanonicalJson(fixture.Saved!)).Config;
        restored.Validate(false);
        Assert.Equal(app.Config, restored);
        var snapshot = restored.SnapshotFor(fixture.Codex.Id, new[] { "compat-extra" });
        foreach (var model in new[] { "main", "override", "o3", "gpt-known", "compat-extra" })
        {
            Assert.Contains(model, snapshot.KnownModels);
        }

        var copy = restored.Clone();
        copy.Providers[0].FetchedModels.Add("copy-only");
        Assert.NotEqual(restored, copy);
        Assert.DoesNotContain("copy-only", restored.Providers[0].FetchedModels);
        restored.Providers[0].FetchedModels.Clear();
        Assert.Contains("o3", snapshot.KnownModels);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    public void InvalidFetchedModelJsonIsRejected(string value)
    {
        using var fixture = new Fixture();
        var json = ProxyConfigJson.ToCanonicalJson(fixture.App.Config).Replace("\"fetched_models\": []", $"\"fetched_models\": {value}", StringComparison.Ordinal);
        Assert.Throws<ConfigException>(() => ProxyConfigJson.Parse(json));
    }

    [Fact]
    public void OlderSchemaSevenWithoutFetchedModelsLoadsAnEmptyList()
    {
        using var fixture = new Fixture();
        var root = JsonNode.Parse(ProxyConfigJson.ToCanonicalJson(fixture.App.Config))!;
        foreach (var provider in root["providers"]!.AsArray())
        {
            provider!.AsObject().Remove("fetched_models");
        }

        var json = root.ToJsonString();
        Assert.DoesNotContain("\"fetched_models\"", json);
        Assert.All(ProxyConfigJson.Parse(json).Config.Providers, provider => Assert.Empty(provider.FetchedModels));
    }

    [Fact]
    public async Task ManagementHotUpdatesKeepTheServiceAndDoNotPublishFailedSaves()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var service = await fixture.StartCodex();
        var key = app.Config.ProviderById("a")!.Keys[0].Clone();
        key.ApiKey = "sk-updated";
        key.ModelOverride = new KeyModelOverride { Model = "new-model" };
        Assert.Null(app.SaveKey("a", key, false));
        Assert.Equal("sk-updated", service.CurrentSnapshot!.ApiKey);
        Assert.Equal("new-model", service.CurrentSnapshot.ModelOverride!.Model);
        var provider = app.Config.ProviderById("a")!.Clone();
        provider.BaseUrl = "http://127.0.0.1:10";
        Assert.Null(app.SaveProvider(provider, false));
        Assert.Equal(provider.BaseUrl, service.CurrentSnapshot.UpstreamBaseUrl);
        app.RememberFetchedModels("a", new[] { "known-new" });
        Assert.Contains("known-new", service.CurrentSnapshot.KnownModels);
        var published = service.CurrentSnapshot;
        fixture.FailSave = true;
        key.ApiKey = "sk-must-not-publish";
        Assert.Contains("保存失败", app.SaveKey("a", key, false));
        Assert.Same(published, service.CurrentSnapshot);
        Assert.Contains("保存失败", app.SwitchKey(fixture.Codex.Id, "a", "a2"));
        Assert.Same(published, service.CurrentSnapshot);
        Assert.Equal("a1", fixture.Codex.CurrentKeyId);
        app.RememberFetchedModels("a", new[] { "must-not-publish" });
        Assert.Contains("保存失败", app.Notice);
        Assert.Same(published, service.CurrentSnapshot);
        Assert.Equal(new[] { "known-new" }, app.Config.ProviderById("a")!.FetchedModels);
        Assert.Equal("sk-updated", app.Config.ProviderById("a")!.Keys[0].ApiKey);
        Assert.Same(service, app.Services[fixture.Codex.Id]);
        Assert.Equal(ServiceState.Running, service.State);
    }

    [Fact]
    public async Task AutomaticStartupIgnoresHistoricalFlagsButRespectsThisRunsManualStop()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        Assert.False(fixture.Codex.DesiredRunning);
        app.StartDesiredRoutes();
        var service = app.Services[fixture.Codex.Id];
        await TestClock.WaitUntil(() => service.State is ServiceState.Running or ServiceState.Error);
        Assert.Equal(ServiceState.Running, service.State);
        Assert.False(app.Services.ContainsKey(app.Config.RouteFor(ClientType.Claude)!.Id));
        app.StopRoute(fixture.Codex.Id);
        await TestClock.WaitUntil(() => service.State == ServiceState.Stopped);
        Assert.False(fixture.Codex.DesiredRunning);
        app.StartDesiredRoutes();
        Assert.Equal(ServiceState.Stopped, service.State);
        Assert.Null(app.SaveKey("a", new ProviderKey { Name = "extra", ApiKey = "sk-extra" }, true));
        app.StartDesiredRoutes();
        Assert.Equal(ServiceState.Stopped, service.State);
        await fixture.StartCodex();
        app.StopRoute(fixture.Codex.Id);
        await TestClock.WaitUntil(() => service.State == ServiceState.Stopped);
        var restarted = new ProxyWorkspace(fixture.Logger, app.Config, null);
        try
        {
            restarted.StartDesiredRoutes();
            await TestClock.WaitUntil(() => restarted.RouteState(fixture.Codex.Id) is ServiceState.Running or ServiceState.Error);
            Assert.Equal(ServiceState.Running, restarted.RouteState(fixture.Codex.Id));
        }
        finally
        {
            restarted.Shutdown();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstProviderAddedAfterStartupRunsUnlessManuallyStopped(bool manuallyStopped)
    {
        using var fixture = new Fixture(empty: true);
        var app = fixture.App;
        app.StartDesiredRoutes();
        Assert.Empty(app.Services);
        if (manuallyStopped)
        {
            app.StopRoute(fixture.Codex.Id);
        }

        Assert.Null(app.SaveProvider(Provider("first", ClientType.Codex, "key"), true));
        var service = app.Services[fixture.Codex.Id];
        if (manuallyStopped)
        {
            Assert.Equal(ServiceState.Stopped, service.State);
        }
        else
        {
            await TestClock.WaitUntil(() => service.State is ServiceState.Running or ServiceState.Error);
            Assert.Equal(ServiceState.Running, service.State);
        }
    }

    [Fact]
    public void SetKeepAliveSaveFailurePreservesConfigurationAndWatchdog()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var before = app.Config.Clone();
        var watchdog = app.RouteKeepAlives[fixture.Codex.Id];
        var idle = watchdog.Idle;
        fixture.FailSave = true;
        app.SetKeepAlive(fixture.Codex.Id, true, 10, 20000, ReasoningEffort.Low);
        Assert.Contains("保存失败", app.Notice);
        Assert.Equal(before, app.Config);
        Assert.False(watchdog.Enabled);
        Assert.Equal(idle, watchdog.Idle);
        Assert.Equal((ulong)fixture.Codex.KeepaliveContextLimit, watchdog.Snapshot().ContextLimit);
        Assert.Equal(fixture.Codex.KeepaliveReasoningEffort, watchdog.Snapshot().ReasoningEffort);
        fixture.Events.Clear();
        app.SetKeepAlive(fixture.Codex.Id, true, 10, -1);
        Assert.Contains("必须大于 0", app.Notice);
        Assert.Empty(fixture.Events);
        Assert.Equal(before, app.Config);
    }

    [Fact]
    public void RouteAndKeepaliveSettingsCommitOnceAndRollBackTogether()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var editor = app.OpenRouteEditor(0)!;
        editor.Retries = "42";
        editor.KeepaliveEnabled = true;
        editor.KeepaliveIdleMinutes = 10;
        editor.KeepaliveContextLimit = 12345;
        editor.KeepaliveReasoningEffort = ReasoningEffort.Low;
        Assert.Null(app.CommitRoute(editor));
        Assert.Equal(new[] { "save" }, fixture.Events);
        Assert.Equal(42, fixture.Codex.MaxRetries);
        Assert.True(fixture.Codex.KeepaliveEnabled);
        Assert.Equal(10, fixture.Codex.KeepaliveIdleMinutes);
        Assert.Equal(12345, fixture.Codex.KeepaliveContextLimit);
        Assert.Equal(ReasoningEffort.Low, fixture.Codex.KeepaliveReasoningEffort);
        var watchdog = app.RouteKeepAlives[fixture.Codex.Id];
        Assert.True(watchdog.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(10), watchdog.Idle);
        Assert.Equal(12345UL, watchdog.Snapshot().ContextLimit);
        Assert.Equal(ReasoningEffort.Low, watchdog.Snapshot().ReasoningEffort);
        var before = app.Config.Clone();
        fixture.Events.Clear();
        editor.KeepaliveIdleMinutes = 0;
        Assert.NotNull(app.CommitRoute(editor));
        Assert.Empty(fixture.Events);
        Assert.Equal(before, app.Config);
        editor.KeepaliveIdleMinutes = 20;
        editor.KeepaliveEnabled = false;
        fixture.FailSave = true;
        Assert.Contains("保存失败", app.CommitRoute(editor));
        Assert.Equal(before, app.Config);
        Assert.True(watchdog.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(10), watchdog.Idle);
    }

    [Theory]
    [InlineData("key-whitespace")]
    [InlineData("client")]
    [InlineData("auth")]
    [InlineData("context-window")]
    [InlineData("compact-limit")]
    public void InvalidProviderFieldsAreRejectedBeforeSaving(string field)
    {
        using var fixture = new Fixture();
        var draft = fixture.App.Config.ProviderById("a")!.Clone();
        switch (field)
        {
            case "key-whitespace": draft.Keys[0].ApiKey = "sk-a\r\nheader"; break;
            case "client": draft.ClientType = (ClientType)99; break;
            case "auth": draft.AuthMode = (RetryProxy.Core.Cli.ClaudeAuthMode)99; break;
            case "context-window": draft.Models.ContextWindow = -1; break;
            case "compact-limit": draft.Models.ContextWindow = 100; draft.Models.AutoCompactTokenLimit = 101; break;
        }

        Assert.NotNull(fixture.App.SaveProvider(draft, false));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task LastSwitchResentCountTracksRealResendsAndResetsOnEverySwitchCall()
    {
        var receivedOldKey = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await FakeUpstream.StartAsync(context =>
        {
            if (context.Request.Headers.Authorization.ToString() == "Bearer sk-fixture-a1")
            {
                receivedOldKey.TrySetResult();
                return Upstream.Pending(context);
            }

            return Upstream.Json(context, 200, "{}");
        });
        using var fixture = new Fixture();
        var app = fixture.App;
        app.Config.ProviderById("a")!.BaseUrl = upstream.BaseUrl;
        var service = await fixture.StartCodex();
        using var client = TestClient.Create(10);
        var pending = TestClient.Send(client, HttpMethod.Post, $"http://127.0.0.1:{fixture.Codex.ListenPort}/v1/responses", "{}",
            headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {fixture.Codex.LocalToken}" });
        await receivedOldKey.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await TestClock.WaitUntil(() => service.Metrics.Snapshot().Requests.Any(request => request.Phase == RequestPhase.WaitingResponse));
        Assert.Null(app.SwitchKey(fixture.Codex.Id, "a", "a2"));
        Assert.Equal(1, app.LastSwitchResentCount);
        using var response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(app.SwitchKey(fixture.Codex.Id, "a", "a2"));
        Assert.Equal(0, app.LastSwitchResentCount);
        Assert.NotNull(app.SwitchKey("missing", "a", "a1"));
        Assert.Equal(0, app.LastSwitchResentCount);
    }
}
