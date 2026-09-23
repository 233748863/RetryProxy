using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>对应 ui.rs 里与绘制无关的单元测试：日志解析、文案函数、服务商/通道编排与一键准备。</summary>
[Collection("cli-environment")]
public class WorkspaceTests
{
    // ---------------------------------------------------------------- 日志解析

    [Fact]
    public void LogLevelIsParsedFromTheLinePrefix()
    {
        Assert.Equal(LogLevelFilter.Info, LogLine.Level("2026-09-05 00:12:11 INFO [默认通道] 已启动"));
        Assert.Equal(LogLevelFilter.Warning, LogLine.Level("2026-09-05 00:12:11 WARNING [默认通道] 第 1 次重试"));
        Assert.Equal(LogLevelFilter.Error, LogLine.Level("2026-09-05 00:12:11 ERROR [默认通道] 上游不可用"));
    }

    [Fact]
    public void LogLevelIgnoresKeywordsInTheMessageBody()
    {
        // 正文里出现 ERROR 不应把一条 INFO 日志标成错误。
        Assert.Equal(LogLevelFilter.Info, LogLine.Level("2026-09-05 00:12:11 INFO [默认通道] 上游返回 {\"type\":\"ERROR\"}"));
    }

    [Fact]
    public void LogFilterMatchesOnlyTheSelectedLevel()
    {
        const string warning = "2026-09-05 00:12:11 WARNING [通道] 重试";
        Assert.True(LogLevelFilter.All.Accepts(warning));
        Assert.True(LogLevelFilter.Warning.Accepts(warning));
        Assert.False(LogLevelFilter.Info.Accepts(warning));
        Assert.False(LogLevelFilter.Error.Accepts(warning));
    }

    [Fact]
    public void LogLineSplitsIntoTimestampLevelTagsAndBody()
    {
        var parts = LogLine.Split("2026-09-05 09:50:00 INFO [E2E][e1657c64] 第 1/2 次 GET /test -> 上游 HTTP 500");
        Assert.Equal("2026-09-05 09:50:00", parts.Timestamp);
        Assert.Equal(LogLevelFilter.Info, parts.Level);
        Assert.Equal(new[] { "E2E", "e1657c64" }, parts.Tags);
        Assert.Equal("第 1/2 次 GET /test -> 上游 HTTP 500", parts.Body);
    }

    [Fact]
    public void AParsedTimestampIsAlwaysLongEnoughToSliceOffTheDate()
    {
        var parts = LogLine.Split("2026-09-05 09:50:00 INFO [E2E] 已启动");
        Assert.Equal(19, parts.Timestamp.Length);
        Assert.Equal("09:50:00", parts.TimeOfDay);
    }

    [Fact]
    public void LogLineWithoutATimestampDegradesToBodyOnly()
    {
        var parts = LogLine.Split("配置已从注册表加载");
        Assert.Empty(parts.Timestamp);
        Assert.Equal(LogLevelFilter.Info, parts.Level);
        Assert.Empty(parts.Tags);
        Assert.Equal("配置已从注册表加载", parts.Body);
        Assert.Empty(parts.TimeOfDay);
    }

    [Fact]
    public void EveryLevelHasAPaddedBadge()
    {
        // 等宽标签让正文起点对齐。
        Assert.All(new[] { LogLevelFilter.Info, LogLevelFilter.Warning, LogLevelFilter.Error }, level => Assert.Equal(4, level.Badge().Length));
        Assert.Equal(new[] { "全部", "信息", "警告", "错误" }, LogLevelFilterExtensions.All.Select(filter => filter.Label()));
    }

    [Fact]
    public void StatusCodesInTheBodyAreLocatedAndColoredByClass()
    {
        const string body = "GET /test -> 上游 HTTP 503，用时 0.02 秒";
        var (status, start, end) = LogLine.FindStatusCode(body)!.Value;
        Assert.Equal(503, status);
        Assert.Equal("HTTP 503", body.Substring(start, end - start));
        Assert.Equal(StatusColorClass.Level, LogLine.StatusColor(200));
        Assert.Equal(StatusColorClass.Level, LogLine.StatusColor(302));
        Assert.Equal(StatusColorClass.Warning, LogLine.StatusColor(429));
        Assert.Equal(StatusColorClass.Danger, LogLine.StatusColor(500));
        Assert.Null(LogLine.StatusColor(999));
    }

    [Fact]
    public void ABareHttpWordIsNotMistakenForAStatusCode()
    {
        // 只有紧跟三位数字才算状态码，URL 里的 HTTP 不该被着色。
        Assert.Null(LogLine.FindStatusCode("上游地址 http://example.com 不可用"));
        Assert.Null(LogLine.FindStatusCode("HTTP 42 不是状态码"));
        // 前面出现无效候选时应继续往后找。
        Assert.Equal(200, LogLine.FindStatusCode("HTTP 42 然后 HTTP 200")!.Value.Status);
    }

    [Fact]
    public void LogMatchesCombinesLevelQueryAndRouteFilters()
    {
        const string line = "2026-09-05 09:50:00 WARNING [E2E][abc123] 上游 HTTP 500 可重试";
        Assert.True(LogLine.Matches(line, LogLevelFilter.All, string.Empty, null));
        Assert.True(LogLine.Matches(line, LogLevelFilter.Warning, string.Empty, null));
        Assert.False(LogLine.Matches(line, LogLevelFilter.Error, string.Empty, null));
        // 关键字大小写不敏感，请求 ID 也能搜到。
        Assert.True(LogLine.Matches(line, LogLevelFilter.All, "abc123", null));
        Assert.False(LogLine.Matches(line, LogLevelFilter.All, "不存在", null));
        // 通道筛选只认完整的 [名称] 标签。
        Assert.True(LogLine.Matches(line, LogLevelFilter.All, string.Empty, "E2E"));
        Assert.False(LogLine.Matches(line, LogLevelFilter.All, string.Empty, "默认通道"));
    }

