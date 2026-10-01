using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

[Collection("cli-environment")]
public sealed class ManagedPreparationTests
{
    private static readonly PreparationKeyRef A = new("provider", "a");
    private static readonly PreparationKeyRef B = new("provider", "b");
    private static readonly PreparationKeyRef C = new("provider", "c");

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 15, 59, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test-local", TimeSpan.FromHours(8), "test-local", "test-local");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<PreparationWorkspace> _opened = new();
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        public readonly Dictionary<PreparationKeyRef, PreparationTarget> Targets = new();
        public readonly Clock Clock = new();
        public readonly ProxyLogger Logger;
        public PreparationKeyRef Current = A;
        public List<SavedPreparation> Saved = new();
        public List<string> Notices = new();
        public List<PreparationKeyRef?> Resolved = new();
        public bool SaveFails;
        public int SaveCalls;
        public Action<List<SavedPreparation>>? BeforeSave;
        public Func<ClientType, PreparationKeyRef?, PreparationTarget>? ResolveOverride;

        public Fixture()
        {
            Logger = ProxyLogger.Create(Directory);
            Put(A); Put(B); Put(C);
        }

        public void Put(PreparationKeyRef key, string model = "default-model", string? url = null, ClientType client = ClientType.Codex) => Targets[key] = new PreparationTarget
        {
            ClientType = client, Key = key, ProviderName = "测试供应商", KeyName = "测试 " + key.KeyId,
            DefaultModel = model,
            Credential = CliCredential.Create("managed-secret-" + key.KeyId, url ?? "https://managed.example", authMode: ClaudeAuthMode.ApiKey),
        };

        public PreparationWorkspace Open(IEnumerable<SavedPreparation?>? saved = null)
        {
            var workspace = new PreparationWorkspace(Logger, saved, list =>
            {
                SaveCalls++;
                BeforeSave?.Invoke(list);
                if (SaveFails) throw new IOException("callback secret: managed-secret-a");
                Saved = list;
            }, targetResolver: (client, key) =>
            {
                Resolved.Add(key);
                return ResolveOverride is { } resolve ? resolve(client, key) : Targets[key ?? Current];
            }, timeProvider: Clock)
            {
                TestCliCommand = new CliCommand(Path.Combine(Directory, "fake-missing-cli.exe")),
                LocalProviderResolver = _ => throw new InvalidOperationException("不得读取真实客户端配置"),
            };
            workspace.NoticePosted += Notices.Add;
            _opened.Add(workspace);
            return workspace;
        }

