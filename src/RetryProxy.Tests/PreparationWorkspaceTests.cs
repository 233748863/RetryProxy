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
        private readonly List<PreparationWorkspace> _opened = new();

        public Fixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
            Logger = ProxyLogger.Create(Directory);
            Preparations = Open(null);
        }

        public string Directory { get; }
        public ProxyLogger Logger { get; }
        public PreparationWorkspace Preparations { get; }

        /// <summary>最近一次写回配置的任务设置。</summary>
        public List<SavedPreparation> Saved { get; private set; } = new();

        /// <summary>按已保存的设置新建工作区（模拟重启软件），与首个工作区共用日志和测试客户端。</summary>
        public PreparationWorkspace Open(IEnumerable<SavedPreparation?>? saved)
        {
            var workspace = new PreparationWorkspace(Logger, saved, list => Saved = list)
            {
                TestCliCommand = new CliCommand(Path.Combine(Directory, "missing-client.exe")),
                LocalProviderResolver = client => CliCredential.Create("sk-local-fixture", $"https://{client.ToString().ToLowerInvariant()}.example"),
            };
            _opened.Add(workspace);
            return workspace;
        }

        public string DrainLogs()
        {
            var lines = new List<string>();
            while (Logger.UiLines!.TryRead(out var line)) { lines.Add(line); }
            return string.Join("\n", lines);
        }

        public void Dispose()
        {
            foreach (var workspace in _opened) { workspace.Shutdown(); }
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
    [InlineData(ClientType.Codex)]
    [InlineData(ClientType.Claude)]
    public void CurrentProviderResolverIsReadAgainWithoutOpeningClientFiles(ClientType client)
    {
        using var fixture = new Fixture();
        var key = "test-key-a";
        var calls = 0;
        var workspace = new PreparationWorkspace(fixture.Logger, currentProviderResolver: selected =>
        {
            Assert.Equal(client, selected);
            calls++;
            return CliCredential.Create(key, "https://current.example", authMode: ClaudeAuthMode.ApiKey);
        });
        try
        {
            var options = Options(client: client);
            Assert.Equal("test-key-a", workspace.ResolveCredential(options).ApiKey);
            key = "test-key-b";
            var credential = workspace.ResolveCredential(options);
            Assert.Equal("test-key-b", credential.ApiKey);
            Assert.Equal(ClaudeAuthMode.ApiKey, credential.AuthMode);
            Assert.Equal(2, calls);
            Assert.Equal("sk-custom-fixture", workspace.ResolveCredential(Options(PrepareMode.CustomProvider, client)).ApiKey);
            Assert.Equal(2, calls);
        }
        finally { workspace.Shutdown(); }
    }

    [Fact]
    public void CustomProviderUsesOnlyTheSuppliedAddressAndKey()
    {
        using var fixture = new Fixture();
        fixture.Preparations.LocalProviderResolver = _ => throw new InvalidOperationException("不应读取本机供应商");
        var credential = fixture.Preparations.ResolveCredential(Options(PrepareMode.CustomProvider, ClientType.Claude));

        Assert.Equal("https://custom.example/v1", credential.BaseUrl);
        Assert.Equal("sk-custom-fixture", credential.ApiKey);
        Assert.Equal(ClaudeAuthMode.Bearer, credential.AuthMode);
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

    [Theory]
    [InlineData(ClientType.Claude, true)]
    [InlineData(ClientType.Codex, false)]
    public void ClaudePreparationUsesFingerprintByDefault(ClientType client, bool expected)
    {
        // 测试进程的全局指纹库不抓取本机 Claude Code（见 TestFingerprintStore），这里只看后台代理是否用了指纹。
        using var fixture = new Fixture();
        var task = Start(fixture.Preparations, Options(PrepareMode.CustomProvider, client));
        // 后台代理启动后才会提交准备，准备因客户端缺失而报错时启动日志一定已经写完。
        WaitFor(fixture.Preparations, () => task.LastError is not null);

        Assert.Equal(expected, fixture.DrainLogs().Contains("上游握手使用 Claude Code TLS 指纹", StringComparison.Ordinal));
    }

    [Fact]
    public void SwitchingEditingAndStoppingChannelsLeavesPreparationRunning()
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
        proxy.RefreshServices();
        fixture.Preparations.Poll();

        Assert.Equal(2, proxy.Config.Routes.Count);
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
        Assert.Equal("无法读取准备目标，请检查供应商与 Key 设置", options.Error);
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
    public void RoutinePreparationTimeoutDoesNotAppearAsAnError()
    {
        using var fixture = new Fixture();
        var watchdog = new KeepAliveWatchdog(false, TimeSpan.FromMinutes(5));
        using var registration = watchdog.RegisterService(KeepAliveFlavor.Codex);
        var task = new PreparationTask(Options(), 1)
        {
            Service = new ProxyService(fixture.Logger, "prepare").WithKeepAliveWatchdog(watchdog),
        };
        Assert.True(watchdog.RequestPreparation());
        using (var probe = watchdog.BeginDueProbe()!)
        {
            probe.Fail("CLI 本轮执行超过 630 秒，已终止并清理会话", timedOut: true);
        }
        Assert.True(watchdog.Snapshot().PreparationLastErrorIsTimeout);
        Assert.Null(task.LastError);

        watchdog.SetPreparationRetryNowForTest();
        using (var probe = watchdog.BeginDueProbe()!)
        {
            probe.Fail("供应商拒绝访问");
        }
        Assert.False(watchdog.Snapshot().PreparationLastErrorIsTimeout);
        Assert.Equal("供应商拒绝访问", task.LastError);
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
            var requestNumber = Interlocked.Increment(ref requests);
            var response = requestNumber == 1
                ? "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":40,\"output_tokens\":12},\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"准备成功\"}]}]}}\n\n"
                : "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n";
            await Upstream.EventStream(context, response);
        });
        var command = new CliCommand("powershell.exe");
        command.Arguments.AddRange(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "fixtures", "preparation-codex.ps1") });
        fixture.Preparations.TestCliCommand = command;
        var notices = new List<string>();
        fixture.Preparations.NoticePosted += notices.Add;
        var options = Options(PrepareMode.CustomProvider);
        options.ProviderUrl = upstream.BaseUrl;
        var task = Start(fixture.Preparations, options);
        WaitFor(fixture.Preparations, () => task.Snapshot!.Enabled && !task.IsPreparing);
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
        var lines = logs.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line =>
        {
            Assert.Contains($"[一键准备][{task.Title}]", line);
            Assert.True(LogLine.Matches(line, LogLevelFilter.All, string.Empty, null, LogSource.Preparation));
            Assert.False(LogLine.Matches(line, LogLevelFilter.All, string.Empty, null, LogSource.ChannelKeepAlive));
        });
        Assert.Contains(lines, line => line.Contains("[准备][会话 ") && line.Contains("CLI 完成，上下文 52/50000 token"));
        Assert.Contains(lines, line => line.Contains("[独立保活][会话 ") && line.Contains("CLI 完成，上下文 "));
        // 一次上游请求只在代理的请求行记用量；本轮结论不重复，转入保活也只由“准备完成”一行说明。
        Assert.Contains("[准备][请求 ", Assert.Single(lines, line => line.Contains("输入/输出 40/12 token")));
        Assert.Contains("准备完成，已开始独立保活", Assert.Single(lines, line => line.Contains("准备完成") || line.Contains("转为")));
        Assert.Contains(lines, line => line.Contains("[准备][请求 "));
        Assert.Contains(lines, line => line.Contains("[独立保活][请求 "));
        Assert.DoesNotContain("[保活-", logs);
        Assert.DoesNotContain("sk-custom-fixture", logs);
    }

    [Fact]
    public void SavedSettingsComeBackAsStoppedTasksAfterRestart()
    {
        using var fixture = new Fixture();
        var customOptions = Options(PrepareMode.CustomProvider, ClientType.Claude);
        customOptions.IdleMinutes = "7.50";
        customOptions.ReasoningEffort = ReasoningEffort.Max;
        var custom = Start(fixture.Preparations, customOptions);
        var local = Start(fixture.Preparations, Options());
        var removed = Start(fixture.Preparations, Options());
        fixture.Preparations.Stop(removed.Id);
        WaitFor(fixture.Preparations, () => removed.CanStart);
        fixture.Preparations.Remove(removed.Id);

        // 手填供应商连同 Key 一起保存；本机供应商只留上次读到的地址用于显示，不保存本机密钥。
        Assert.Equal(2, fixture.Saved.Count);
        var savedCustom = fixture.Saved[0];
        Assert.Equal((custom.Id, 1, "claude", SavedPreparation.CustomSource), (savedCustom.Id, savedCustom.Number, savedCustom.ClientType, savedCustom.ProviderSource));
        Assert.Equal(("https://custom.example/v1", "sk-custom-fixture"), (savedCustom.ProviderUrl, savedCustom.ApiKey));
        Assert.Equal(("preparation-test-model", "max", "7.5"), (savedCustom.Model, savedCustom.ReasoningEffort, savedCustom.IdleMinutes));
        var savedLocal = fixture.Saved[1];
        Assert.Equal((local.Id, 2, "codex", SavedPreparation.CurrentSource), (savedLocal.Id, savedLocal.Number, savedLocal.ClientType, savedLocal.ProviderSource));
        Assert.Equal(("https://codex.example", string.Empty), (savedLocal.ProviderUrl, savedLocal.ApiKey));
        Assert.Equal(("preparation-test-model", "default", "5"), (savedLocal.Model, savedLocal.ReasoningEffort, savedLocal.IdleMinutes));

        // 退出只结束服务，不能把已保存的任务写成空列表。
        fixture.Preparations.Shutdown();
        Assert.Equal(new[] { custom.Id, local.Id }, fixture.Saved.Select(item => item.Id));

        var restarted = fixture.Open(fixture.Saved);
        Assert.Equal(new[] { custom.Id, local.Id }, restarted.Tasks.OrderBy(task => task.Number).Select(task => task.Id));
        var restored = restarted.Find(custom.Id)!;
        Assert.Equal("准备 1 · Claude Code", restored.Title);
        Assert.Equal((ServiceState.Stopped, "已停止", "可按当前设置重新开始"), (restored.State, restored.Status, restored.Hint));
        Assert.True(restored.CanStart);
        Assert.Null(restored.ListenPort);
        Assert.Null(restored.LastError);
        Assert.Equal("https://custom.example/v1", restored.ProviderUrl);
        Assert.Equal(("preparation-test-model", ReasoningEffort.Max, "7.5"), (restored.Model, restored.ReasoningEffort, restored.IdleMinutes));
        var dialog = restarted.OpenPrepareDialog(custom.Id);
        Assert.Equal((PrepareMode.CustomProvider, ClientType.Claude), (dialog.Mode, dialog.ClientType));
        Assert.Equal(("https://custom.example/v1", "sk-custom-fixture"), (dialog.ProviderUrl, dialog.ApiKey));
        Assert.Equal("https://codex.example", restarted.Find(local.Id)!.ProviderUrl);
        var localDialog = restarted.OpenPrepareDialog(local.Id);
        Assert.Equal((PrepareMode.LocalProvider, string.Empty, string.Empty), (localDialog.Mode, localDialog.ProviderUrl, localDialog.ApiKey));
        Assert.False(restarted.HintChangesOverTime());

        // 恢复的任务按保存的设置直接开始；新任务的编号接在已有任务之后。
        restarted.Start(custom.Id);
        WaitFor(restarted, () => !restored.Pending);
        Assert.Equal(ServiceState.Running, restored.State);
        Assert.Equal(ReasoningEffort.Max, restored.Snapshot!.ReasoningEffort);
        Assert.Equal(TimeSpan.FromMinutes(7.5), restored.Snapshot.Idle);
        Assert.Equal("准备 3 · Codex", Start(restarted, Options()).Title);
        Assert.Equal(new[] { 1, 2, 3 }, fixture.Saved.Select(item => item.Number));
        Assert.DoesNotContain("sk-custom-fixture", fixture.DrainLogs());
    }

    [Fact]
    public void UnrecognizedSavedEntriesAreSkippedAndConflictsAreRepaired()
    {
        using var fixture = new Fixture();
        var restarted = fixture.Open(new SavedPreparation?[]
        {
            // 旧版的 local 按"跟随当前"恢复。
            new() { Id = "first", Number = 2, ClientType = "codex", ProviderSource = SavedPreparation.LegacyLocalSource, Model = "m", ReasoningEffort = "ultra", IdleMinutes = "5" },
            new() { Id = "first", Number = 2, ClientType = "claude", ProviderSource = SavedPreparation.CustomSource, ProviderUrl = "https://custom.example", ApiKey = "sk-custom-fixture", Model = "m", ReasoningEffort = "ultra", IdleMinutes = "5" },
            new() { Id = "unknown-client", Number = 1, ClientType = "gemini", ProviderSource = SavedPreparation.CurrentSource },
            new() { Id = "unknown-source", Number = 1, ClientType = "codex", ProviderSource = "remote" },
            null,
        });

        var tasks = restarted.Tasks.OrderBy(task => task.Number).ToList();
        Assert.Equal(2, tasks.Count);
        Assert.Equal(("first", "准备 2 · Codex", ReasoningEffort.Ultra), (tasks[0].Id, tasks[0].Title, tasks[0].ReasoningEffort));
        // 重复的 ID 换新、重复的编号顺延；Claude 不支持 ultra，回到默认。
        Assert.NotEqual("first", tasks[1].Id);
        Assert.Equal(("准备 3 · Claude Code", ReasoningEffort.Default), (tasks[1].Title, tasks[1].ReasoningEffort));
        Assert.Equal("sk-custom-fixture", restarted.OpenPrepareDialog(tasks[1].Id).ApiKey);
        Assert.Contains("一键准备有 3 项已保存的设置无法识别，已跳过", fixture.DrainLogs());
        // 恢复本身不写配置，等用户下次改动任务时才整体写回。
        Assert.Empty(fixture.Saved);
    }
}