    [Fact]
    public void IdleMinutesInputOnlyAcceptsAPositiveNumber()
    {
        Assert.Equal(3.0, UiText.ParseIdleMinutes("3"));
        Assert.Equal(7.5, UiText.ParseIdleMinutes(" 7.5 "));
        Assert.Null(UiText.ParseIdleMinutes(string.Empty));
        Assert.Null(UiText.ParseIdleMinutes("三"));
        Assert.Null(UiText.ParseIdleMinutes("0"));
        Assert.Null(UiText.ParseIdleMinutes("-2"));
        Assert.Null(UiText.ParseIdleMinutes("Infinity"));
    }

    [Fact]
    public void DurationsAreSpelledOutInChinese()
    {
        Assert.Equal("0 秒", UiText.FormatDurationCn(TimeSpan.Zero));
        Assert.Equal("45 秒", UiText.FormatDurationCn(TimeSpan.FromSeconds(45)));
        Assert.Equal("1 分", UiText.FormatDurationCn(TimeSpan.FromSeconds(60)));
        Assert.Equal("2 分 30 秒", UiText.FormatDurationCn(TimeSpan.FromSeconds(150)));
        Assert.Equal("1 小时", UiText.FormatDurationCn(TimeSpan.FromSeconds(3600)));
        Assert.Equal("1 小时 5 分", UiText.FormatDurationCn(TimeSpan.FromSeconds(3900)));
    }

    [Fact]
    public void TrimFloatDropsTrailingZeros()
    {
        Assert.Equal("300", UiText.TrimFloat(300.0));
        Assert.Equal("0.5", UiText.TrimFloat(0.5));
        Assert.Equal("4.25", UiText.TrimFloat(4.25));
        Assert.Equal("0", UiText.TrimFloat(0.0));
    }

    [Fact]
    public void ServiceStatesAllHaveLabels()
    {
        var labels = Enum.GetValues<ServiceState>().Select(UiText.StateLabel).ToArray();
        Assert.Equal(new[] { "已停止", "启动中", "运行中", "停止中", "异常" }, labels);
    }

    [Fact]
    public void LogBufferTrimsInBatches()
    {
        var buffer = new LogBuffer();
        for (var index = 0; index < LogBuffer.RetainLines; index++)
        {
            buffer.Push($"line-{index}");
        }

        Assert.Equal(LogBuffer.RetainLines, buffer.Count);
        buffer.Push("overflow");
        Assert.Equal(LogBuffer.RetainLines - LogBuffer.TrimLines + 1, buffer.Count);
        Assert.Equal($"line-{LogBuffer.TrimLines}", buffer[0]);
        Assert.Equal(LogBuffer.TrimLines, buffer.Dropped);
        Assert.Equal(LogBuffer.RetainLines + 1, buffer.TotalPushed);
        buffer.Clear();
        Assert.Equal(0, buffer.Count);
        Assert.Equal(LogBuffer.RetainLines + 1, buffer.Dropped);
    }