        public void Dispose()
        {
            BeforeSave = null;
            SaveFails = false;
            foreach (var workspace in _opened) workspace.Shutdown();
            Logger.Dispose();
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
        }
    }

    private static PreparationDialogState List(params PreparationKeyRef[] keys) => new()
    {
        Mode = PrepareMode.ListProvider,
        ClientType = ClientType.Codex,
        ProviderId = keys.FirstOrDefault().ProviderId ?? string.Empty,
        KeyIds = keys.Select(key => key.KeyId).ToList(),
    };

    private static SavedPreparation Saved(string id, PreparationKeyRef key, string source = SavedPreparation.ListSource, bool running = false) => new()
    {
        Id = id, Number = id == "one" ? 1 : 2, ClientType = "codex", ProviderSource = source,
        ProviderId = key.ProviderId, KeyId = key.KeyId, ProviderName = "原供应商", KeyName = "原 Key",
        IdleMinutes = "5", ReasoningEffort = "default", WasRunning = running,
    };

    private static void WaitFor(PreparationWorkspace workspace, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            workspace.Poll();
            if (done()) return;
            Assert.True(DateTime.UtcNow < deadline, string.Join("\n", workspace.Tasks.Select(task => $"{task.Id}: {task.State} {task.LastError}")));
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

    private static void Stop(PreparationWorkspace workspace, PreparationTask task)
    {
        Assert.Null(workspace.StopChecked(task.Id));
        WaitFor(workspace, () => task.CanStart);
    }

    [Fact]
    public void BatchSavesEveryIntentBeforePublishingOrStartingAndDoesNotCopyManagedSecrets()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open();
        var observedInitialSave = false;
        fixture.BeforeSave = saved =>
        {
            if (observedInitialSave) return;
            observedInitialSave = true;
            Assert.Empty(workspace.Tasks);
            Assert.Equal(3, saved.Count);
            Assert.All(saved, entry => { Assert.True(entry.WasRunning); Assert.Empty(entry.ApiKey); Assert.Empty(entry.Model); });
        };
        var options = List(A, B, C);
        Assert.True(workspace.SubmitPrepareDialog(options), options.Error);
        fixture.BeforeSave = null;
        Assert.True(observedInitialSave);
        Assert.Equal(3, options.StartedCount);
        Assert.Equal(3, workspace.Tasks.Count);
        Assert.Equal(new[] { 1, 2, 3 }, workspace.Tasks.Select(task => task.Number));
        Assert.Equal(new[] { A, B, C }, workspace.Tasks.Select(task => new PreparationKeyRef(task.ProviderId, task.KeyId)));
        Assert.All(workspace.Tasks, task =>
        {
            Assert.Equal(PrepareMode.ListProvider, task.Mode);
            Assert.Equal("default-model", task.Model);
            Assert.Equal("测试供应商", task.ProviderName);
            Assert.True(task.WasRunning);
        });
        Assert.DoesNotContain("managed-secret-", JsonSerializer.Serialize(workspace.ExportSaved()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("existing")]
    [InlineData("save")]
    public void BatchFailureStartsNothingAndKeepsExistingObjects(string failure)
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", C) });
        var original = workspace.Find("one")!;
        var options = List(A, B);
        if (failure == "missing") fixture.Targets.Remove(B);
        if (failure == "duplicate") options.KeyIds.Add(A.KeyId);
        if (failure == "existing") options.KeyIds.Add(C.KeyId);
        fixture.SaveFails = failure == "save";
        Assert.False(workspace.SubmitPrepareDialog(options));
        Assert.Equal(0, options.StartedCount);
        Assert.Same(original, Assert.Single(workspace.Tasks));
        Assert.Null(original.Service);
        Assert.False(original.WasRunning);
        Assert.DoesNotContain("managed-secret-", options.Error);
        Assert.Equal(failure == "save" ? 1 : 0, fixture.SaveCalls);
    }

    [Fact]
    public void StoppedBindingIsUniqueAndCurrentRetargetRejectsConflictsWithoutDroppingTasks()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A, SavedPreparation.CurrentSource), Saved("two", B) });
        var current = workspace.Find("one")!;
        fixture.Current = B;
        var options = workspace.OpenPrepareDialog(current.Id);
        Assert.False(workspace.SubmitPrepareDialog(options));
        Assert.Contains("已有", options.Error);
        Assert.Equal(A, current.Binding);
        Assert.Equal(2, workspace.Tasks.Count);
        Assert.Same(current, workspace.FindForKey(A));
        Assert.False(workspace.SubmitPrepareDialog(List(A)));
        Assert.Equal(0, fixture.SaveCalls);
    }

    [Fact]
    public void CurrentRebindsOnlyAtStartAndModelsStayDynamicUnlessOverridden()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open();
        var options = new PreparationDialogState();
        var task = Start(workspace, options);
        Assert.Equal(A, task.Binding);
        Assert.Null(fixture.Resolved.Last());
        fixture.Current = B;
        fixture.Put(B, "new-default", "https://changed.example");
        workspace.Poll();
        Assert.Equal(A, task.Binding);
        Assert.Equal("default-model", task.Model);
        Stop(workspace, task);
        workspace.Start(task.Id);
        WaitFor(workspace, () => !task.Pending);
        Assert.Equal(B, task.Binding);
        Assert.Equal("new-default", task.Model);
        Assert.Equal("https://changed.example", task.ProviderUrl);
        Assert.Null(workspace.FindForKey(A));
        Assert.Same(task, workspace.FindForKey(B));
        Assert.Empty(Assert.Single(workspace.ExportSaved()).Model);
        Stop(workspace, task);
        options = workspace.OpenPrepareDialog(task.Id);
        options.SelectedModel = " manual-override ";
        Start(workspace, options);
        Assert.Equal("manual-override", task.Model);
        Assert.Equal("manual-override", Assert.Single(workspace.ExportSaved()).Model);
        Assert.Equal(3, fixture.Resolved.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fixed")]
    public void ListUsesExplicitTargetAndRefreshesCredentialsAndDefaultOnRestart(string model)
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open();
        var options = List(A);
        options.SelectedModel = model;
        var task = Start(workspace, options);
        fixture.Put(A, "second-model", "https://second.example");
        fixture.Current = C;
        Assert.Equal("https://managed.example", task.ProviderUrl);
        Stop(workspace, task);
        workspace.Start(task.Id);
        WaitFor(workspace, () => !task.Pending);
        Assert.Equal(A, fixture.Resolved.Last());
        Assert.Equal(A, task.Binding);
        Assert.Equal(model.Length == 0 ? "second-model" : model, task.Model);
        Assert.Equal(model, Assert.Single(workspace.ExportSaved()).Model);
        Assert.Equal("https://second.example", task.ProviderUrl);
    }

    [Fact]
    public void MissingDefaultRequiresExplicitModelAndResolverNeverFallsBackToClientFiles()
    {
        using var fixture = new Fixture();
        fixture.Put(A, "");
        var workspace = fixture.Open();
        var options = List(A);
        Assert.False(workspace.SubmitPrepareDialog(options));
        Assert.Contains("模型", options.Error);
        options.SelectedModel = "override";
        var task = Start(workspace, options);
        Assert.Equal("override", task.Model);
        Assert.DoesNotContain(fixture.Notices, notice => notice.Contains("真实客户端"));
    }

    [Fact]
    public void CopyPreservesTaskIdAndSeparatesSelectionsAndFetchedModels()
    {
        var original = List(A, B);
        original.Models = new[] { "fetched" };
        var copy = original.Copy();
        Assert.Equal(original.TaskId, copy.TaskId);
        copy.KeyIds.Clear();
        Assert.Equal(2, original.KeyIds.Count);
        Assert.NotSame(original.Models, copy.Models);
        Assert.Null(copy.SelectedModel);
        Assert.Equal(0, copy.StartedCount);
    }

    [Fact]
    public void EditingOneTaskUsesKeyIdAndSaveFailureDoesNotPublishOptions()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A) });
        var task = workspace.Find("one")!;
        var options = workspace.OpenPrepareDialog(task.Id);
        options.KeyId = B.KeyId;
        options.KeyIds = new() { C.KeyId };
        options.SelectedModel = "changed";
        fixture.SaveFails = true;
        Assert.False(workspace.SubmitPrepareDialog(options));
        Assert.Equal(A, task.Binding);
        Assert.Empty(task.Model);
        Assert.Null(task.Service);
        fixture.SaveFails = false;
        Start(workspace, options);
        Assert.Same(task, Assert.Single(workspace.Tasks));
        Assert.Equal(B, task.Binding);
        Assert.Equal(1, options.StartedCount);
    }

    [Fact]
    public void ShortcutReusesStoppedSettingsSkipsRunningAndDefaultsNewTasks()
    {
        using var fixture = new Fixture();
        var entry = Saved("one", A);
        entry.Model = "saved-override";
        entry.IdleMinutes = "7.5";
        entry.ReasoningEffort = "high";
        var workspace = fixture.Open(new[] { entry });
        var task = workspace.Find("one")!;
        Assert.Null(workspace.PrepareKeys(ClientType.Codex, new[] { A, B }, out var count));
        Assert.Equal(2, count);
        WaitFor(workspace, () => workspace.Tasks.All(item => !item.Pending));
        Assert.Same(task, workspace.FindForKey(A));
        Assert.Equal(("saved-override", "7.5", ReasoningEffort.High), (task.Model, task.IdleMinutes, task.ReasoningEffort));
        var second = workspace.FindForKey(B)!;
        Assert.Equal(("default-model", "5", ReasoningEffort.Default), (second.Model, second.IdleMinutes, second.ReasoningEffort));
        var calls = fixture.SaveCalls;
        Assert.Null(workspace.PrepareKeys(ClientType.Codex, new[] { A, B }, out count));
        Assert.Equal(0, count);
        Assert.Equal(calls, fixture.SaveCalls);
    }

    [Fact]
    public void RestoreDoesNotStartAndResumeIsIdempotentWithIndividualFailuresIsolated()
    {
        using var fixture = new Fixture();
        var legacy = Saved("legacy", default, SavedPreparation.LegacyLocalSource, true);
        var workspace = fixture.Open(new[] { Saved("one", A, running: true), Saved("missing", C, running: true), legacy, Saved("manual-stop", B) });
        fixture.Targets.Remove(C);
        Assert.Empty(fixture.Resolved);
        Assert.All(workspace.Tasks, task => Assert.Null(task.Service));
        Assert.Equal(0, fixture.SaveCalls);
        // 第一个任务占据 A，旧无绑定 current 不丢弃；恢复时给出冲突，WasRunning 改为 false。
        workspace.ResumeRunning();
        Assert.Equal(4, workspace.Tasks.Count);
        WaitFor(workspace, () => workspace.Find("one") is { Pending: false });
        Assert.True(workspace.Find("one")!.CanStop);
        Assert.False(workspace.Find("missing")!.WasRunning);
        Assert.False(workspace.Find("legacy")!.WasRunning);
        Assert.Contains("已有", workspace.Find("legacy")!.LastError);
        Assert.Null(workspace.Find("manual-stop")!.Service);
        var calls = fixture.Resolved.Count;
        workspace.ResumeRunning();
        Assert.Equal(calls, fixture.Resolved.Count);
        Assert.False(workspace.ExportSaved().Single(entry => entry.Id == "missing").WasRunning);
    }

    [Fact]
    public void LegacyUnboundCurrentIsKeptAndBindsOnItsFirstStart()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", default, SavedPreparation.LegacyLocalSource, true) });
        Assert.Single(workspace.Tasks);
        Assert.Null(workspace.FindForKey(A));
        Assert.Single(workspace.ExportSaved(new[] { default(PreparationKeyRef) }));
        workspace.RemoveForKeysAfterCommit(new[] { default(PreparationKeyRef) });
        Assert.Single(workspace.Tasks);
        workspace.ResumeRunning();
        var task = workspace.Find("one")!;
        WaitFor(workspace, () => !task.Pending);
        Assert.Same(task, workspace.FindForKey(A));
        Assert.Equal(SavedPreparation.CurrentSource, Assert.Single(workspace.ExportSaved()).ProviderSource);
    }

    [Fact]
    public void ManualStopSavesFirstAndSaveFailureKeepsServiceRunning()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open();
        var task = Start(workspace, List(A));
        fixture.SaveFails = true;
        var service = task.Service;
        Assert.Equal("准备任务保存失败，请检查配置文件后重试", workspace.StopChecked(task.Id));
        Assert.Same(service, task.Service);
        Assert.True(task.CanStop);
        Assert.True(task.WasRunning);
        fixture.SaveFails = false;
        fixture.BeforeSave = entries =>
        {
            Assert.True(task.CanStop);
            Assert.True(task.WasRunning);
            Assert.False(Assert.Single(entries).WasRunning);
        };
        Assert.Null(workspace.StopChecked(task.Id));
        fixture.BeforeSave = null;
        WaitFor(workspace, () => task.CanStart);
        var reopened = fixture.Open(workspace.ExportSaved());
        reopened.ResumeRunning();
        Assert.Null(Assert.Single(reopened.Tasks).Service);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShutdownPreservesResumeIntentAndAlwaysStopsEvenWhenSaveThrows(bool saveFails)
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open();
        var first = Start(workspace, List(A));
        var stopped = Start(workspace, List(B));
        Stop(workspace, stopped);
        var service = first.Service!;
        var candidate = new List<SavedPreparation>();
        fixture.BeforeSave = entries => candidate = entries;
        fixture.SaveFails = saveFails;
        workspace.Shutdown();
        Assert.Equal(ServiceState.Stopped, service.State);
        Assert.Empty(workspace.Tasks);
        Assert.True(candidate.Single(entry => entry.Id == first.Id).WasRunning);
        Assert.False(candidate.Single(entry => entry.Id == stopped.Id).WasRunning);
        Assert.DoesNotContain(fixture.Notices, notice => notice.Contains("managed-secret-"));
        var calls = fixture.SaveCalls;
        workspace.Shutdown();
        Assert.Equal(calls, fixture.SaveCalls);
        if (!saveFails)
        {
            fixture.BeforeSave = null;
            var reopened = fixture.Open(fixture.Saved);
            reopened.ResumeRunning();
            WaitFor(reopened, () => !reopened.Find(first.Id)!.Pending);
            Assert.True(reopened.Find(first.Id)!.CanStop);
            Assert.Null(reopened.Find(stopped.Id)!.Service);
        }
    }

    [Fact]
    public void ServiceFaultClearsResumeIntentAndDoesNotExposeRawStartupError()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A, running: true) });
        var task = workspace.Find("one")!;
        task.Service = new ProxyService(fixture.Logger, "fake");
        task.Service.SetErrorForTest("raw managed-secret-a");
        workspace.Poll();
        Assert.False(task.WasRunning);
        Assert.Equal("后台准备服务发生异常", task.LastError);
        Assert.False(Assert.Single(fixture.Saved).WasRunning);
    }

    [Fact]
    public void DeleteBarrierBlocksCreationRebindingAndRestartAndCanBeNested()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A, SavedPreparation.CurrentSource) });
        var outer = workspace.BlockKeys(new[] { A });
        using (workspace.BlockKeys(new[] { A }))
        {
            outer.Dispose();
            Assert.NotNull(workspace.PrepareKeys(ClientType.Codex, new[] { A }, out var count));
            Assert.Equal(0, count);
            fixture.Current = B;
            Assert.False(workspace.SubmitPrepareDialog(workspace.OpenPrepareDialog("one")));
            Assert.False(workspace.SubmitPrepareDialog(List(A)));
        }
        fixture.Current = A;
        Start(workspace, workspace.OpenPrepareDialog("one"));
        Assert.Equal(A, workspace.Find("one")!.Binding);
    }

    [Fact]
    public void ExportAndPostCommitDeleteUsePersistentBindingWithoutSecondSave()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A, SavedPreparation.CurrentSource), Saved("two", B) });
        fixture.Current = B;
        var snapshot = workspace.ExportSaved(new[] { A });
        Assert.Equal("two", Assert.Single(snapshot).Id);
        snapshot[0].Model = "mutated-copy";
        Assert.Empty(workspace.Find("two")!.Model);
        Assert.Equal(0, fixture.SaveCalls);
        using (workspace.BlockKeys(new[] { A })) workspace.RemoveForKeysAfterCommit(new[] { A });
        Assert.Null(workspace.FindForKey(A));
        Assert.Equal("two", Assert.Single(workspace.Tasks).Id);
        Assert.Equal(0, fixture.SaveCalls);
    }

    [Fact]
    public void OrdinaryDeleteSavesBeforeRemovingAndFailureRetainsTask()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A) });
        var task = workspace.Find("one")!;
        fixture.SaveFails = true;
        Assert.NotNull(workspace.RemoveChecked(task.Id));
        Assert.Same(task, Assert.Single(workspace.Tasks));
        fixture.SaveFails = false;
        fixture.BeforeSave = entries => { Assert.Empty(entries); Assert.Same(task, Assert.Single(workspace.Tasks)); };
        Assert.Null(workspace.RemoveChecked(task.Id));
        fixture.BeforeSave = null;
        Assert.Empty(workspace.Tasks);
    }

    [Fact]
    public void DailyCountsUseTotalsDeltasAndResetAtLocalMidnightWithoutDoubleCounting()
    {
        using var fixture = new Fixture();
        var entry = Saved("one", A);
        entry.DailyDate = "2026-09-30"; entry.DailyRounds = 4; entry.DailySuccesses = 3;
        var workspace = fixture.Open(new[] { entry });
        var task = workspace.Find("one")!;
        var watchdog = new KeepAliveWatchdog(false, TimeSpan.FromMinutes(5));
        using var registration = watchdog.RegisterService(KeepAliveFlavor.Codex);
        task.Service = new ProxyService(fixture.Logger, "fake").WithKeepAliveWatchdog(watchdog);
        task.Service.ForceStateForTest(ServiceState.Running);
        Assert.True(watchdog.RequestPreparation());
        using (var probe = watchdog.BeginDueProbe()!) Assert.NotNull(probe.Complete("model", 52));
        workspace.Poll();
        Assert.Equal((5UL, 4UL, "2026-09-30"), (task.DailyRounds, task.DailySuccesses, task.DailyDate));
        workspace.Poll(); workspace.ExportSaved();
        Assert.Equal((5UL, 4UL), (task.DailyRounds, task.DailySuccesses));
        Assert.True(watchdog.RequestPreparation());
        using (var probe = watchdog.BeginDueProbe()!) probe.Fail("测试失败");
        workspace.Poll();
        Assert.Equal((6UL, 4UL), (task.DailyRounds, task.DailySuccesses));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        workspace.Poll();
        Assert.Equal((0UL, 0UL, "2026-10-01"), (task.DailyRounds, task.DailySuccesses, task.DailyDate));
        watchdog.SetPreparationRetryNowForTest();
        using (var probe = watchdog.BeginDueProbe()!) Assert.NotNull(probe.Complete("model", 60));
        workspace.Poll();
        Assert.Equal((1UL, 1UL), (task.DailyRounds, task.DailySuccesses));
        Assert.Null(workspace.StopChecked(task.Id));
        task.Service.ForceStateForTest(ServiceState.Stopped);
        workspace.Poll();
        var before = workspace.ExportSaved().Single();
        var reopened = fixture.Open(new[] { before });
        Assert.Equal((1UL, 1UL), (reopened.Find(task.Id)!.DailyRounds, reopened.Find(task.Id)!.DailySuccesses));
        workspace.Start(task.Id);
        WaitFor(workspace, () => !task.Pending);
        Assert.True(task.DailyRounds >= 1);
        Assert.Equal(1UL, task.DailySuccesses);
    }

    [Fact]
    public void DailySaveFailureRetriesSafelyAndOldDateIsClearedOnRestore()
    {
        using var fixture = new Fixture();
        var entry = Saved("one", A);
        entry.DailyDate = "2026-09-29"; entry.DailyRounds = 99; entry.DailySuccesses = 88;
        var workspace = fixture.Open(new[] { entry });
        var task = workspace.Find("one")!;
        Assert.Equal((0UL, 0UL), (task.DailyRounds, task.DailySuccesses));
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        fixture.SaveFails = true;
        workspace.Poll(); workspace.Poll();
        Assert.Single(fixture.Notices);
        fixture.SaveFails = false;
        workspace.Poll();
        Assert.Equal("2026-10-01", Assert.Single(fixture.Saved).DailyDate);
    }

    [Fact]
    public void ShortcutOnOldCurrentCreatesFixedTaskAndKeepsFollowingTask()
    {
        using var fixture = new Fixture();
        var entry = Saved("one", A, SavedPreparation.CurrentSource);
        entry.Model = "follow-override";
        entry.IdleMinutes = "7.5";
        entry.ReasoningEffort = "high";
        var workspace = fixture.Open(new[] { entry });
        var following = workspace.Find("one")!;
        fixture.Current = B;
        var observedSave = false;
        fixture.BeforeSave = entries =>
        {
            if (observedSave) return;
            observedSave = true;
            Assert.Same(following, Assert.Single(workspace.Tasks));
            Assert.Equal(A, following.Binding);
            Assert.Equal(2, entries.Count);
            Assert.Empty(entries.Single(item => item.Id == following.Id).KeyId);
            Assert.Equal(A.KeyId, entries.Single(item => item.Id != following.Id).KeyId);
        };
        Assert.Null(workspace.PrepareKeys(ClientType.Codex, new[] { A }, out var count));
        fixture.BeforeSave = null;
        Assert.True(observedSave);
        Assert.Equal(1, count);
        Assert.Equal(2, workspace.Tasks.Count);
        Assert.Same(following, workspace.Find("one"));
        Assert.Equal(PrepareMode.LocalProvider, following.Mode);
        Assert.True(following.Binding.IsEmpty);
        Assert.Null(following.Service);
        Assert.Equal(("follow-override", "7.5", ReasoningEffort.High), (following.Model, following.IdleMinutes, following.ReasoningEffort));
        var fixedTask = workspace.FindForKey(A)!;
        Assert.NotSame(following, fixedTask);
        WaitFor(workspace, () => !fixedTask.Pending);
        Assert.Equal(PrepareMode.ListProvider, fixedTask.Mode);
        Assert.Equal(("default-model", "5", ReasoningEffort.Default), (fixedTask.Model, fixedTask.IdleMinutes, fixedTask.ReasoningEffort));
        var saved = workspace.ExportSaved();
        Assert.Equal(SavedPreparation.CurrentSource, saved.Single(item => item.Id == following.Id).ProviderSource);
        Assert.False(saved.Single(item => item.Id == following.Id).WasRunning);
        Assert.True(saved.Single(item => item.Id == fixedTask.Id).WasRunning);
        workspace.Start(following.Id);
        WaitFor(workspace, () => !following.Pending);
        Assert.Same(following, workspace.FindForKey(B));
        Assert.Equal(PrepareMode.LocalProvider, following.Mode);
        Assert.True(fixedTask.CanStop);
        Assert.Equal(A, fixedTask.Binding);
    }

    [Fact]
    public void ShortcutReleaseAndNewFixedTaskRollBackTogetherWhenSaveFails()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A, SavedPreparation.CurrentSource) });
        var original = workspace.Find("one")!;
        fixture.Current = B;
        fixture.SaveFails = true;
        Assert.Equal("准备任务保存失败，请检查配置文件后重试", workspace.PrepareKeys(ClientType.Codex, new[] { A }, out var count));
        Assert.Equal(0, count);
        Assert.Same(original, Assert.Single(workspace.Tasks));
        Assert.Equal(A, original.Binding);
        Assert.Equal(PrepareMode.LocalProvider, original.Mode);
        Assert.Null(original.Service);
        fixture.SaveFails = false;
        Assert.Null(workspace.PrepareKeys(ClientType.Codex, new[] { A }, out count));
        Assert.Equal(1, count);
        Assert.Equal(2, workspace.Tasks.Count);
        Assert.Equal(2, workspace.FindForKey(A)!.Number);
    }

    [Fact]
    public void ShortcutOnSameCurrentReusesFollowAndUsesOnlyTheResolvedSnapshot()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A, SavedPreparation.CurrentSource) });
        fixture.ResolveOverride = (_, key) =>
        {
            var target = fixture.Targets[key ?? fixture.Current];
            fixture.Current = B;
            return target;
        };
        Assert.Null(workspace.PrepareKeys(ClientType.Codex, new[] { A }, out var count));
        Assert.Equal(1, count);
        var task = Assert.Single(workspace.Tasks);
        WaitFor(workspace, () => !task.Pending);
        Assert.Equal("one", task.Id);
        Assert.Equal(A, task.Binding);
        Assert.Equal(PrepareMode.LocalProvider, task.Mode);
        Assert.Single(fixture.Resolved);
    }

    [Fact]
    public void DuplicateSavedKeysAreRetainedAndCannotRunInParallel()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A, running: true), Saved("two", A, running: true), Saved("other", C, running: true) });
        Assert.Equal(3, workspace.Tasks.Count);
        workspace.ResumeRunning();
        WaitFor(workspace, () => !workspace.Find("other")!.Pending);
        Assert.True(workspace.Find("other")!.CanStop);
        Assert.Equal(3, workspace.Tasks.Count);
        var duplicates = workspace.Tasks.Where(task => task.Binding == A).ToList();
        Assert.Equal(2, duplicates.Count);
        Assert.All(duplicates, task =>
        {
            Assert.False(task.WasRunning);
            Assert.Null(task.Service);
            Assert.Contains("已有", task.LastError);
        });
        Assert.Equal(3, workspace.ExportSaved().Count);
        Assert.Null(workspace.RemoveChecked("two"));
        workspace.Start("one");
        WaitFor(workspace, () => !workspace.Find("one")!.Pending);
        Assert.True(workspace.Find("one")!.CanStop);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("save")]
    public void ResumeFailureOnFirstTaskDoesNotBlockFollowingTaskOrExposeSecrets(string failure)
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A, running: true), Saved("two", B, running: true) });
        if (failure == "target")
            fixture.ResolveOverride = (_, key) => key == A ? throw new IOException("raw managed-secret-a") : fixture.Targets[key!.Value];
        else
            fixture.BeforeSave = _ =>
            {
                if (fixture.SaveCalls == 1) throw new IOException("raw managed-secret-a", new InvalidOperationException("inner managed-secret-b"));
            };
        workspace.ResumeRunning();
        WaitFor(workspace, () => !workspace.Find("two")!.Pending);
        var first = workspace.Find("one")!;
        Assert.False(first.WasRunning);
        Assert.Null(first.Service);
        Assert.NotNull(first.LastError);
        Assert.True(workspace.Find("two")!.CanStop);
        Assert.DoesNotContain("managed-secret-", first.LastError);
        Assert.DoesNotContain(fixture.Notices, notice => notice.Contains("managed-secret-"));
        while (fixture.Logger.UiLines!.TryRead(out var line)) Assert.DoesNotContain("managed-secret-", line);
        Assert.False(fixture.Saved.Single(item => item.Id == first.Id).WasRunning);
    }

    [Fact]
    public void PostCommitDeleteStopsRunningServiceWithoutAnotherSaveAndKeepsReleasedFollow()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A, SavedPreparation.CurrentSource) });
        fixture.Current = B;
        Assert.Null(workspace.PrepareKeys(ClientType.Codex, new[] { A }, out _));
        var task = workspace.FindForKey(A)!;
        WaitFor(workspace, () => !task.Pending);
        var service = task.Service!;
        var calls = fixture.SaveCalls;
        using (workspace.BlockKeys(new[] { A }))
        {
            Assert.Equal("one", Assert.Single(workspace.ExportSaved(new[] { A })).Id);
            workspace.RemoveForKeysAfterCommit(new[] { A });
        }
        Assert.Equal(ServiceState.Stopped, service.State);
        Assert.Equal("one", Assert.Single(workspace.Tasks).Id);
        Assert.Equal(calls, fixture.SaveCalls);
        Assert.Equal(PrepareMode.LocalProvider, workspace.Find("one")!.Mode);
    }

    [Fact]
    public void ShutdownPersistsTheFinalInterruptedRoundWithoutLosingRunningIntent()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A, running: true) });
        var task = workspace.Find("one")!;
        var watchdog = new KeepAliveWatchdog(false, TimeSpan.FromMinutes(5));
        using var registration = watchdog.RegisterService(KeepAliveFlavor.Codex);
        task.Service = new ProxyService(fixture.Logger, "fake").WithKeepAliveWatchdog(watchdog);
        task.Service.ForceStateForTest(ServiceState.Running);
        Assert.True(watchdog.RequestPreparation());
        using var probe = watchdog.BeginDueProbe()!;
        using var cancellation = probe.Cancel.Register(probe.Dispose);
        workspace.Shutdown();
        var saved = Assert.Single(fixture.Saved);
        Assert.True(saved.WasRunning);
        Assert.Equal(1UL, saved.DailyRounds);
        Assert.Equal(0UL, saved.DailySuccesses);
        var reopened = fixture.Open(fixture.Saved);
        Assert.Equal(1UL, Assert.Single(reopened.Tasks).DailyRounds);
    }

    [Fact]
    public void ReadinessAndElapsedExposePreparationLifecycleAndRedactKnownSecrets()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Open(new[] { Saved("one", A) });
        var task = workspace.Find("one")!;
        var watchdog = new KeepAliveWatchdog(false, TimeSpan.FromMinutes(5));
        watchdog.EnableAfterPreparation();
        using var registration = watchdog.RegisterService(KeepAliveFlavor.Codex);
        task.Service = new ProxyService(fixture.Logger, "fake").WithKeepAliveWatchdog(watchdog);
        task.Service.ForceStateForTest(ServiceState.Running);
        task.UpstreamApiKey = "managed-secret-a";
        task.BeginTiming();
        Assert.True(watchdog.RequestPreparation());
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(12);
        Assert.Equal(TimeSpan.FromSeconds(12), task.PreparationElapsed);
        Assert.False(task.IsReady);
        using (var probe = watchdog.BeginDueProbe()!) probe.Fail("refused managed-secret-a");
        Assert.DoesNotContain("managed-secret-a", task.LastError);
        watchdog.SetPreparationRetryNowForTest();
        using (var probe = watchdog.BeginDueProbe()!) Assert.NotNull(probe.Complete("model", 52));
        workspace.Poll();
        Assert.True(task.IsReady);
        Assert.Equal(TimeSpan.FromSeconds(12), task.PreparationElapsed);
        Assert.InRange(task.NextKeepAlive.TotalSeconds, 290, 300);
    }
}
