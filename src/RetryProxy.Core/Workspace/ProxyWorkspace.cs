using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Service;

namespace RetryProxy.Core.Workspace;

/// <summary>
/// 界面编排层（对应 ui.rs 里 RetryProxyApp 的非绘制部分）：配置与选择同步、每条通道的服务与保活看门狗、
/// 服务商/通道增删改、启停、保活设置与一键准备编排。只在界面线程上访问；
/// 后台线程只会通过 <see cref="SetUiNotifier"/> 设置的回调请求刷新。
/// </summary>
public sealed class ProxyWorkspace
{
    private readonly Action<ProxyConfig>? _save;
    private readonly HashSet<string> _reportedServiceErrors = new();
    private Action? _uiNotifier;

    public ProxyWorkspace(ProxyLogger logger, ProxyConfig config, Action<ProxyConfig>? save)
    {
        Logger = logger;
        Config = config.Clone().Normalize();
        _save = save;
        SelectedRoute = Config.SelectedRoute?.Id ?? string.Empty;
        SelectedProvider = (Config.SelectedRoute is { } route ? Config.ProviderByName(route.ProviderName) : null)?.Name
            ?? Config.Providers.FirstOrDefault()?.Name
            ?? string.Empty;
    }

    public ProxyLogger Logger { get; }

    public ProxyConfig Config { get; private set; }

    /// <summary>测试用：注入的 CLI 命令，让保活看门狗不去找本机 CLI。</summary>
    internal CliCommand? TestCliCommand { get; set; }

    public Dictionary<string, ProxyService> Services { get; } = new();

    public Dictionary<string, KeepAliveWatchdog> RouteKeepAlives { get; } = new();

    public string SelectedProvider { get; set; } = string.Empty;

    public string SelectedRoute { get; set; } = string.Empty;

    /// <summary>保活分钟输入框的文本；切换通道时重置为该通道的值。</summary>
    public string KeepAliveMinutes { get; set; } = string.Empty;

    public PendingPreparation? Pending { get; private set; }

    private string? _notice;

    /// <summary>待显示的提示。赋非空值时触发 <see cref="NoticePosted"/>。</summary>
    public string? Notice
    {
        get => _notice;
        set
        {
            _notice = value;
            if (value is not null)
            {
                NoticePosted?.Invoke(value);
            }
        }
    }

    public event Action<string>? NoticePosted;

    private void AppendNotice(string message)
    {
        Notice = _notice is { } existing ? $"{existing}\n\n{message}" : message;
    }

    // ---------------------------------------------------------------- 通知与保存

    /// <summary>通道状态、请求统计、保活状态变化时的界面刷新回调，会套用到现有与之后创建的服务/看门狗。</summary>
    public void SetUiNotifier(Action? notifier)
    {
        _uiNotifier = notifier;
        foreach (var watchdog in RouteKeepAlives.Values)
        {
            watchdog.SetUiNotifier(notifier);
        }

        foreach (var service in Services.Values)
        {
            service.SetUiNotifier(notifier);
        }
    }

    public void Save()
    {
        if (_save is null)
        {
            return;
        }

        try
        {
            _save(Config);
        }
        catch (Exception error)
        {
            Notice = $"保存失败：{error.Message}";
        }
    }

    // ---------------------------------------------------------------- 服务与选择

    public void RefreshServices()
    {
        foreach (var id in RouteKeepAlives.Keys.Where(id => Config.Routes.All(route => route.Id != id)).ToList())
        {
            RouteKeepAlives.Remove(id);
        }

        foreach (var route in Config.Routes)
        {
            if (Config.ProviderByName(route.ProviderName) is null)
            {
                continue;
            }

            var idle = TimeSpan.FromSeconds(route.KeepaliveIdleMinutes * 60.0);
            if (!RouteKeepAlives.TryGetValue(route.Id, out var watchdog))
            {
                watchdog = TestCliCommand is { } command
                    ? KeepAliveWatchdog.WithCliCommand(route.KeepaliveEnabled, idle, command.Clone())
                    : new KeepAliveWatchdog(route.KeepaliveEnabled, idle);
                watchdog.SetUiNotifier(_uiNotifier);
                RouteKeepAlives[route.Id] = watchdog;
                watchdog.ConfigureFlavor(KeepAliveFlavorExtensions.FromClientType(route.ClientType));
                if (route.DedicatedPreparation && route.ProtectedApiKey is { Length: > 0 } protectedKey)
                {
                    try
                    {
                        var key = PreparationSecret.Unprotect(protectedKey, route.Id);
                        watchdog.RestoreCredential(CliCredential.Create(key, route.LocalUrl, route.PreparationModel));
                    }
                    catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException or PlatformNotSupportedException)
                    {
                        AppendNotice($"通道“{route.Name}”的准备密钥无法读取，请重新输入密钥后准备");
                    }
                }
            }

            watchdog.Configure(route.KeepaliveEnabled, idle);
            watchdog.SetContextLimit((ulong)Math.Max(route.KeepaliveContextLimit, 1));
            watchdog.ConfigureFlavor(KeepAliveFlavorExtensions.FromClientType(route.ClientType));
        }

        foreach (var id in Services.Keys.Where(id => Config.Routes.All(route => route.Id != id)).ToList())
        {
            Services.Remove(id);
        }

