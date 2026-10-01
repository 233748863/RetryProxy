using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

public sealed class PreparationCatalogTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        public ProxyConfig Config { get; } = CreateConfig();
        public ProxyLogger Logger { get; }
        public PreparationWorkspace Preparations { get; }
        public PreparationCatalog Catalog { get; }
        public ProxyConfig? SavedProxy { get; private set; }
        public List<SavedPreparation>? SavedTasks { get; private set; }
        public bool FailSave { get; set; }
        public int Saves { get; private set; }

        public Fixture()
        {
            Logger = ProxyLogger.Create(_directory);
            Preparations = new PreparationWorkspace(Logger, [Entry("task-a", "a1"), Entry("task-b", "a2")],
                save: tasks => SavedTasks = tasks);
            Catalog = new PreparationCatalog(() => Config, Preparations, (proxy, tasks) =>
            {
                Saves++;
                if (FailSave) throw new IOException("sk-secret-must-not-leak");
                SavedProxy = proxy.Clone();
                SavedTasks = tasks;
            });
        }

        public static SavedPreparation Entry(string id, string key) => new()
        {
            Id = id, ClientType = "codex", ProviderSource = "list", ProviderId = "a", KeyId = key,
            Model = "", IdleMinutes = "5", ReasoningEffort = "default",
        };

        public void Dispose()
        {
            Preparations.Shutdown();
            Logger.Dispose();
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }

    private static ProxyConfig CreateConfig()
    {
        var config = ProxyConfig.Builtin();
        config.Providers.Add(new ProviderEndpoint
        {
            Id = "a", Name = "供应商A", ClientType = ClientType.Codex, BaseUrl = "https://a.example/v1",
            Models = new ProviderModels { Model = "base-model" },
            Keys = [new ProviderKey { Id = "a1", Name = "主号", ApiKey = "sk-first" },
                new ProviderKey { Id = "a2", Name = "备用", ApiKey = "sk-second", ModelOverride = new KeyModelOverride { Model = "override-model" } }],
        });
        var route = config.RouteFor(ClientType.Codex)!;
        route.CurrentProviderId = "a";
        route.CurrentKeyId = "a1";
        return config;
    }

    [Fact]
    public void Current_target_uses_managed_key_and_base_model()
    {
        var target = PreparationCatalog.Resolve(CreateConfig(), ClientType.Codex, null);
        Assert.Equal(new PreparationKeyRef("a", "a1"), target.Key);
        Assert.Equal("sk-first", target.Credential.ApiKey);
        Assert.Equal("base-model", target.DefaultModel);
        Assert.False(target.Credential.IsChannelToken);
        Assert.DoesNotContain("sk-first", target.ToString());
    }

    [Fact]
    public void Explicit_target_keeps_requested_key_after_channel_switch()
    {
        var config = CreateConfig();
        config.RouteFor(ClientType.Codex)!.CurrentKeyId = "a2";
        var target = PreparationCatalog.Resolve(config, ClientType.Codex, new PreparationKeyRef("a", "a1"));
        Assert.Equal("sk-first", target.Credential.ApiKey);
        Assert.Equal("base-model", target.DefaultModel);
        Assert.Equal("override-model", PreparationCatalog.Resolve(config, ClientType.Codex, null).DefaultModel);
    }

    [Theory]
    [InlineData(true, true, "override-model[1M]")]
    [InlineData(true, false, "override-model")]
    [InlineData(false, true, "base-model[1M]")]
    public void Claude_preparation_uses_effective_main_model_and_context(bool hasOverride, bool context, string expected)
    {
        var config = CreateConfig();
        var provider = config.Providers[0];
        provider.ClientType = ClientType.Claude;
        provider.Models.Context1M = context;
        provider.Models.Opus.Model = "role-model-must-not-replace-main";
        provider.Keys[1].ModelOverride = hasOverride ? new KeyModelOverride { Model = "override-model", Context1M = context } : null;
        var target = PreparationCatalog.Resolve(config, ClientType.Claude, new PreparationKeyRef("a", "a2"));
        Assert.Equal(expected, target.DefaultModel);
    }

    [Fact]
    public void Empty_main_model_does_not_become_only_context_suffix()
    {
        var config = CreateConfig();
        config.Providers[0].ClientType = ClientType.Claude;
        config.Providers[0].Models.Model = "";
        config.Providers[0].Models.Context1M = true;
        Assert.Equal("", PreparationCatalog.Resolve(config, ClientType.Claude, new PreparationKeyRef("a", "a1")).DefaultModel);
    }

    [Fact]
    public void Wrong_client_and_deleted_key_are_rejected()
    {
        var config = CreateConfig();
        Assert.Throws<WorkspaceException>(() => PreparationCatalog.Resolve(config, ClientType.Claude, new PreparationKeyRef("a", "a1")));
        Assert.Throws<WorkspaceException>(() => PreparationCatalog.Resolve(config, ClientType.Codex, new PreparationKeyRef("a", "deleted")));
    }

    [Fact]
    public void Target_is_start_snapshot_not_live_reference()
    {
        var config = CreateConfig();
        var first = PreparationCatalog.Resolve(config, ClientType.Codex, null);
        config.Providers[0].BaseUrl = "https://changed.example";
        config.Providers[0].Keys[0].ApiKey = "sk-new";
        config.Providers[0].Models.Model = "new-model";
        Assert.Equal("https://a.example/v1", first.Credential.BaseUrl);
        Assert.Equal("sk-first", first.Credential.ApiKey);
        Assert.Equal("base-model", first.DefaultModel);
        Assert.Equal("sk-new", PreparationCatalog.Resolve(config, ClientType.Codex, null).Credential.ApiKey);
    }

    [Fact]
    public void Deletion_saves_proxy_and_tasks_together_then_publishes_removal()
    {
        using var fixture = new Fixture();
        var candidate = fixture.Config.Clone();
        candidate.Providers[0].Keys.RemoveAt(1);
        fixture.Catalog.SaveProxy(candidate);
        Assert.Equal(1, fixture.Saves);
        Assert.Single(fixture.SavedProxy!.Providers[0].Keys);
        Assert.Equal("task-a", Assert.Single(fixture.SavedTasks!).Id);
        Assert.Null(fixture.Preparations.Find("task-b"));
        Assert.NotNull(fixture.Preparations.Find("task-a"));
    }

    [Fact]
    public void Failed_combined_save_keeps_all_tasks_and_hides_exception_secret()
    {
        using var fixture = new Fixture { FailSave = true };
        var candidate = fixture.Config.Clone();
        candidate.Providers[0].Keys.RemoveAt(1);
        var error = Assert.Throws<WorkspaceException>(() => fixture.Catalog.SaveProxy(candidate));
        Assert.DoesNotContain("sk-secret", error.Message);
        Assert.Equal(2, fixture.Preparations.Tasks.Count);
        Assert.Null(fixture.SavedProxy);
    }

    [Fact]
    public void Deleting_provider_removes_all_its_tasks_in_one_save()
    {
        using var fixture = new Fixture();
        var candidate = fixture.Config.Clone();
        candidate.Providers.Clear();
        fixture.Catalog.SaveProxy(candidate);
        Assert.Empty(fixture.Preparations.Tasks);
        Assert.Empty(fixture.SavedTasks!);
        Assert.Equal(1, fixture.Saves);
    }

    [Fact]
    public void Ordinary_proxy_update_preserves_preparation_list()
    {
        using var fixture = new Fixture();
        var candidate = fixture.Config.Clone();
        candidate.Providers[0].Name = "新名称";
        fixture.Catalog.SaveProxy(candidate);
        Assert.Equal(2, fixture.Preparations.Tasks.Count);
        Assert.Equal(2, fixture.SavedTasks!.Count);
    }

    [Fact]
    public void Combined_shutdown_does_not_overwrite_saved_tasks_with_empty_list()
    {
        using var fixture = new Fixture();
        fixture.Preparations.Find("task-b")!.WasRunning = true;
        var workspace = new ProxyWorkspace(fixture.Logger, fixture.Config, fixture.Catalog.SaveProxy);
        fixture.Catalog.Shutdown(workspace);
        Assert.Equal(2, fixture.SavedTasks!.Count);
        Assert.False(fixture.SavedTasks.Single(task => task.Id == "task-a").WasRunning);
        Assert.True(fixture.SavedTasks.Single(task => task.Id == "task-b").WasRunning);
        Assert.Empty(fixture.Preparations.Tasks);
    }

    [Fact]
    public void Failed_proxy_save_during_shutdown_still_saves_preparation_intent_and_cleans_up()
    {
        using var fixture = new Fixture { FailSave = true };
        fixture.Preparations.Find("task-b")!.WasRunning = true;
        var workspace = new ProxyWorkspace(fixture.Logger, fixture.Config, fixture.Catalog.SaveProxy);
        fixture.Catalog.Shutdown(workspace);
        Assert.Equal(2, fixture.SavedTasks!.Count);
        Assert.True(fixture.SavedTasks.Single(task => task.Id == "task-b").WasRunning);
        Assert.Empty(fixture.Preparations.Tasks);
    }

    [Fact]
    public async Task Cancellation_skips_commit_and_releases_key_block()
    {
        using var fixture = new Fixture();
        var key = new PreparationKeyRef("a", "a1");
        var called = false;
        Assert.Equal("操作已取消", await fixture.Catalog.ChangeAsync([key], () => { called = true; return null; }, new CancellationToken(true)));
        Assert.False(called);
        Assert.Null(fixture.Preparations.RemoveChecked("task-a"));
    }

    [Fact]
    public async Task Commit_rechecks_restrictions_after_stop_and_does_not_delete_on_error()
    {
        using var fixture = new Fixture();
        var result = await fixture.Catalog.ChangeAsync([new PreparationKeyRef("a", "a1")], () => "当前 Key 不能删除，请先切换到其他 Key");
        Assert.Equal("当前 Key 不能删除，请先切换到其他 Key", result);
        Assert.Equal(2, fixture.Preparations.Tasks.Count);
        Assert.Equal(0, fixture.Saves);
    }
}
