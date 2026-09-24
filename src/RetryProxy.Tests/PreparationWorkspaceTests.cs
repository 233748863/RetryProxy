using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

[Collection("cli-environment")]
public sealed class PreparationWorkspaceTests
{
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
            Logger = ProxyLogger.Create(Directory);
            Preparations = new PreparationWorkspace(Logger)
            {
                TestCliCommand = new CliCommand(Path.Combine(Directory, "missing-client.exe")),
                LocalProviderResolver = client => CliCredential.Create("sk-local-fixture", $"https://{client.ToString().ToLowerInvariant()}.example"),
            };
        }

        public string Directory { get; }
        public ProxyLogger Logger { get; }
        public PreparationWorkspace Preparations { get; }

        public string DrainLogs()
        {
            var lines = new List<string>();
            while (Logger.UiLines!.TryRead(out var line)) { lines.Add(line); }
            return string.Join("\n", lines);
        }

        public void Dispose()
        {
            Preparations.Shutdown();
            Logger.Dispose();
            try { System.IO.Directory.Delete(Directory, true); }
            catch (IOException) { }
        }
    }

    private static PreparationDialogState Options(PrepareMode mode = PrepareMode.LocalProvider, ClientType client = ClientType.Codex) => new()
    {
        Mode = mode,
        ClientType = client,
        ProviderUrl = "https://custom.example/v1",
        ApiKey = "sk-custom-fixture",
        SelectedModel = "preparation-test-model",
    };

    private static void WaitFor(PreparationWorkspace workspace, Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            workspace.Poll();
            if (condition()) { return; }
            Assert.True(DateTime.UtcNow < deadline, string.Join("\n", workspace.Tasks.Select(task => $"{task.Title}: {task.State}, {task.LastError}")));
            Thread.Sleep(10);
        }
    }

    private static PreparationTask Start(PreparationWorkspace workspace, PreparationDialogState options)
    {
        Assert.True(workspace.SubmitPrepareDialog(options), options.Error);
        var task = workspace.Find(options.TaskId)!;
        WaitFor(workspace, () => !task.Pending);
        Assert.Equal(ServiceState.Running, task.State);
        return task;
    }

    [Theory]
    [InlineData(PrepareMode.LocalProvider, ClientType.Codex)]
    [InlineData(PrepareMode.LocalProvider, ClientType.Claude)]
    [InlineData(PrepareMode.CustomProvider, ClientType.Codex)]
    [InlineData(PrepareMode.CustomProvider, ClientType.Claude)]
    public void PreparationUsesItsOwnClientAndSettingsWithoutAnyChannels(PrepareMode mode, ClientType client)
    {
        using var fixture = new Fixture();
        var proxy = new ProxyWorkspace(fixture.Logger, new ProxyConfig(), null);
        proxy.RefreshServices();
        var original = ProxyConfigJson.ToCanonicalJson(proxy.Config);
        var options = Options(mode, client);
        options.IdleMinutes = "7.5";
        options.ReasoningEffort = ReasoningEffort.High;
        options.SelectedModel = " preparation-test-model ";
        var task = Start(fixture.Preparations, options);

        Assert.Empty(proxy.Config.Routes);
        Assert.Empty(proxy.Config.Providers);
        Assert.Equal(original, ProxyConfigJson.ToCanonicalJson(proxy.Config));
        Assert.Equal(KeepAliveFlavorExtensions.FromClientType(client), task.Snapshot!.Flavor);
        Assert.Equal(ReasoningEffort.High, task.Snapshot.ReasoningEffort);
        Assert.Equal(ReasoningEffort.High, task.ReasoningEffort);
        Assert.True(task.Snapshot.WithKey);
        Assert.False(task.Snapshot.Enabled);
        Assert.Equal("preparation-test-model", task.Snapshot.Model);
        Assert.Equal(TimeSpan.FromMinutes(7.5), task.Snapshot.Idle);
        Assert.Equal(50_000UL, task.Snapshot.ContextLimit);
        Assert.Equal(mode == PrepareMode.LocalProvider ? $"https://{client.ToString().ToLowerInvariant()}.example" : "https://custom.example/v1", task.ProviderUrl);
        Assert.True(fixture.Preparations.HintChangesOverTime());
        Assert.False(proxy.KeepAliveHintChangesOverTime());

        var saved = fixture.Preparations.OpenPrepareDialog(task.Id);
        Assert.Equal(client, saved.ClientType);
        Assert.Equal(ReasoningEffort.High, saved.ReasoningEffort);
        Assert.Equal("7.5", saved.IdleMinutes);
        Assert.Equal(mode == PrepareMode.CustomProvider ? "sk-custom-fixture" : string.Empty, saved.ApiKey);
        var runtime = PreparationWorkspace.CreateRuntimeConfig(task.ProviderUrl, task.ListenPort!.Value, 7.5, client);
        Assert.Equal(0, runtime.MaxRetries);
        Assert.Equal(ConfigDefaults.TimeoutSeconds, runtime.TimeoutSeconds);
        Assert.Equal(ConfigDefaults.GenerationTimeoutSeconds, runtime.GenerationTimeoutSeconds);
        Assert.Equal(ConfigDefaults.TotalTimeoutSeconds, runtime.TotalTimeoutSeconds);
        Assert.Empty(runtime.Routes);
        Assert.Empty(runtime.Providers);
        Assert.Null(runtime.RuntimeOverrides);
        Assert.DoesNotContain("sk-custom-fixture", fixture.DrainLogs());
    }

    [Fact]
    public void SwitchingEditingStoppingAndDeletingChannelsLeavesPreparationRunning()
    {
        using var fixture = new Fixture();
        var config = ProxyConfig.Builtin();
        foreach (var route in config.Routes) { route.DesiredRunning = false; route.KeepaliveEnabled = false; }
        var proxy = new ProxyWorkspace(fixture.Logger, config, null);
        proxy.RefreshServices();
        var task = Start(fixture.Preparations, Options());
        var service = task.Service;
        var preparationOptions = fixture.Preparations.OpenPrepareDialog(task.Id);

        foreach (var route in proxy.Config.Routes.ToList())
        {
            proxy.SelectRoute(route.Id);
            proxy.SetKeepAlive(route.Id, false, 23, 999_999, ReasoningEffort.Max);
            route.MaxRetries = 10_000;
            route.TimeoutSeconds = 1;
            proxy.RefreshServices();
        }
        proxy.StopAll();
        foreach (var route in proxy.Config.Routes.ToList()) { proxy.DeleteRoute(route.Id); }
        foreach (var provider in proxy.Config.Providers.ToList())
        {
            proxy.DeleteProvider(proxy.Config.Providers.FindIndex(candidate => candidate.Name == provider.Name));
        }
        proxy.RefreshServices();
        fixture.Preparations.Poll();

        Assert.Empty(proxy.Config.Routes);
        Assert.Same(task, Assert.Single(fixture.Preparations.Tasks));
        Assert.Same(service, task.Service);
        Assert.Equal(ServiceState.Running, task.State);
        Assert.Equal(TimeSpan.FromMinutes(5), task.Snapshot!.Idle);
        Assert.Equal(50_000UL, task.Snapshot.ContextLimit);
        Assert.Equal(ReasoningEffort.Default, task.Snapshot.ReasoningEffort);
        Assert.Equal(preparationOptions.SelectedModel, fixture.Preparations.OpenPrepareDialog(task.Id).SelectedModel);
        fixture.Preparations.Stop(task.Id);
        WaitFor(fixture.Preparations, () => task.State == ServiceState.Stopped);
        Assert.False(fixture.Preparations.HintChangesOverTime());
    }

    [Fact]
    public void MultipleTasksForTheSameProviderStopAndRestartIndependently()
    {
        using var fixture = new Fixture();
        var first = Start(fixture.Preparations, Options(PrepareMode.CustomProvider));
        var secondOptions = Options(PrepareMode.CustomProvider, ClientType.Claude);
        secondOptions.SelectedModel = "another-model";
        var second = Start(fixture.Preparations, secondOptions);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.ListenPort, second.ListenPort);
        Assert.NotSame(first.Service, second.Service);
        var secondService = second.Service;

        fixture.Preparations.Stop(first.Id);
        WaitFor(fixture.Preparations, () => first.State == ServiceState.Stopped);
        Assert.Equal(ServiceState.Running, second.State);
        Assert.Equal("another-model", second.Snapshot!.Model);
        Assert.Same(secondService, second.Service);
        fixture.Preparations.Start(first.Id);
        WaitFor(fixture.Preparations, () => !first.Pending);
        Assert.Equal(ServiceState.Running, first.State);
        Assert.Equal(2, fixture.Preparations.Tasks.Count);
        fixture.Preparations.Remove(second.Id);
        Assert.Equal(2, fixture.Preparations.Tasks.Count);
        fixture.Preparations.Stop(first.Id);
        WaitFor(fixture.Preparations, () => first.CanStart);
        fixture.Preparations.Remove(first.Id);
        Assert.Same(second, Assert.Single(fixture.Preparations.Tasks));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.49")]
    [InlineData("1441")]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    [InlineData("abc")]
    public void InvalidIntervalDoesNotCreateATask(string value)
    {
        using var fixture = new Fixture();
        var options = Options();
        options.IdleMinutes = value;
        Assert.False(fixture.Preparations.SubmitPrepareDialog(options));
        Assert.Equal("独立保活间隔请输入 0.5～1440 分钟", options.Error);
        Assert.Empty(fixture.Preparations.Tasks);
    }

    [Fact]
    public void MissingModelOrInvalidCredentialsDoNotCreateATask()
    {
        using var fixture = new Fixture();
        var options = Options(PrepareMode.CustomProvider);
        options.SelectedModel = " ";
        Assert.False(fixture.Preparations.SubmitPrepareDialog(options));
        Assert.Contains("模型", options.Error);
        options.SelectedModel = "test-model";
        options.ProviderUrl = "invalid URL";
        Assert.False(fixture.Preparations.SubmitPrepareDialog(options));
        options.ProviderUrl = "https://custom.example";
        options.ApiKey = "";
        Assert.False(fixture.Preparations.SubmitPrepareDialog(options));
        options.Mode = PrepareMode.LocalProvider;
        fixture.Preparations.LocalProviderResolver = _ => throw new WorkspaceException("本机配置不存在");
        Assert.False(fixture.Preparations.SubmitPrepareDialog(options));
        Assert.Equal("本机配置不存在", options.Error);
        Assert.Empty(fixture.Preparations.Tasks);
    }

    [Fact]
    public void EditingADraftOrSubmittingInvalidChangesPreservesTheSavedTask()
    {
        using var fixture = new Fixture();
        var task = Start(fixture.Preparations, Options());
        var originalService = task.Service;
        var draft = fixture.Preparations.OpenPrepareDialog(task.Id);
        draft.ClientType = ClientType.Claude;
        draft.SelectedModel = "changed";
        Assert.False(fixture.Preparations.SubmitPrepareDialog(draft));
        Assert.Same(originalService, task.Service);
        Assert.Equal(ClientType.Codex, task.ClientType);
        fixture.Preparations.Stop(task.Id);
        WaitFor(fixture.Preparations, () => task.CanStart);
        draft.IdleMinutes = "0";
        Assert.False(fixture.Preparations.SubmitPrepareDialog(draft));
        Assert.Equal("preparation-test-model", task.Model);
        draft.IdleMinutes = "12";
        Start(fixture.Preparations, draft);
        Assert.Same(task, Assert.Single(fixture.Preparations.Tasks));
        Assert.Equal(ClientType.Claude, task.ClientType);
        Assert.Equal("changed", task.Model);
        Assert.Equal(TimeSpan.FromMinutes(12), task.Snapshot!.Idle);
        var next = fixture.Preparations.OpenPrepareDialog();
        Assert.NotEqual(task.Id, next.TaskId);
        Assert.Equal(ClientType.Codex, next.ClientType);
        Assert.Null(next.SelectedModel);
        Assert.Empty(next.ApiKey);
    }

    [Fact]
    public void LocalProviderChangesApplyWhenStartingAgainAndDoNotRetargetARunningTask()
    {
        using var fixture = new Fixture();
        var address = "https://first.example";
        fixture.Preparations.LocalProviderResolver = _ => CliCredential.Create("sk-local-fixture", address);
        var task = Start(fixture.Preparations, Options());
        address = "https://second.example";
        fixture.Preparations.Poll();
        Assert.Equal("https://first.example", task.ProviderUrl);
        fixture.Preparations.Stop(task.Id);
        WaitFor(fixture.Preparations, () => task.CanStart);
        fixture.Preparations.Start(task.Id);
        WaitFor(fixture.Preparations, () => !task.Pending);
        Assert.Equal("https://second.example", task.ProviderUrl);
    }

    [Fact]
    public void FailuresKeepRetryingUntilStoppedAndShutdownClearsAllTemporaryState()
    {
        using var fixture = new Fixture();
        var task = Start(fixture.Preparations, Options());
        WaitFor(fixture.Preparations, () => task.Snapshot!.PreparationLastError is not null);
        Assert.True(task.IsPreparing);
        Assert.True(task.Snapshot!.PreparationAttempts > 0);
        Assert.NotNull(task.Snapshot.PreparationRetryAfter);
        Assert.False(task.Snapshot.Enabled);
        var service = task.Service!;
        var port = task.ListenPort!.Value;
        fixture.Preparations.Shutdown();
        Assert.Equal(ServiceState.Stopped, service.State);
        Assert.Empty(fixture.Preparations.Tasks);
        Assert.False(fixture.Preparations.HintChangesOverTime());
        Assert.Null(fixture.Preparations.OpenPrepareDialog().SelectedModel);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Assert.DoesNotContain("sk-local-fixture", fixture.DrainLogs());
    }

    [Fact]
    public async Task PreparationUsesItsOwnProxyThenKeepsTheSessionAlive()
    {
        using var fixture = new Fixture();
        var requests = 0;
        await using var upstream = await FakeUpstream.StartAsync(async context =>
        {
            Assert.Equal("Bearer sk-custom-fixture", context.Request.Headers.Authorization.ToString());
            var body = await Upstream.ReadBody(context);
            Assert.DoesNotContain("retry_proxy_keepalive", body);
            Interlocked.Increment(ref requests);
            await Upstream.EventStream(context, "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n");
        });
        var command = new CliCommand("powershell.exe");
        command.Arguments.AddRange(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "fixtures", "preparation-codex.ps1") });
        fixture.Preparations.TestCliCommand = command;
        var notices = new List<string>();
        fixture.Preparations.NoticePosted += notices.Add;
        var options = Options(PrepareMode.CustomProvider);
        options.ProviderUrl = upstream.BaseUrl;
        var task = Start(fixture.Preparations, options);
        WaitFor(fixture.Preparations, () => task.Snapshot!.Enabled);
        Assert.False(task.IsPreparing);
        Assert.Equal(1UL, task.Snapshot!.Totals.Completed);
        Assert.Equal(52UL, task.Snapshot.ContextTokens);
        Assert.Contains("准备完成", Assert.Single(notices));
        var session = task.Snapshot.SessionId;
        task.Service!.KeepAlive.MakeDueForTest();
        WaitFor(fixture.Preparations, () => task.Snapshot!.Totals.Completed == 2);
        Assert.Equal(session, task.Snapshot!.SessionId);
        Assert.Equal(2, Volatile.Read(ref requests));
        Assert.Equal(0UL, task.Service.Metrics.Snapshot().TotalRequests);
        fixture.Preparations.Stop(task.Id);
        WaitFor(fixture.Preparations, () => task.State == ServiceState.Stopped);
        Assert.False(task.Snapshot.Enabled);
        var logs = fixture.DrainLogs();
        Assert.Contains("[准备]", logs);
        Assert.Contains("[保活]", logs);
        Assert.DoesNotContain("sk-custom-fixture", logs);
    }
}