        foreach (var route in Config.Routes)
        {
            if (!RouteKeepAlives.TryGetValue(route.Id, out var watchdog))
            {
                continue;
            }

            if (Services.TryGetValue(route.Id, out var existing))
            {
                var replace = (existing.RouteName != route.Name || !ReferenceEquals(existing.KeepAlive, watchdog))
                    && existing.State is ServiceState.Stopped or ServiceState.Error;
                if (!replace)
                {
                    continue;
                }

                var service = ProxyService.WithMetrics(Logger, route.Name, existing.Metrics).WithKeepAliveWatchdog(watchdog);
                service.SetUiNotifier(_uiNotifier);
                Services[route.Id] = service;
            }
            else
            {
                var service = ProxyService.WithDailyStatistics(Logger, route.Id, route.Name).WithKeepAliveWatchdog(watchdog);
                service.SetUiNotifier(_uiNotifier);
                Services[route.Id] = service;
            }
        }

        SyncSelection();
    }

    public IEnumerable<ProxyRoute> VisibleRoutes()
    {
        return Config.Routes.Where(route => string.Equals(route.ProviderName, SelectedProvider, StringComparison.OrdinalIgnoreCase));
    }

    public ProxyRoute? SelectedRouteRef() => VisibleRoutes().FirstOrDefault(route => route.Id == SelectedRoute);

    public void SyncSelection()
    {
        SelectedProvider = (Config.ProviderByName(SelectedProvider)
                ?? (Config.SelectedRoute is { } selected ? Config.ProviderByName(selected.ProviderName) : null)
                ?? Config.Providers.FirstOrDefault())?.Name
            ?? string.Empty;
        SelectedRoute = (SelectedRouteRef() ?? VisibleRoutes().FirstOrDefault())?.Id ?? string.Empty;
        if (SelectedRoute.Length > 0)
        {
            Config.SelectedRouteId = SelectedRoute;
        }
        else if (Config.SelectedRoute is null)
        {
            Config.SelectedRouteId = Config.Routes.FirstOrDefault()?.Id ?? string.Empty;
        }

        Config = Config.Clone().Normalize();
        SyncKeepAliveBuffer();
    }

    public void SelectProvider(string providerName)
    {
        SelectedProvider = providerName;
        SyncSelection();
        Save();
    }

    public ServiceState RouteState(string routeId)
    {
        return Services.TryGetValue(routeId, out var service) ? service.State : ServiceState.Stopped;
    }

    public void SelectRoute(string routeId)
    {
        SelectedRoute = routeId;
        SyncSelection();
        Save();
    }

    /// <summary>按通道 ID 选中，同时把服务商切到它所属的服务商（运行状态页的通道下拉跨服务商）。</summary>
    public void SelectRouteAcrossProviders(string routeId)
    {
        var route = Config.Routes.FirstOrDefault(candidate => candidate.Id == routeId);
        if (route is null)
        {
            return;
        }

        SelectedProvider = route.ProviderName;
        SelectRoute(routeId);
    }

    private void SyncKeepAliveBuffer()
    {
        KeepAliveMinutes = UiText.TrimFloat(SelectedRouteRef()?.KeepaliveIdleMinutes ?? ConfigDefaults.KeepaliveIdleMinutes);
    }

    // ---------------------------------------------------------------- 启停

    public void StartRoute(string routeId)
    {
        if (Config.Routes.All(route => route.Id != routeId))
        {
            return;
        }

        ProxyConfig runtime;
        try
        {
            runtime = Config.RuntimeConfigFor(routeId);
        }
        catch (ConfigException error)
        {
            Notice = error.Message;
            return;
        }

        if (!Services.TryGetValue(routeId, out var service))
        {
            return;
        }

        try
        {
            if (service.RequestStart(runtime))
            {
                Config = Config.WithRouteRunning(routeId, true);
                Save();
            }
        }
        catch (Exception error)
        {
            Notice = $"启动失败：{error.Message}";
        }
    }

    public void StopRoute(string routeId)
    {
        if (Services.TryGetValue(routeId, out var service))
        {
            service.RequestStop();
        }

        Config = Config.WithRouteRunning(routeId, false);
        Save();
    }

    public void StartAll()
    {
        foreach (var id in Config.Routes.Select(route => route.Id).ToList())
        {
            StartRoute(id);
        }
    }

    public void StopAll()
    {
        foreach (var id in Config.Routes.Select(route => route.Id).ToList())
        {
            StopRoute(id);
        }
    }

    /// <summary>启动时自动拉起上次标记为运行的通道。</summary>
    public void StartDesiredRoutes()
    {
        foreach (var id in Config.Routes.Where(route => route.DesiredRunning).Select(route => route.Id).ToList())
        {
            StartRoute(id);
        }
    }

    public int RunningCount() => Config.Routes.Count(route => RouteState(route.Id) == ServiceState.Running);

    public int ProviderUsage(string providerName)
    {
        return Config.Routes.Count(route => string.Equals(route.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>通道 Error 状态首次出现时提示；启动失败不改 desired_running。</summary>
    public void PollServiceErrors()
    {
        foreach (var route in Config.Routes.ToList())
        {
            if (!Services.TryGetValue(route.Id, out var service))
            {
                continue;
            }

            switch (service.State)
            {
                case ServiceState.Error:
                    if (_reportedServiceErrors.Add(route.Id) && service.StartupError is { } error)
                    {
                        AppendNotice($"通道“{route.Name}”异常：{error}");
                    }

                    break;
                case ServiceState.Running:
                    _reportedServiceErrors.Remove(route.Id);
                    break;
            }
        }
    }

    public void Shutdown()
    {
        foreach (var service in Services.Values)
        {
            service.Stop(TimeSpan.FromSeconds(15));
        }

        Save();
    }

    // ---------------------------------------------------------------- 服务商增删改

    public ProviderEditor OpenProviderEditor(int? index)
    {
        var editor = new ProviderEditor { Index = index };
        if (index is { } current && current < Config.Providers.Count)
        {
            editor.Name = Config.Providers[current].Name;
            editor.Url = Config.Providers[current].BaseUrl;
        }

        return editor;
    }

    /// <summary>提交服务商编辑。返回 null 表示成功，否则为留在对话框内的错误文案。</summary>
    public string? CommitProvider(ProviderEditor editor)
    {
        if (editor.Index is { } index && index < Config.Providers.Count)
        {
            var existing = Config.Providers[index];
            var activeDependency = Config.Routes.Any(route =>
                string.Equals(route.ProviderName, existing.Name, StringComparison.OrdinalIgnoreCase)
                && Services.TryGetValue(route.Id, out var service)
                && service.State is not (ServiceState.Stopped or ServiceState.Error));
            if (activeDependency)
            {
                return "请先停止引用该服务商的通道";
            }
        }

        var provider = new ProviderEndpoint(editor.Name, editor.Url);
        var providerName = provider.Name;
        try
        {
            provider.Validate();
        }
        catch (ConfigException error)
        {
            return error.Message;
        }

        var duplicate = Config.Providers.Where((value, position) => position != editor.Index).Any(value =>
            string.Equals(value.Name, provider.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            return "服务商名称重复";
        }

        var candidate = Config.Clone();
        if (editor.Index is { } editing)
        {
            var old = Config.Providers[editing].Name;
            candidate.Providers[editing] = provider.Clone();
            foreach (var route in candidate.Routes)
            {
                if (string.Equals(route.ProviderName, old, StringComparison.OrdinalIgnoreCase))
                {
                    route.ProviderName = provider.Name;
                }
            }
        }
        else
        {
            candidate.Providers.Add(provider);
        }

        candidate = candidate.Normalize();
        try
        {
            candidate.Validate(false);
        }
        catch (ConfigException error)
        {
            return error.Message;
        }

        if (editor.Index is { } changed)
        {
            var existing = Config.Providers[changed];
            if (existing.Name != candidate.Providers[changed].Name || existing.BaseUrl != candidate.Providers[changed].BaseUrl)
            {
                foreach (var route in Config.Routes)
                {
                    if (string.Equals(route.ProviderName, existing.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        RouteKeepAlives.Remove(route.Id);
                    }
                }
            }
        }

        Config = candidate;
        SelectedProvider = providerName;
        RefreshServices();
        Save();
        return null;
    }

    public void DeleteProvider(int index)
    {
        if (index < 0 || index >= Config.Providers.Count)
        {
            return;
        }

        var provider = Config.Providers[index];
        if (Config.Routes.Any(route => string.Equals(route.ProviderName, provider.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Notice = "该服务商仍被通道使用";
            return;
        }

        Config.Providers.RemoveAt(index);
        var fallback = Math.Min(index, Math.Max(Config.Providers.Count - 1, 0));
        SelectedProvider = fallback < Config.Providers.Count ? Config.Providers[fallback].Name : string.Empty;
        SyncSelection();
        Save();
    }

    public int? SelectedProviderIndex()
    {
        var index = Config.Providers.FindIndex(provider => provider.Name == SelectedProvider);
        return index < 0 ? null : index;
    }

    // ---------------------------------------------------------------- 通道增删改

    /// <summary>打开通道编辑器。新增时没有服务商则提示并返回 null。</summary>
    public RouteEditor? OpenRouteEditor(int? index)
    {
        if (index is null && Config.ProviderByName(SelectedProvider) is null)
        {
            Notice = "请先新增服务商，再创建通道";
            return null;
        }

        var editor = new RouteEditor { Index = index };
        if (index is { } current && current < Config.Routes.Count)
        {
            var route = Config.Routes[current];
            editor.Name = route.Name;
            editor.Provider = route.ProviderName;
            editor.ClientType = route.ClientType;
            editor.Port = route.ListenPort.ToString();
            editor.Retries = route.MaxRetries.ToString();
            editor.Timeout = FormatNumber(route.TimeoutSeconds);
            editor.GenerationTimeout = FormatNumber(route.GenerationTimeoutSeconds);
            editor.TotalTimeout = FormatNumber(route.TotalTimeoutSeconds);
            editor.BaseDelay = FormatNumber(route.BaseDelaySeconds);
            editor.MaxDelay = FormatNumber(route.MaxDelaySeconds);
        }
        else
        {
            var defaults = SelectedRouteRef() ?? new ProxyRoute();
            editor.Provider = SelectedProvider;
            editor.ClientType = null;
            editor.Port = FirstFreePort().ToString();
            editor.Retries = defaults.MaxRetries.ToString();
            editor.Timeout = FormatNumber(defaults.TimeoutSeconds);
            editor.GenerationTimeout = FormatNumber(defaults.GenerationTimeoutSeconds);
            editor.TotalTimeout = FormatNumber(defaults.TotalTimeoutSeconds);
            editor.BaseDelay = FormatNumber(defaults.BaseDelaySeconds);
            editor.MaxDelay = FormatNumber(defaults.MaxDelaySeconds);
        }

        return editor;
    }

    /// <summary>Rust 的 f64 Display：整数不带小数点，其余按最短表示。</summary>
    private static string FormatNumber(double value)
    {
        return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    public int FirstFreePort()
    {
        var used = Config.Routes.Select(route => route.ListenPort).ToHashSet();
        for (var port = ConfigDefaults.ListenPort; port <= 65535; port++)
        {
            if (!used.Contains(port))
            {
                return port;
            }
        }

        return ConfigDefaults.ListenPort;
    }

    /// <summary>配置里未占用且此刻能在本机绑定的最小端口；新通道要马上启动，只看配置不够。</summary>
    public int FirstBindableFreePort()
    {
        var used = Config.Routes.Select(route => route.ListenPort).ToHashSet();
        for (var port = ConfigDefaults.ListenPort; port <= 65535; port++)
        {
            if (used.Contains(port))
            {
                continue;
            }

            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }

        return FirstFreePort();
    }

    /// <summary>把编辑器文本解析成通道；解析失败抛 <see cref="WorkspaceException"/>，消息即界面文案。</summary>
    public ProxyRoute RouteFromEditor(RouteEditor editor)
    {
        // 编辑弹窗里没有启停和保活这两组开关，它们在首页卡片上，改名改端口时要原样保留。
        var existing = editor.Index is { } index && index < Config.Routes.Count ? Config.Routes[index] : new ProxyRoute();
        var route = new ProxyRoute
        {
            Id = editor.Index is { } editing && editing < Config.Routes.Count ? Config.Routes[editing].Id : Guid.NewGuid().ToString("N"),
            Name = editor.Name,
            ProviderName = editor.IsEditing ? existing.ProviderName : editor.Provider,
            ClientType = editor.ClientType ?? throw new WorkspaceException("请选择客户端：Codex 或 Claude Code"),
            ListenPort = ParseInt(editor.Port, "端口必须是整数"),
            MaxRetries = ParseLong(editor.Retries, "重试次数必须是整数"),
            TimeoutSeconds = ParseDouble(editor.Timeout, "超时必须是数字"),
            TotalTimeoutSeconds = ParseDouble(editor.TotalTimeout, "总等待上限必须是数字"),
            GenerationTimeoutSeconds = ParseDouble(editor.GenerationTimeout, "等待生成上限必须是数字"),
            BaseDelaySeconds = ParseDouble(editor.BaseDelay, "最小间隔必须是数字"),
            MaxDelaySeconds = ParseDouble(editor.MaxDelay, "最大间隔必须是数字"),
            DesiredRunning = existing.DesiredRunning,
            KeepaliveEnabled = existing.KeepaliveEnabled,
            KeepaliveIdleMinutes = existing.KeepaliveIdleMinutes,
            KeepaliveContextLimit = existing.KeepaliveContextLimit,
            DedicatedPreparation = existing.DedicatedPreparation,
            ProtectedApiKey = existing.ProtectedApiKey,
            PreparationModel = existing.PreparationModel,
        };
        route.NormalizeInPlace();
        return route;
    }

    private static int ParseInt(string text, string message)
    {
        return int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new WorkspaceException(message);
    }

    private static long ParseLong(string text, string message)
    {
        return long.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new WorkspaceException(message);
    }

    private static double ParseDouble(string text, string message)
    {
        return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new WorkspaceException(message);
    }

    /// <summary>提交通道编辑。返回 null 表示成功，否则为留在对话框内的错误文案。</summary>
    public string? CommitRoute(RouteEditor editor)
    {
        ProxyRoute route;
        try
        {
            route = RouteFromEditor(editor);
            route.Validate();
        }
        catch (WorkspaceException error)
        {
            return error.Message;
        }
        catch (ConfigException error)
        {
            return error.Message;
        }

        var candidate = Config.Clone();
        if (editor.Index is { } index && index < candidate.Routes.Count)
        {
            candidate.Routes[index] = route.Clone();
        }
        else
        {
            candidate.Routes.Add(route.Clone());
        }

        candidate.SelectedRouteId = route.Id;
        candidate = candidate.Normalize();
        try
        {
            candidate.Validate(false);
        }
        catch (ConfigException error)
        {
            return error.Message;
        }

        SelectedProvider = route.ProviderName;
        SelectedRoute = route.Id;
        Config = candidate;
        RefreshServices();
        Save();
        return null;
    }

    public void DeleteRoute(string routeId)
    {
        var index = Config.Routes.FindIndex(route => route.Id == routeId);
        if (index < 0)
        {
            return;
        }

        if (Services.TryGetValue(routeId, out var service) && service.State is not (ServiceState.Stopped or ServiceState.Error))
        {
            Notice = "请先停用通道";
            return;
        }

        var deletedRoute = Config.Routes[index];
        Config.Routes.RemoveAt(index);
        if (deletedRoute.DedicatedPreparation && Config.Routes.All(other => other.ProviderName != deletedRoute.ProviderName))
        {
            Config.Providers.RemoveAll(provider => provider.Name == deletedRoute.ProviderName);
        }
        Services.Remove(routeId);
        SyncSelection();
        Save();
    }

    // ---------------------------------------------------------------- 保活

    public void SetKeepAlive(string routeId, bool enabled, double idleMinutes, long contextLimit)
    {
        var route = Config.Routes.FirstOrDefault(candidate => candidate.Id == routeId);
        if (route is null)
        {
            return;
        }

        route.KeepaliveEnabled = enabled;
        route.KeepaliveIdleMinutes = idleMinutes;
        route.KeepaliveContextLimit = contextLimit;
        if (RouteKeepAlives.TryGetValue(routeId, out var watchdog))
        {
            watchdog.Configure(enabled, TimeSpan.FromSeconds(idleMinutes * 60.0));
            watchdog.SetContextLimit((ulong)Math.Max(contextLimit, 1));
        }

        Config = Config.Clone().Normalize();
        Save();
    }

    /// <summary>
    /// 保活行的输入落地：分钟文本认不出来时回落原值，再夹到 0.5–1440；只有真正变化才写配置。
    /// </summary>
    public void ApplyKeepAliveInput(bool enabled, string minutesText, long contextLimit)
    {
        var route = SelectedRouteRef();
        KeepAliveMinutes = minutesText;
        if (route is null)
        {
            return;
        }

        var minutes = Math.Clamp(
            UiText.ParseIdleMinutes(minutesText) ?? route.KeepaliveIdleMinutes,
            ConfigDefaults.MinKeepaliveIdleMinutes,
            ConfigDefaults.MaxKeepaliveIdleMinutes);
        if (enabled != route.KeepaliveEnabled || minutes != route.KeepaliveIdleMinutes || contextLimit != route.KeepaliveContextLimit)
        {
            SetKeepAlive(route.Id, enabled, minutes, contextLimit);
        }
    }

    public string KeepAliveHint(ProxyRoute route)
    {
        var running = RouteState(route.Id) == ServiceState.Running;
        if (!RouteKeepAlives.TryGetValue(route.Id, out var watchdog))
        {
            return "等待通道保活初始化";
        }

        var snapshot = watchdog.Snapshot();
        var model = snapshot.Model ?? "模型沿用本机配置";
        var configuration = snapshot.WithKey ? "指定 Key 经本通道" : "本机默认配置";
        string status;
        if (!running)
        {
            status = "等待本通道启用";
        }
        else if (snapshot.Preparing && snapshot.ActiveRequests > 0)
        {
            status = $"等待 {snapshot.ActiveRequests} 个真实请求结束后准备";
        }
        else if (snapshot.Preparing && snapshot.Probing)
        {
            status = $"正在进行第 {snapshot.PreparationAttempts} 次准备，失败自动重试";
        }
        else if (snapshot.Preparing && snapshot.PreparationRetryAfter is { } retryAfter)
        {
            status = $"第 {snapshot.PreparationAttempts} 次未完成，{Math.Ceiling(retryAfter.TotalSeconds):F0} 秒后继续准备";
        }
        else if (snapshot.Preparing)
        {
            status = "准备已排队，即将发送";
        }
        else if (snapshot.ActiveRequests > 0)
        {
            status = $"{snapshot.ActiveRequests} 个真实请求处理中，保活让行";
        }
        else if (snapshot.Probing)
        {
            status = $"正在进行第 {snapshot.Turns + 1} 轮问答";
        }
        else if (!route.KeepaliveEnabled)
        {
            status = "自动保活已关闭 · 可一键准备";
        }
        else
        {
            var remaining = watchdog.Idle - watchdog.IdleFor();
            status = $"距下轮 {UiText.FormatDurationCn(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining)}";
        }

        var session = snapshot.SessionId is { } id ? (id.Length > 8 ? id.Substring(0, 8) : id) : "待新建";
        var tokens = snapshot.ContextTokens?.ToString() ?? "待回复";
        var lastTokens = snapshot.LastSuccess is { } success
            ? (success.ContextTokens is { } count ? $"{count} token" : "未返回用量")
            : "尚无成功回复";
        return $"{status} · {snapshot.Flavor.Label()} · {configuration} · {model}\n"
            + $"本次运行：成功 {snapshot.Totals.Completed} 轮 · 失败 {snapshot.Totals.Failed} 轮 · 中断 {snapshot.Totals.Interrupted} 轮 · 最近成功用量 {lastTokens}\n"
            + $"会话 {session} · 本会话 {snapshot.Turns} 轮 · 会话用量 {tokens}/{route.KeepaliveContextLimit} token";
    }

    /// <summary>保活提示里是否有随时间变化的内容（倒计时或准备中），需要每秒刷新一次。</summary>
    public bool KeepAliveHintChangesOverTime()
    {
        var route = SelectedRouteRef();
        if (route is null || !RouteKeepAlives.TryGetValue(route.Id, out var watchdog))
        {
            return false;
        }

        if (RouteState(route.Id) != ServiceState.Running)
        {
            return false;
        }

        var snapshot = watchdog.Snapshot();
        if (snapshot.Preparing)
        {
            return true;
        }

        return route.KeepaliveEnabled && snapshot.ActiveRequests == 0 && !snapshot.Probing;
    }

    // ---------------------------------------------------------------- 一键准备

    /// <summary>点小箭头弹出选项窗；通道未选或未运行时直接提示，不弹窗。</summary>
    public PreparationDialogState? OpenPrepareDialog()
    {
        var route = SelectedRouteRef();
        if (route is null)
        {
            Notice = "请先选择一条通道，再一键准备";
            return null;
        }

        if (!(Services.TryGetValue(route.Id, out var service) && service.IsRunning))
        {
            Notice = $"请先启用通道“{route.Name}”，再一键准备";
            return null;
        }

        var provider = Config.Providers
            .FirstOrDefault(candidate => !string.Equals(candidate.Name, route.ProviderName, StringComparison.OrdinalIgnoreCase))
            ?.Name;
        var previousSeparateRoute = Config.Routes.Any(other => other.Id != route.Id
            && other.ClientType == route.ClientType
            && string.Equals(other.ProviderName, route.ProviderName, StringComparison.OrdinalIgnoreCase))
            && route.Name.StartsWith($"{route.ProviderName} · {route.ClientType.Label()}", StringComparison.OrdinalIgnoreCase);
        string apiKey = string.Empty;
        if (route.DedicatedPreparation && route.ProtectedApiKey is { Length: > 0 } encrypted)
        {
            try
            {
                apiKey = PreparationSecret.Unprotect(encrypted, route.Id);
            }
            catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException or PlatformNotSupportedException)
            {
                Notice = $"通道“{route.Name}”的准备密钥无法读取，请重新输入";
            }
        }
        return new PreparationDialogState
        {
            RouteId = route.Id,
            Mode = route.DedicatedPreparation || previousSeparateRoute ? PrepareMode.CurrentRoute : PrepareMode.Default,
            Provider = provider,
            NewProviderName = Config.ProviderByName(route.ProviderName)?.Name ?? string.Empty,
            NewProviderUrl = Config.ProviderByName(route.ProviderName)?.BaseUrl ?? string.Empty,
            ApiKey = apiKey,
            SelectedModel = route.PreparationModel,
        };
    }

    /// <summary>主按钮：按通道当前配置立即准备，不弹窗也不改变已选配置。</summary>
    public void PrepareSelectedRoute() => PrepareRoute(null, false, null);

    /// <summary>
    /// 提交弹窗选择。返回 true 表示弹窗应关闭；返回 false 时 <see cref="PreparationDialogState.Error"/> 里是原因，弹窗保持打开。
    /// </summary>
    public bool SubmitPrepareDialog(PreparationDialogState dialog)
    {
        var route = Config.Routes.FirstOrDefault(candidate => candidate.Id == dialog.RouteId);
        if (route is null)
        {
            Notice = "通道已不存在，无法准备";
            return true;
        }

        switch (dialog.Mode)
        {
            case PrepareMode.Default:
                if (RouteKeepAlives.TryGetValue(route.Id, out var currentWatchdog)
                    && currentWatchdog.Snapshot().Preparing)
                {
                    currentWatchdog.CancelPreparation();
                }

                if (route.DedicatedPreparation)
                {
                    route.DedicatedPreparation = false;
                    route.ProtectedApiKey = null;
                    route.PreparationModel = null;
                    Save();
                }
                PrepareRoute(dialog.RouteId, true, null);
                return true;
            default:
                var reason = SubmitSeparatePreparation(route, dialog);
                if (reason is null)
                {
                    return true;
                }

                dialog.Error = reason;
                return false;
        }
    }

    /// <summary>按弹窗输入确定上游地址；专用通道使用独立的服务商记录。</summary>
    public ProviderEndpoint SeparateTargetProvider(PreparationDialogState dialog)
    {
        if (dialog.Mode != PrepareMode.CurrentRoute && dialog.Provider is { } name)
        {
            return Config.ProviderByName(name)?.Clone() ?? throw new WorkspaceException($"服务商“{name}”已不存在，请重新选择");
        }

        var provider = new ProviderEndpoint(dialog.NewProviderName, dialog.NewProviderUrl);
        if (provider.BaseUrl.Length == 0)
        {
            throw new WorkspaceException("请输入服务商地址");
        }

        if (provider.Name.Length == 0)
        {
            throw new WorkspaceException("请输入服务商名称");
        }

        try
        {
            provider.Validate();
        }
        catch (ConfigException error)
        {
            throw new WorkspaceException(error.Message);
        }

        return provider;
    }

    /// <summary>
    /// 单独准备始终新建独立通道，端口取配置未用且当前能绑定的最小值。
    /// </summary>
    public SeparateChannelPlan SeparateChannelPlanFor(ProxyRoute origin, string providerName)
    {
        var baseName = $"{providerName} · {origin.ClientType.Label()}";
        var name = baseName;
        var suffix = 2;
        while (Config.Routes.Any(route => string.Equals(route.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} {suffix}";
            suffix++;
        }

        return new SeparateChannelPlan.Create(name, FirstBindableFreePort());
    }

    /// <summary>选项窗里目标服务商一确定就把承接通道算出来给用户看。</summary>
    public string PlanText(PreparationDialogState dialog)
    {
        var route = Config.Routes.FirstOrDefault(candidate => candidate.Id == dialog.RouteId);
        if (dialog.Mode == PrepareMode.CurrentRoute)
        {
            return "将更新本通道的服务商地址、密钥和模型，不影响其他通道。";
        }

        if (route is null)
        {
            return string.Empty;
        }

        var name = dialog.Provider ?? dialog.NewProviderName.Trim();
        return name.Length == 0
            ? "将为输入的服务商新建独立通道。"
            : SeparateChannelPlanFor(route, name) is SeparateChannelPlan.Create create
                ? $"将新建独立通道“{create.Name}”，监听 http://127.0.0.1:{create.Port}，不影响其他通道。"
                : string.Empty;
    }

    private static string UniqueProviderName(ProxyConfig config, string name)
    {
        if (config.ProviderByName(name) is null)
        {
            return name;
        }

        var baseName = $"{name} · 独立";
        var candidate = baseName;
        for (var suffix = 2; config.ProviderByName(candidate) is not null; suffix++)
        {
            candidate = $"{baseName} {suffix}";
        }

        return candidate;
    }

    /// <summary>单独准备：落地服务商与通道、启动通道、切到该通道页面，并登记待提交的准备。返回错误文案或 null。</summary>
    public string? SubmitSeparatePreparation(ProxyRoute origin, PreparationDialogState dialog)
    {
        var apiKey = dialog.ApiKey;
        if (apiKey.Trim().Length == 0)
        {
            return "请输入该供应商的 API Key";
        }

        ProviderEndpoint provider;
        try
        {
            provider = SeparateTargetProvider(dialog);
        }
        catch (WorkspaceException error)
        {
            return error.Message;
        }

        var model = dialog.SelectedModel?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return "请输入模型名称，或获取模型后选择一个用于准备";
        }

        var editCurrent = dialog.Mode == PrepareMode.CurrentRoute;
        var candidate = Config.Clone();
        var currentProvider = Config.ProviderByName(origin.ProviderName)!;
        var sharedProvider = candidate.Routes.Any(route => route.Id != origin.Id
            && string.Equals(route.ProviderName, origin.ProviderName, StringComparison.OrdinalIgnoreCase));
        if (editCurrent && !sharedProvider)
        {
            candidate.Providers.RemoveAll(existing => existing.Name == currentProvider.Name);
        }

        provider.Name = UniqueProviderName(candidate, provider.Name);
        candidate.Providers.Add(provider.Clone());

        ProxyRoute target;
        if (editCurrent)
        {
            target = candidate.Routes.First(route => route.Id == origin.Id);
            target.ProviderName = provider.Name;
        }
        else
        {
            var plan = (SeparateChannelPlan.Create)SeparateChannelPlanFor(origin, provider.Name);
            target = new ProxyRoute
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = plan.Name,
                ProviderName = provider.Name,
                ClientType = origin.ClientType,
                ListenPort = plan.Port,
                MaxRetries = origin.MaxRetries,
                TimeoutSeconds = origin.TimeoutSeconds,
                GenerationTimeoutSeconds = origin.GenerationTimeoutSeconds,
                TotalTimeoutSeconds = origin.TotalTimeoutSeconds,
                BaseDelaySeconds = origin.BaseDelaySeconds,
                MaxDelaySeconds = origin.MaxDelaySeconds,
                DesiredRunning = true,
                KeepaliveEnabled = origin.KeepaliveEnabled,
                KeepaliveIdleMinutes = origin.KeepaliveIdleMinutes,
                KeepaliveContextLimit = origin.KeepaliveContextLimit,
            };
            candidate.Routes.Add(target);
        }

        CliCredential credential;
        try
        {
            credential = CliCredential.Create(apiKey, target.LocalUrl, model);
        }
        catch (CliException error)
        {
            return error.Message;
        }

        try
        {
            target.ProtectedApiKey = PreparationSecret.Protect(credential.ApiKey, target.Id);
        }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            return "无法安全保存 API Key，请检查当前 Windows 用户权限";
        }

        target.PreparationModel = model;
        target.DedicatedPreparation = true;

        candidate.SelectedRouteId = target.Id;
        candidate = candidate.Normalize();
        try
        {
            candidate.Validate(false);
        }
        catch (ConfigException error)
        {
            return error.Message;
        }

        if (editCurrent && currentProvider.BaseUrl != provider.BaseUrl
            && Services.TryGetValue(target.Id, out var previousService)
            && previousService.State is not (ServiceState.Stopped or ServiceState.Error))
        {
            previousService.Stop(TimeSpan.FromSeconds(15));
            if (previousService.State is not (ServiceState.Stopped or ServiceState.Error))
            {
                return $"通道“{target.Name}”尚未停止，请稍后重试修改服务商地址";
            }
        }

        Config = candidate;
        SelectedProvider = provider.Name;
        SelectedRoute = target.Id;
        RefreshServices();
        Save();
        Logger.Info($"通道“{origin.Name}”发起单独准备：目标服务商“{provider.Name}”，经通道“{target.Name}”（{target.LocalUrl}）转发，其他通道不受影响");
        if (editCurrent && RouteKeepAlives.TryGetValue(target.Id, out var watchdog)
            && watchdog.Snapshot().Preparing)
        {
            watchdog.CancelPreparation();
        }

        if (!(Services.TryGetValue(target.Id, out var service) && service.IsRunning))
        {
            StartRoute(target.Id);
            if (_notice is { } notice)
            {
                _notice = null;
                return $"通道“{target.Name}”无法启动：{notice}";
            }
        }

        Pending = new PendingPreparation
        {
            RouteId = target.Id,
            Credential = credential,
            ProviderName = provider.Name,
            OriginRouteName = origin.Name,
        };
        PollPendingPreparation();
        return null;
    }

    /// <summary>新通道监听成功后才把 Key 交给它准备；启动失败或被停用则放弃并提示。</summary>
    public void PollPendingPreparation()
    {
        if (Pending is not { } pending)
        {
            return;
        }

        var route = Config.Routes.FirstOrDefault(candidate => candidate.Id == pending.RouteId);
        if (route is null)
        {
            Pending = null;
            Notice = "承接单独准备的通道已不存在，本次准备取消";
            return;
        }

        if (!Services.TryGetValue(route.Id, out var service))
        {
            Pending = null;
            return;
        }

        switch (service.State)
        {
            case ServiceState.Starting:
                break;
            case ServiceState.Running:
                Pending = null;
                try
                {
                    if (service.RequestPreparationWith(pending.Credential))
                    {
                        Logger.Info($"通道“{route.Name}”一键准备已提交：后台 {route.ClientType.Label()} 使用输入的 Key 经 {route.LocalUrl} 转发到服务商“{pending.ProviderName}”，本机客户端配置与通道“{pending.OriginRouteName}”不受影响；本通道自动保活也使用这把 Key");
                    }
                }
                catch (InvalidOperationException error)
                {
                    Notice = $"通道“{route.Name}”无法准备：{error.Message}";
                }

                break;
            case ServiceState.Error:
                Pending = null;
                Logger.Warn($"通道“{route.Name}”未能启动，单独准备取消：{service.StartupError ?? "通道启动失败"}");
                break;
            default:
                Pending = null;
                Notice = $"通道“{route.Name}”未运行，单独准备已取消";
                break;
        }
    }

    /// <summary>
    /// <paramref name="switchConfiguration"/> 为 false 时沿用通道当前配置；为 true 时切换配置（<paramref name="credential"/> 为 null 即切回默认）。
    /// </summary>
    public void PrepareRoute(string? routeId, bool switchConfiguration, CliCredential? credential)
    {
        var route = routeId is null ? SelectedRouteRef() : Config.Routes.FirstOrDefault(candidate => candidate.Id == routeId);
        if (route is null)
        {
            Notice = "请先选择一条通道，再一键准备";
            return;
        }

        if (!(Services.TryGetValue(route.Id, out var service) && service.IsRunning))
        {
            Notice = $"请先启用通道“{route.Name}”，再一键准备";
            return;
        }

        var withKey = switchConfiguration
            ? credential is not null
            : RouteKeepAlives.TryGetValue(route.Id, out var watchdog) && watchdog.Snapshot().WithKey;
        bool submitted;
        try
        {
            submitted = switchConfiguration ? service.RequestPreparationWith(credential) : service.RequestPreparation();
        }
        catch (InvalidOperationException error)
        {
            Notice = $"通道“{route.Name}”无法准备：{error.Message}";
            return;
        }

        if (!submitted)
        {
            return;
        }

        Logger.Info(withKey
            ? $"通道“{route.Name}”一键准备已提交：后台 {route.ClientType.Label()} 使用已指定的 Key 经本通道转发，不改动本机客户端配置；自动保活也使用这把 Key"
            : $"通道“{route.Name}”一键准备已提交，沿用本机 CLI 默认配置，正在等待完整回复；自动保活也使用默认配置");
    }

    public void CancelSelectedPreparation()
    {
        if (RouteKeepAlives.TryGetValue(SelectedRoute, out var watchdog))
        {
            watchdog.CancelPreparation();
        }
    }

    /// <summary>取走一条准备结果并写成提示；已有提示未消费时不取。返回是否取到。</summary>
    public bool PollPreparationEvents()
    {
        if (_notice is not null)
        {
            return false;
        }

        foreach (var route in Config.Routes)
        {
            if (!RouteKeepAlives.TryGetValue(route.Id, out var watchdog))
            {
                continue;
            }

            var result = watchdog.TakePreparationResult();
            if (result is null)
            {
                continue;
            }

            string notice;
            switch (result)
            {
                case PreparationResult.Ready:
                    notice = $"通道“{route.Name}”：准备完成";
                    Logger.Info(notice);
                    break;
                case PreparationResult.Failed failed:
                    notice = $"通道“{route.Name}”：准备未完成，{failed.Reason}";
                    Logger.Warn(notice);
                    break;
                default:
                    notice = $"通道“{route.Name}”：准备已终止";
                    Logger.Info(notice);
                    break;
            }

            Notice = notice;
            return true;
        }

        return false;
    }
}