    // ---------------------------------------------------------------- 夹具

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Logger = ProxyLogger.Create(Directory);
            var config = new ProxyConfig
            {
                Providers = new[] { "alpha", "beta", "empty" }.Select(name => new ProviderEndpoint(name, $"https://{name}.example")).ToList(),
                Routes = new[] { ("alpha-one", "alpha", 18080), ("alpha-two", "alpha", 18081), ("beta-one", "beta", 18082) }
                    .Select(item => new ProxyRoute
                    {
                        Id = item.Item1,
                        Name = item.Item1,
                        ProviderName = item.Item2,
                        ListenPort = item.Item3,
                        DesiredRunning = item.Item1 == "alpha-one",
                        ClientType = item.Item1 == "alpha-two" ? ClientType.Claude : ClientType.Codex,
                        KeepaliveEnabled = item.Item2 == "alpha",
                        KeepaliveIdleMinutes = item.Item2 == "alpha" ? 7.0 : 11.0,
                    })
                    .ToList(),
                SelectedRouteId = "alpha-two",
            }.Normalize();
            App = new ProxyWorkspace(Logger, config, null)
            {
                SelectedRoute = "alpha-two",
                SelectedProvider = "alpha",
            };
            App.RefreshServices();
        }

        public string Directory { get; }

        public ProxyLogger Logger { get; }

        public ProxyWorkspace App { get; }

        public List<string> DrainLogs()
        {
            var lines = new List<string>();
            while (Logger.UiLines!.TryRead(out var line))
            {
                lines.Add(line);
            }

            return lines;
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
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static KeepAliveTemplate Template(string path, string body)
    {
        return new KeepAliveTemplate("POST", path, new HeaderList(), Encoding.UTF8.GetBytes(body));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void BindRouteToFreePort(ProxyWorkspace app, string routeId)
    {
        foreach (var route in app.Config.Routes.Where(route => route.Id == routeId))
        {
            route.ListenPort = FreePort();
            route.KeepaliveEnabled = false;
        }
    }

    /// <summary>等通道监听成功并把待提交的准备交出去；启动是后台线程完成的。</summary>
    private static void WaitForPendingPreparation(ProxyWorkspace app, string routeId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (app.Pending is not null)
        {
            Assert.True(DateTime.UtcNow < deadline, $"通道 {routeId} 未在 5 秒内启动：{app.Services[routeId].StartupError}");
            Thread.Sleep(10);
            app.PollPendingPreparation();
        }

        Assert.Equal(ServiceState.Running, app.Services[routeId].State);
    }

    // ---------------------------------------------------------------- 服务与选择

    [Fact]
    public void RefreshingServicesPreservesIndependentRouteKeepAliveSettings()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var watchdog = app.Services["alpha-one"].KeepAlive;
        var metrics = app.Services["alpha-one"].Metrics;
        Assert.NotSame(watchdog, app.Services["alpha-two"].KeepAlive);
        Assert.NotSame(watchdog, app.Services["beta-one"].KeepAlive);
        app.Config.Routes[1].KeepaliveEnabled = false;
        app.Config.Routes[1].KeepaliveIdleMinutes = 9.0;
        app.Config.Routes[1].KeepaliveContextLimit = 72000;
        app.RefreshServices();
        Assert.Same(watchdog, app.Services["alpha-one"].KeepAlive);
        Assert.Same(metrics, app.Services["alpha-one"].Metrics);
        Assert.False(app.Services["alpha-two"].KeepAlive.Enabled);
        Assert.True(watchdog.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(420), watchdog.Idle);
        Assert.Equal(50000UL, watchdog.Snapshot().ContextLimit);
        Assert.Equal(72000UL, app.Services["alpha-two"].KeepAlive.Snapshot().ContextLimit);
        Assert.Equal(TimeSpan.FromSeconds(540), app.Services["alpha-two"].KeepAlive.Idle);
        Assert.Equal(TimeSpan.FromSeconds(660), app.Services["beta-one"].KeepAlive.Idle);
        Assert.Equal("9", app.KeepAliveMinutes);
        app.SelectedRoute = "alpha-one";
        app.SyncSelection();
        Assert.Equal("7", app.KeepAliveMinutes);
    }

    [Fact]
    public void DisablingOneChannelOnlyCancelsItsOwnBackgroundRequest()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var codex = app.RouteKeepAlives["alpha-one"];
        var claude = app.RouteKeepAlives["alpha-two"];
        using var codexService = codex.RegisterService(KeepAliveFlavor.Codex);
        using var claudeService = claude.RegisterService(KeepAliveFlavor.Claude);
        Assert.True(codex.RequestPreparation());
        Assert.True(claude.RequestPreparation());
        using var codexProbe = codex.BeginDueProbe()!;
        using var claudeProbe = claude.BeginDueProbe()!;
        var codexSession = codex.Snapshot().SessionId;

        app.SetKeepAlive("alpha-two", false, 9.0, 72000);
        app.RefreshServices();

        Assert.True(claudeProbe.Cancel.IsCancellationRequested);
        Assert.Null(claude.Snapshot().SessionId);
        Assert.False(claude.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(540), claude.Idle);
        Assert.Equal(72000UL, claude.Snapshot().ContextLimit);
        Assert.False(codexProbe.Cancel.IsCancellationRequested);
        Assert.Equal(codexSession, codex.Snapshot().SessionId);
        Assert.True(codex.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(420), codex.Idle);
        Assert.Equal(50000UL, codex.Snapshot().ContextLimit);
        Assert.False(app.Config.RuntimeConfigFor("alpha-two").KeepaliveEnabled);
        Assert.True(app.Config.RuntimeConfigFor("alpha-one").KeepaliveEnabled);
        Assert.Equal("9", app.KeepAliveMinutes);
        app.SelectRoute("alpha-one");
        Assert.Equal("7", app.KeepAliveMinutes);
        app.SelectRoute("alpha-two");
        Assert.Equal("9", app.KeepAliveMinutes);
        Assert.False(app.Config.KeepaliveEnabled);
    }

    [Fact]
    public void SameProviderRoutesUseIndependentCliFlavorsAndSessions()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var codex = app.RouteKeepAlives["alpha-one"];
        var claude = app.RouteKeepAlives["alpha-two"];
        Assert.NotSame(codex, claude);
        Assert.Equal(KeepAliveFlavor.Codex, codex.Snapshot().Flavor);
        Assert.Equal(KeepAliveFlavor.Claude, claude.Snapshot().Flavor);

        var codexTemplate = Template("/v1/responses", "{\"model\":\"codex-model\"}");
        codex.Remember(codexTemplate);
        var codexProbe = codex.BeginProbe(codexTemplate)!;
        var codexSession = codexProbe.SessionId;
        codexProbe.Complete("Codex answer", 100);
        codexProbe.Dispose();

        var claudeTemplate = Template("/v1/messages", "{\"model\":\"claude-model\"}");
        claude.Remember(claudeTemplate);
        var claudeProbe = claude.BeginProbe(claudeTemplate)!;
        var claudeSession = claudeProbe.SessionId;
        claudeProbe.Complete("Claude answer", 120);
        claudeProbe.Dispose();

        Assert.Equal(codexSession, codex.Snapshot().SessionId);
        Assert.Equal(claudeSession, claude.Snapshot().SessionId);
        Assert.NotEqual(codex.Snapshot().SessionId, claude.Snapshot().SessionId);
        Assert.Equal(KeepAliveFlavor.Codex, codex.Snapshot().Flavor);
        Assert.Equal(KeepAliveFlavor.Claude, claude.Snapshot().Flavor);
    }

    [Fact]
    public void SwitchingProviderViewsPreservesTheBackgroundConversation()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var watchdog = app.RouteKeepAlives["alpha-two"];
        var template = Template("/v1/responses", "{\"model\":\"latest-model\",\"input\":\"private\"}");
        watchdog.Remember(template);
        var probe = watchdog.BeginProbe(template)!;
        var sessionId = probe.SessionId;
        Assert.NotNull(probe.Complete("Java answer", 120));
        probe.Dispose();
        foreach (var name in new[] { "beta", "empty", "alpha" })
        {
            app.SelectedProvider = name;
            app.RefreshServices();
        }

        Assert.Same(watchdog, app.RouteKeepAlives["alpha-two"]);
        Assert.Equal(sessionId, watchdog.Snapshot().SessionId);
        Assert.Equal(1, watchdog.Snapshot().Turns);
        Assert.Equal(120UL, watchdog.Snapshot().ContextTokens);
    }

    [Fact]
    public void ProviderSwitchFiltersRoutesWithoutRebindingOrResettingServices()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var routesBefore = app.Config.Routes.Select(route => route.Clone()).ToList();
        var metricsBefore = app.Services["alpha-one"].Metrics;
        metricsBefore.RequestStarted("provider-test", "POST", "/v1/responses");
        app.SelectedProvider = "beta";
        app.SyncSelection();
        Assert.Equal("beta-one", app.SelectedRoute);
        Assert.Equal("beta-one", app.Config.SelectedRouteId);
        Assert.Equal("11", app.KeepAliveMinutes);
        Assert.Equal(new[] { "beta-one" }, app.VisibleRoutes().Select(route => route.Id));
        Assert.Equal(routesBefore, app.Config.Routes);
        Assert.Same(metricsBefore, app.Services["alpha-one"].Metrics);
        Assert.Equal(1UL, metricsBefore.Snapshot().ActiveRequests);
        metricsBefore.RequestFinished("provider-test");
    }

    [Fact]
    public void EmptyProviderKeepsTheViewEmptyAfterRefresh()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.SelectedProvider = "empty";
        app.RefreshServices();
        Assert.Equal("empty", app.SelectedProvider);
        Assert.Empty(app.SelectedRoute);
        Assert.Null(app.SelectedRouteRef());
        Assert.Empty(app.VisibleRoutes());
        Assert.Equal("alpha-two", app.Config.SelectedRouteId);
        Assert.Equal(3, app.Config.Routes.Count);
        app.Config.Validate(false);
    }

    [Fact]
    public void DeletingTheLastOwnedRouteKeepsItsProviderSelected()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.SelectedProvider = "beta";
        app.SyncSelection();
        app.Config.Routes.RemoveAll(route => route.Id == "beta-one");
        app.RefreshServices();
        Assert.Equal("beta", app.SelectedProvider);
        Assert.Empty(app.SelectedRoute);
        Assert.Null(app.SelectedRouteRef());
        Assert.Equal("alpha-one", app.Config.SelectedRouteId);
        app.Config.Validate(false);
    }

    [Fact]
    public void SelectionPreservesOwnedRoutesAndFollowsProviderRenames()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.SelectedProvider = "ALPHA";
        app.SyncSelection();
        Assert.Equal("alpha", app.SelectedProvider);
        Assert.Equal("alpha-two", app.SelectedRoute);
        app.Config.Providers[0].Name = "renamed";
        foreach (var route in app.Config.Routes.Where(route => route.ProviderName == "alpha"))
        {
            route.ProviderName = "renamed";
        }

        app.SelectedProvider = "renamed";
        app.SyncSelection();
        Assert.Equal("alpha-two", app.SelectedRoute);
        Assert.Equal(2, app.VisibleRoutes().Count());
    }

    // ---------------------------------------------------------------- 通道编辑器

    [Fact]
    public void NewRouteInheritsTheSelectedProvider()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.SelectedProvider = "empty";
        app.SyncSelection();
        var editor = app.OpenRouteEditor(null)!;
        editor.Name = "new-channel";
        var error = Assert.Throws<WorkspaceException>(() => app.RouteFromEditor(editor));
        Assert.Contains("请选择客户端", error.Message);
        editor.ClientType = ClientType.Claude;
        Assert.Equal("empty", editor.Provider);
        var route = app.RouteFromEditor(editor);
        Assert.Equal("empty", route.ProviderName);
        Assert.Equal(18083, route.ListenPort);
        Assert.Equal(ClientType.Claude, route.ClientType);
        route.Validate();
        Assert.Equal(3, app.Config.Routes.Count);
    }

    [Fact]
    public void EditingARoutePreservesItsProviderAndLiveSettings()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.Config.Routes[0].KeepaliveContextLimit = 12345;
        var original = app.Config.Routes[0].Clone();
        var editor = app.OpenRouteEditor(0)!;
        editor.Provider = "beta";
        editor.Name = "updated-channel";
        var route = app.RouteFromEditor(editor);
        Assert.Equal(original.Id, route.Id);
        Assert.Equal("updated-channel", route.Name);
        Assert.Equal(original.ProviderName, route.ProviderName);
        Assert.Equal(original.ClientType, route.ClientType);
        Assert.Equal(original.DesiredRunning, route.DesiredRunning);
        Assert.Equal(original.KeepaliveEnabled, route.KeepaliveEnabled);
        Assert.Equal(original.KeepaliveIdleMinutes, route.KeepaliveIdleMinutes);
        Assert.Equal(original.KeepaliveContextLimit, route.KeepaliveContextLimit);
        Assert.Equal(original, app.Config.Routes[0]);
    }

    [Fact]
    public void NewRouteStartsWithAutomaticKeepaliveOff()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        Assert.True(app.SelectedRouteRef()!.KeepaliveEnabled);
        var editor = app.OpenRouteEditor(null)!;
        editor.Name = "new-channel";
        editor.ClientType = ClientType.Codex;
        var route = app.RouteFromEditor(editor);
        Assert.Equal("alpha", route.ProviderName);
        Assert.False(route.KeepaliveEnabled);
        Assert.Equal(ConfigDefaults.KeepaliveIdleMinutes, route.KeepaliveIdleMinutes);
        Assert.Equal(50000, route.KeepaliveContextLimit);
    }

    [Fact]
    public void RouteCreationRequiresAProvider()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        using var logger = ProxyLogger.Silent(directory);
        var app = new ProxyWorkspace(logger, new ProxyConfig(), null);
        app.RefreshServices();
        Assert.Null(app.OpenRouteEditor(null));
        Assert.Equal("请先新增服务商，再创建通道", app.Notice);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void RouteEditorKeepsTotalTimeoutSeparateFromAttemptTimeout()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.Config.Routes[0].TotalTimeoutSeconds = 123.0;
        app.Config.Routes[0].GenerationTimeoutSeconds = 90.0;
        var editor = app.OpenRouteEditor(0)!;
        Assert.Equal("123", editor.TotalTimeout);
        Assert.Equal("90", editor.GenerationTimeout);
        editor.TotalTimeout = "45.5";
        editor.GenerationTimeout = "30.5";
        var route = app.RouteFromEditor(editor);
        Assert.Equal(45.5, route.TotalTimeoutSeconds);
        Assert.Equal(30.5, route.GenerationTimeoutSeconds);
        Assert.Equal(app.Config.Routes[0].TimeoutSeconds, route.TimeoutSeconds);
        Assert.Equal(123.0, app.Config.Routes[0].TotalTimeoutSeconds);
        Assert.Equal(90.0, app.Config.Routes[0].GenerationTimeoutSeconds);
        route.Validate();
        editor.GenerationTimeout = "invalid";
        Assert.Contains("等待生成上限", Assert.Throws<WorkspaceException>(() => app.RouteFromEditor(editor)).Message);
        editor.GenerationTimeout = "30.5";
        editor.TotalTimeout = "invalid";
        Assert.Contains("总等待上限", Assert.Throws<WorkspaceException>(() => app.RouteFromEditor(editor)).Message);
    }

    [Fact]
    public void ConfiguredClientTypesSurviveMisleadingNamesAndPorts()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.Config.Routes[0].Name = "Claude renamed";
        app.Config.Routes[0].ListenPort = 18081;
        app.Config.Routes[1].Name = "codex renamed";
        app.Config.Routes[1].ListenPort = 19001;
        app.RefreshServices();
        Assert.Equal(KeepAliveFlavor.Codex, app.RouteKeepAlives["alpha-one"].Snapshot().Flavor);
        Assert.Equal(KeepAliveFlavor.Claude, app.RouteKeepAlives["alpha-two"].Snapshot().Flavor);
        var editor = app.OpenRouteEditor(0)!;
        Assert.Equal(ClientType.Codex, editor.ClientType);
        editor.ClientType = ClientType.Claude;
        Assert.Equal(ClientType.Claude, app.RouteFromEditor(editor).ClientType);
    }

    [Fact]
    public void CommittingRoutesAndProvidersValidatesAndSelectsTheResult()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var editor = app.OpenRouteEditor(null)!;
        editor.Name = "alpha-one";
        editor.ClientType = ClientType.Codex;
        Assert.Equal("转发通道名称重复：alpha-one", app.CommitRoute(editor));
        editor.Name = "gamma";
        Assert.Null(app.CommitRoute(editor));
        Assert.Equal(4, app.Config.Routes.Count);
        Assert.Equal("alpha", app.SelectedProvider);
        Assert.Equal(app.Config.Routes[3].Id, app.SelectedRoute);
        Assert.Equal(18083, app.Config.Routes[3].ListenPort);
        Assert.True(app.Services.ContainsKey(app.SelectedRoute));

        var provider = app.OpenProviderEditor(null);
        provider.Name = "ALPHA";
        provider.Url = "https://other.example";
        Assert.Equal("服务商名称重复", app.CommitProvider(provider));
        provider.Name = "delta";
        Assert.Null(app.CommitProvider(provider));
        Assert.Equal("delta", app.SelectedProvider);
        Assert.Empty(app.SelectedRoute);

        var rename = app.OpenProviderEditor(0);
        Assert.Equal("alpha", rename.Name);
        rename.Name = "alpha-renamed";
        Assert.Null(app.CommitProvider(rename));
        Assert.All(app.Config.Routes.Take(2), route => Assert.Equal("alpha-renamed", route.ProviderName));
        Assert.Equal("alpha-renamed", app.SelectedProvider);

        app.DeleteProvider(0);
        Assert.Equal("该服务商仍被通道使用", app.Notice);
        app.Notice = null;
        app.DeleteProvider(3);
        Assert.Equal(3, app.Config.Providers.Count);
        Assert.Equal("empty", app.SelectedProvider);
    }

    // ---------------------------------------------------------------- 一键准备

    private static void SelectTestModel(PreparationDialogState dialog)
    {
        dialog.Models = new[] { "test-model" };
        dialog.SelectedModel = "test-model";
    }

    [Fact]
    public void LegacyRouteCanBeConfiguredInPlaceWithoutChangingSharedProvider()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.TestCliCommand = new CliCommand(Path.Combine(fixture.Directory, "missing-client.exe"));
        app.RouteKeepAlives.Clear();
        app.Services.Clear();
        BindRouteToFreePort(app, "alpha-one");
        app.Config.Routes.Single(route => route.Id == "alpha-one").Name = "alpha · Codex";
        app.Config.Routes.Single(route => route.Id == "alpha-two").ClientType = ClientType.Codex;
        app.RefreshServices();
        app.SelectedProvider = "alpha";
        app.SelectRoute("alpha-one");
        app.Services["alpha-one"].Start(app.Config.RuntimeConfigFor("alpha-one"), TimeSpan.FromSeconds(5));

        var dialog = app.OpenPrepareDialog()!;
        Assert.Equal(PrepareMode.CurrentRoute, dialog.Mode);
        dialog.Mode = PrepareMode.CurrentRoute;
        dialog.NewProviderName = "alpha";
        dialog.NewProviderUrl = "https://alpha.example";
        dialog.ApiKey = "sk-legacy-upgrade";
        dialog.SelectedModel = "legacy-model";
        Assert.True(app.SubmitPrepareDialog(dialog));
        Assert.Equal(3, app.Config.Routes.Count);
        Assert.Equal(4, app.Config.Providers.Count);
        Assert.Equal("alpha · 独立", app.Config.Routes.Single(route => route.Id == "alpha-one").ProviderName);
        Assert.Equal("alpha", app.Config.Routes.Single(route => route.Id == "alpha-two").ProviderName);
        Assert.Equal("https://alpha.example", app.Config.ProviderByName("alpha")!.BaseUrl);
        Assert.Equal("https://alpha.example", app.Config.ProviderByName("alpha · 独立")!.BaseUrl);
        Assert.Equal("legacy-model", app.RouteKeepAlives["alpha-one"].Snapshot().Model);
        Assert.False(app.RouteKeepAlives["alpha-two"].Snapshot().WithKey);

        var saved = ProxyConfigJson.ToCanonicalJson(app.Config);
        Assert.DoesNotContain("sk-legacy-upgrade", saved);
        var restored = new ProxyWorkspace(fixture.Logger, ProxyConfigJson.Parse(saved).Config, null);
        restored.RefreshServices();
        Assert.True(restored.RouteKeepAlives["alpha-one"].Snapshot().WithKey);
        Assert.Equal("legacy-model", restored.RouteKeepAlives["alpha-one"].Snapshot().Model);
        Assert.False(restored.RouteKeepAlives["alpha-two"].Snapshot().WithKey);

        foreach (var service in app.Services.Values)
        {
            service.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void PreparationRequiresARunningProviderChannel()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        app.SelectedProvider = "alpha";
        app.SelectedRoute = "alpha-two";
        app.SyncSelection();
        app.PrepareSelectedRoute();
        Assert.Contains("请先启用通道", app.Notice);
        Assert.False(app.RouteKeepAlives["alpha-two"].Snapshot().Preparing);
        app.Notice = null;
        Assert.Null(app.OpenPrepareDialog());
        Assert.Contains("请先启用通道", app.Notice);
        app.Notice = null;
        app.SelectedProvider = "empty";
        app.SyncSelection();
        app.PrepareSelectedRoute();
        Assert.Equal("请先选择一条通道，再一键准备", app.Notice);
        app.Notice = null;
        Assert.Null(app.OpenPrepareDialog());
        Assert.Equal("请先选择一条通道，再一键准备", app.Notice);
    }

    [Fact]
    public void PrepareDialogValidatesTheKeyAndSubmitsEachModeToTheSelectedChannel()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        // 用不存在的 CLI 路径，保证测试不会真的拉起本机 Codex。
        app.TestCliCommand = new CliCommand(Path.Combine(fixture.Directory, "missing-client.exe"));
        app.RouteKeepAlives.Clear();
        app.Services.Clear();
        BindRouteToFreePort(app, "alpha-one");
        BindRouteToFreePort(app, "beta-one");
        app.RefreshServices();
        app.SelectedProvider = "alpha";
        app.SelectRoute("alpha-one");
        app.Services["alpha-one"].Start(app.Config.RuntimeConfigFor("alpha-one"), TimeSpan.FromSeconds(5));
        var origin = app.RouteKeepAlives["alpha-one"];
        Assert.Same(origin, app.Services["alpha-one"].KeepAlive);

        var dialog = app.OpenPrepareDialog()!;
        Assert.Equal("alpha-one", dialog.RouteId);
        Assert.Equal(PrepareMode.Default, dialog.Mode);
        // 目标服务商默认选第一个不是本通道所属的服务商。
        Assert.Equal("beta", dialog.Provider);
        Assert.False(dialog.ShowKey);

        // 选了单独准备但没填 Key：弹窗保留并显示原因，什么都不建、不提交。
        dialog.Mode = PrepareMode.SeparateProvider;
        dialog.ApiKey = "  ";
        Assert.False(app.SubmitPrepareDialog(dialog));
        Assert.Equal("请输入该供应商的 API Key", dialog.Error);
        Assert.Equal(3, app.Config.Routes.Count);
        Assert.Null(app.Pending);
        Assert.False(origin.Snapshot().Preparing);

        // 地址相同也创建私有服务商与通道，原 beta-one 不受影响。
        Assert.Contains("将新建独立通道", app.PlanText(dialog));
        dialog.ApiKey = " sk-test-secret ";
        Assert.False(app.SubmitPrepareDialog(dialog));
        Assert.Equal("请输入模型名称，或获取模型后选择一个用于准备", dialog.Error);
        SelectTestModel(dialog);
        dialog.SelectedModel = "   ";
        Assert.False(app.SubmitPrepareDialog(dialog));
        Assert.Equal("请输入模型名称，或获取模型后选择一个用于准备", dialog.Error);
        dialog.SelectedModel = "test-model";
        Assert.True(app.SubmitPrepareDialog(dialog));
        Assert.Null(app.Notice);
        Assert.Equal(4, app.Config.Routes.Count);
        Assert.Equal(4, app.Config.Providers.Count);
        Assert.Equal("beta · 独立", app.SelectedProvider);
        var dedicatedRoute = app.SelectedRouteRef()!;
        Assert.Equal("beta · 独立", dedicatedRoute.ProviderName);
        Assert.True(dedicatedRoute.DedicatedPreparation);
        Assert.NotNull(dedicatedRoute.ProtectedApiKey);
        Assert.NotEqual("sk-test-secret", dedicatedRoute.ProtectedApiKey);
        Assert.Equal("test-model", dedicatedRoute.PreparationModel);
        Assert.False(app.RouteKeepAlives["beta-one"].Snapshot().Preparing);
        Assert.NotNull(app.Pending);
        Assert.Equal("test-model", app.Pending.Credential.Model);
        WaitForPendingPreparation(app, dedicatedRoute.Id);
        var separate = app.RouteKeepAlives[dedicatedRoute.Id];
        var snapshot = separate.Snapshot();
        Assert.True(snapshot.Preparing);
        Assert.True(snapshot.WithKey);
        // 原通道完全不受影响：不准备、不带 Key。
        Assert.False(origin.Snapshot().Preparing);
        Assert.False(origin.Snapshot().WithKey);
        // 密钥只注入 CLI 子进程，不进入日志。
        var logs = fixture.DrainLogs();
        Assert.Contains(logs, line => line.Contains("单独准备"));
        Assert.Contains(logs, line => line.Contains("使用输入的 Key"));
        Assert.DoesNotContain(logs, line => line.Contains("sk-test-secret"));
        Assert.True(separate.CancelPreparation());
        Assert.True(app.PollPreparationEvents());
        Assert.Equal($"通道“{dedicatedRoute.Name}”：准备已终止", app.Notice);
        app.Notice = null;
        // 终止后新通道仍记住这把 Key，状态文字随之显示。
        Assert.True(separate.Snapshot().WithKey);
        Assert.Contains("指定 Key 经本通道", app.KeepAliveHint(app.SelectedRouteRef()!));

        // 主按钮：不弹窗，按新通道当前配置（这把 Key）再准备一次。
        app.PrepareSelectedRoute();
        Assert.True(separate.Snapshot().Preparing);
        Assert.True(separate.Snapshot().WithKey);
        Assert.True(separate.CancelPreparation());
        Assert.True(app.PollPreparationEvents());
        app.Notice = null;

        // 新建服务商：地址与已有服务商不同则新增，并为它新建、启动一条同类型通道。
        dialog = app.OpenPrepareDialog()!;
        Assert.Equal(dedicatedRoute.Id, dialog.RouteId);
        Assert.Equal(PrepareMode.CurrentRoute, dialog.Mode);
        dialog.Mode = PrepareMode.SeparateProvider;
        dialog.Provider = null;
        dialog.NewProviderName = string.Empty;
        Assert.Equal("将为输入的服务商新建独立通道。", app.PlanText(dialog));
        dialog.NewProviderUrl = "https://gamma.example/";
        dialog.ApiKey = "sk-gamma-secret";
        Assert.False(app.SubmitPrepareDialog(dialog));
        Assert.Equal("请输入服务商名称", dialog.Error);
        dialog.NewProviderName = "gamma";
        dialog.SelectedModel = "  manual-model  ";
        Assert.True(app.SubmitPrepareDialog(dialog));
        Assert.Null(app.Notice);
        Assert.Equal(5, app.Config.Providers.Count);
        Assert.Equal("https://gamma.example", app.Config.ProviderByName("gamma")!.BaseUrl);
        Assert.Equal(5, app.Config.Routes.Count);
        var created = app.Config.Routes.Single(route => route.ProviderName == "gamma").Clone();
        Assert.Equal("gamma · Codex", created.Name);
        Assert.Equal(ClientType.Codex, created.ClientType);
        Assert.True(created.DesiredRunning);
        Assert.DoesNotContain(app.Config.Routes, route => route.Id != created.Id && route.ListenPort == created.ListenPort);
        Assert.Equal("gamma", app.SelectedProvider);
        Assert.Equal(created.Id, app.SelectedRoute);
        Assert.Equal("manual-model", app.Pending!.Credential.Model);
        WaitForPendingPreparation(app, created.Id);
        var gamma = app.RouteKeepAlives[created.Id];
        Assert.True(gamma.Snapshot().Preparing);
        Assert.True(gamma.Snapshot().WithKey);
        Assert.False(separate.Snapshot().Preparing);
        Assert.True(separate.Snapshot().WithKey);
        Assert.True(gamma.CancelPreparation());
        Assert.True(app.PollPreparationEvents());
        app.Notice = null;

        // 同一条独立通道修改 URL、Key、模型：保持通道 ID，不更改其他通道的配置。
        dialog = app.OpenPrepareDialog()!;
        Assert.Equal(PrepareMode.CurrentRoute, dialog.Mode);
        Assert.Equal("sk-gamma-secret", dialog.ApiKey);
        Assert.Equal("manual-model", dialog.SelectedModel);
        Assert.Equal("https://gamma.example", dialog.NewProviderUrl);
        dialog.NewProviderUrl = "https://gamma-updated.example";
        dialog.ApiKey = "sk-updated-secret";
        dialog.SelectedModel = "edited-model";
        Assert.True(app.SubmitPrepareDialog(dialog));
        Assert.Equal(5, app.Config.Routes.Count);
        Assert.Equal("https://gamma-updated.example", app.Config.ProviderByName("gamma")!.BaseUrl);
        Assert.Equal("https://beta.example", app.Config.ProviderByName("beta")!.BaseUrl);
        Assert.Equal(created.Id, app.SelectedRoute);
        WaitForPendingPreparation(app, created.Id);
        Assert.True(gamma.Snapshot().Preparing);
        Assert.False(separate.Snapshot().Preparing);
        Assert.True(gamma.CancelPreparation());
        Assert.True(app.PollPreparationEvents());
        app.Notice = null;

        // 重启后的新工作区能从加密配置恢复该通道的密钥与模型。
        var saved = ProxyConfigJson.ToCanonicalJson(app.Config);
        Assert.DoesNotContain("sk-updated-secret", saved);
        Assert.DoesNotContain("sk-gamma-secret", saved);
        var restored = new ProxyWorkspace(fixture.Logger, ProxyConfigJson.Parse(saved).Config, null);
        restored.TestCliCommand = app.TestCliCommand;
        restored.RefreshServices();
        Assert.True(restored.RouteKeepAlives[created.Id].Snapshot().WithKey);
        Assert.Equal("edited-model", restored.RouteKeepAlives[created.Id].Snapshot().Model);
        Assert.True(restored.RouteKeepAlives[dedicatedRoute.Id].Snapshot().WithKey);
        Assert.Equal("test-model", restored.RouteKeepAlives[dedicatedRoute.Id].Snapshot().Model);

        // 选回默认准备会清除当前通道保存的密钥；其他独立通道仍保持自己的密钥。
        dialog = app.OpenPrepareDialog()!;
        dialog.Mode = PrepareMode.Default;
        Assert.True(app.SubmitPrepareDialog(dialog));
        snapshot = gamma.Snapshot();
        Assert.True(snapshot.Preparing);
        Assert.False(snapshot.WithKey);
        Assert.False(app.SelectedRouteRef()!.DedicatedPreparation);
        Assert.True(separate.Snapshot().WithKey);
        Assert.True(gamma.CancelPreparation());
        Assert.True(app.PollPreparationEvents());
        app.Notice = null;
        logs = fixture.DrainLogs();
        Assert.DoesNotContain(logs, line => line.Contains("sk-test-secret") || line.Contains("sk-gamma-secret") || line.Contains("sk-updated-secret"));
        foreach (var service in app.Services.Values)
        {
            service.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void PreparationNoticesSurviveProviderSwitchesAndDoNotOverwriteEachOther()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var registrations = new List<IDisposable>();
        foreach (var routeId in new[] { "alpha-one", "beta-one" })
        {
            var watchdog = app.RouteKeepAlives[routeId];
            var clientType = app.Config.Routes.Single(route => route.Id == routeId).ClientType;
            registrations.Add(watchdog.RegisterService(KeepAliveFlavorExtensions.FromClientType(clientType)));
            watchdog.Remember(Template("/v1/responses", "{\"model\":\"recent-model\"}"));
            Assert.True(watchdog.RequestPreparation());
            var probe = watchdog.BeginDueProbe()!;
            Assert.NotNull(probe.Complete("Java answer", 100));
            probe.Dispose();
        }

        app.SelectedProvider = "empty";
        app.Notice = "existing notice";
        Assert.False(app.PollPreparationEvents());
        Assert.Equal("existing notice", app.Notice);
        foreach (var routeName in new[] { "alpha-one", "beta-one" })
        {
            app.Notice = null;
            Assert.True(app.PollPreparationEvents());
            Assert.Equal($"通道“{routeName}”：准备完成", app.Notice);
            Assert.False(app.PollPreparationEvents());
            Assert.Equal("empty", app.SelectedProvider);
        }

        app.Notice = null;
        Assert.False(app.PollPreparationEvents());
        Assert.False(app.RouteKeepAlives["beta-one"].Enabled);
        registrations.ForEach(registration => registration.Dispose());
    }

    [Fact]
    public void PreparationFailuresKeepRetryingUntilTheUserCancels()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var watchdog = app.RouteKeepAlives["alpha-one"];
        using var service = watchdog.RegisterService(KeepAliveFlavor.Codex);
        watchdog.Remember(Template("/v1/responses", "{\"model\":\"recent-model\"}"));
        Assert.True(watchdog.RequestPreparation());
        var probe = watchdog.BeginDueProbe()!;
        probe.Fail("HTTP 503");
        probe.Dispose();
        app.SelectedProvider = "beta";
        Assert.False(app.PollPreparationEvents());
        Assert.True(watchdog.Snapshot().Preparing);
        Assert.Equal("HTTP 503", watchdog.Snapshot().PreparationLastError);
        app.SelectedProvider = "alpha";
        app.SelectRoute("alpha-one");
        app.CancelSelectedPreparation();
        Assert.True(app.PollPreparationEvents());
        Assert.Equal("通道“alpha-one”：准备已终止", app.Notice);
    }

    [Fact]
    public void KeepaliveHintKeepsTotalsAndLastUsageWhenAutomaticModeIsOff()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var route = app.Config.Routes[1].Clone();
        route.KeepaliveEnabled = false;
        var watchdog = app.RouteKeepAlives["alpha-two"];
        using var service = watchdog.RegisterService(KeepAliveFlavor.Codex);
        Assert.True(watchdog.RequestPreparation());
        var success = watchdog.BeginDueProbe()!;
        Assert.NotNull(success.Complete(null, 52));
        success.Dispose();
        Assert.True(watchdog.RequestPreparation());
        var failure = watchdog.BeginDueProbe()!;
        failure.Fail("fixture failure");
        failure.Dispose();
        var hint = app.KeepAliveHint(route);
        Assert.Contains("成功 1 轮 · 失败 1 轮 · 中断 0 轮", hint);
        Assert.Contains("最近成功用量 52 token", hint);
        Assert.Contains("本会话 0 轮", hint);
        Assert.StartsWith("等待本通道启用 · Claude Code · 本机默认配置 · 模型沿用本机配置\n", hint);
        Assert.Equal("等待通道保活初始化", app.KeepAliveHint(new ProxyRoute { Id = "missing" }));
    }

    [Fact]
    public void KeepAliveInputFallsBackAndClampsBeforeSaving()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        var route = app.SelectedRouteRef()!;
        Assert.Equal("alpha-two", route.Id);
        app.ApplyKeepAliveInput(route.KeepaliveEnabled, "三", route.KeepaliveContextLimit);
        Assert.Equal(7.0, app.SelectedRouteRef()!.KeepaliveIdleMinutes);
        app.ApplyKeepAliveInput(false, "99999", 60000);
        var updated = app.SelectedRouteRef()!;
        Assert.False(updated.KeepaliveEnabled);
        Assert.Equal(ConfigDefaults.MaxKeepaliveIdleMinutes, updated.KeepaliveIdleMinutes);
        Assert.Equal(60000, updated.KeepaliveContextLimit);
        Assert.False(app.RouteKeepAlives["alpha-two"].Enabled);
        Assert.Equal(60000UL, app.RouteKeepAlives["alpha-two"].Snapshot().ContextLimit);
        Assert.Equal("99999", app.KeepAliveMinutes);
    }

    [Fact]
    public void DeletingARunningRouteIsRefused()
    {
        using var fixture = new Fixture();
        var app = fixture.App;
        BindRouteToFreePort(app, "alpha-one");
        app.RefreshServices();
        app.Services["alpha-one"].Start(app.Config.RuntimeConfigFor("alpha-one"), TimeSpan.FromSeconds(5));
        Assert.Equal(1, app.RunningCount());
        app.DeleteRoute("alpha-one");
        Assert.Equal("请先停用通道", app.Notice);
        Assert.Equal(3, app.Config.Routes.Count);
        var editor = app.OpenProviderEditor(0);
        editor.Name = "alpha-renamed";
        Assert.Equal("请先停止引用该服务商的通道", app.CommitProvider(editor));
        app.StopRoute("alpha-one");
        Assert.False(app.Config.Routes[0].DesiredRunning);
        app.Services["alpha-one"].Stop(TimeSpan.FromSeconds(5));
        app.Notice = null;
        app.DeleteRoute("alpha-one");
        Assert.Null(app.Notice);
        Assert.Equal(2, app.Config.Routes.Count);
        Assert.False(app.Services.ContainsKey("alpha-one"));
        Assert.Equal("alpha-two", app.SelectedRoute);
    }
}
