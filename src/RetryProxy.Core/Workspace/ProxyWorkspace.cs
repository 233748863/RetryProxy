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
/// 服务商/通道增删改、启停与通道保活设置。只在界面线程上访问；
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
        SelectedProvider = ProviderForRoute(Config.SelectedRoute)?.Id ?? Config.Providers.FirstOrDefault()?.Id ?? string.Empty;
    }

    public ProxyLogger Logger { get; }

    public ProxyConfig Config { get; private set; }

    /// <summary>测试用：注入的 CLI 命令，让保活看门狗不去找本机 CLI。</summary>
    internal CliCommand? TestCliCommand { get; set; }

    public Dictionary<string, ProxyService> Services { get; } = new();

    public Dictionary<string, KeepAliveWatchdog> RouteKeepAlives { get; } = new();

    /// <summary>界面上选中的服务商 ID。</summary>
    public string SelectedProvider { get; set; } = string.Empty;

    public string SelectedRoute { get; set; } = string.Empty;

    /// <summary>保活分钟输入框的文本；切换通道时重置为该通道的值。</summary>
    public string KeepAliveMinutes { get; set; } = string.Empty;

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
            if (Config.ProviderById(route.CurrentProviderId) is null)
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
            }

            watchdog.Configure(route.KeepaliveEnabled, idle);
            watchdog.SetContextLimit((ulong)Math.Max(route.KeepaliveContextLimit, 1));
            watchdog.ConfigureFlavor(KeepAliveFlavorExtensions.FromClientType(route.ClientType));
            watchdog.SetReasoningEffort(route.KeepaliveReasoningEffort);
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

    /// <summary>选中服务商所属客户端的通道（每个客户端一条）；没有选中服务商时为空。</summary>
    public IEnumerable<ProxyRoute> VisibleRoutes()
    {
        return Config.ProviderById(SelectedProvider) is { } provider
            ? Config.Routes.Where(route => route.ClientType == provider.ClientType)
            : Enumerable.Empty<ProxyRoute>();
    }

    /// <summary>通道的当前服务商；还没选时取同客户端的第一个服务商。</summary>
    private ProviderEndpoint? ProviderForRoute(ProxyRoute? route)
    {
        return route is null ? null : Config.ProviderById(route.CurrentProviderId) ?? Config.ProvidersFor(route.ClientType).FirstOrDefault();
    }

    public ProxyRoute? SelectedRouteRef() => VisibleRoutes().FirstOrDefault(route => route.Id == SelectedRoute);

    public void SyncSelection()
    {
        var route = Config.Routes.FirstOrDefault(candidate => candidate.Id == SelectedRoute) ?? Config.SelectedRoute;
        SelectedProvider = (Config.ProviderById(SelectedProvider) ?? ProviderForRoute(route) ?? Config.Providers.FirstOrDefault())?.Id
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

    public void SelectProvider(string providerId)
    {
        SelectedProvider = providerId;
        SyncSelection();
        Save();
    }

    public ServiceState RouteState(string routeId)
    {
        return Services.TryGetValue(routeId, out var service) ? service.State : ServiceState.Stopped;
    }

    /// <summary>
    /// 按通道 ID 选中。界面上选中的服务商属于别的客户端时，一并切到该通道的服务商，
    /// 例：正在看 Codex 的服务商时从运行概况选 Claude Code 通道 → 服务商视图切到 Claude Code 的当前服务商。
    /// </summary>
    public void SelectRoute(string routeId)
    {
        SelectedRoute = routeId;
        var route = Config.Routes.FirstOrDefault(candidate => candidate.Id == routeId);
        if (route is not null && Config.ProviderById(SelectedProvider)?.ClientType != route.ClientType)
        {
            SelectedProvider = ProviderForRoute(route)?.Id ?? string.Empty;
        }

        SyncSelection();
        Save();
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

    /// <summary>启动时自动拉起上次标记为运行的通道；还没有服务商的通道静默跳过，等用户新增服务商。</summary>
    public void StartDesiredRoutes()
    {
        foreach (var id in Config.Routes.Where(route => route.DesiredRunning && Config.ProviderById(route.CurrentProviderId) is not null)
                     .Select(route => route.Id).ToList())
        {
            StartRoute(id);
        }
    }

    public int RunningCount() => Config.Routes.Count(route => RouteState(route.Id) == ServiceState.Running);

    public int ProviderUsage(string providerId)
    {
        return Config.Routes.Count(route => route.CurrentProviderId == providerId);
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
            editor.ClientType = Config.Providers[current].ClientType;
        }
        else
        {
            editor.ClientType = Config.ProviderById(SelectedProvider)?.ClientType ?? Config.SelectedRoute?.ClientType ?? ClientType.Codex;
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
                route.CurrentProviderId == existing.Id
                && Services.TryGetValue(route.Id, out var service)
                && service.State is not (ServiceState.Stopped or ServiceState.Error));
            if (activeDependency)
            {
                return "请先停止引用该服务商的通道";
            }
        }

        // 编辑时在原服务商上改名称与地址，保留 ID、客户端与 Key；新增时按所选客户端新建。
        var provider = editor.Index is { } source && source < Config.Providers.Count
            ? Config.Providers[source].Clone()
            : new ProviderEndpoint { Id = ProxyConfig.NewId(), ClientType = editor.ClientType };
        provider.Name = editor.Name;
        provider.BaseUrl = editor.Url;
        provider.NormalizeInPlace();
        try
        {
            provider.Validate();
        }
        catch (ConfigException error)
        {
            return error.Message;
        }

        var duplicate = Config.Providers.Where((value, position) => position != editor.Index).Any(value =>
            value.ClientType == provider.ClientType && string.Equals(value.Name, provider.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            return "服务商名称重复";
        }

        var candidate = Config.Clone();
        if (editor.Index is { } editing)
        {
            candidate.Providers[editing] = provider.Clone();
        }
        else
        {
            candidate.Providers.Add(provider);
            // 该客户端的通道还没有服务商时，新增的第一个服务商直接成为它的当前服务商。
            if (candidate.RouteFor(provider.ClientType) is { CurrentProviderId.Length: 0 } route)
            {
                route.CurrentProviderId = provider.Id;
            }
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
                foreach (var route in Config.Routes.Where(route => route.CurrentProviderId == existing.Id))
                {
                    RouteKeepAlives.Remove(route.Id);
                }
            }
        }

        Config = candidate;
        SelectedProvider = provider.Id;
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
        if (Config.Routes.Any(route => route.CurrentProviderId == provider.Id))
        {
            Notice = "该服务商仍被通道使用";
            return;
        }

        Config.Providers.RemoveAt(index);
        var fallback = Math.Min(index, Math.Max(Config.Providers.Count - 1, 0));
        SelectedProvider = fallback < Config.Providers.Count ? Config.Providers[fallback].Id : string.Empty;
        SyncSelection();
        Save();
    }

    public int? SelectedProviderIndex()
    {
        var index = Config.Providers.FindIndex(provider => provider.Id == SelectedProvider);
        return index < 0 ? null : index;
    }

    // ---------------------------------------------------------------- 通道编辑

    /// <summary>
    /// 打开通道编辑器。每个客户端固定一条通道，只能编辑。界面上选中的服务商属于同一客户端时，
    /// 保存后通道改用它；否则沿用通道当前的服务商。下标无效或该客户端还没有服务商时提示并返回 null。
    /// </summary>
    public RouteEditor? OpenRouteEditor(int index)
    {
        var route = index >= 0 && index < Config.Routes.Count ? Config.Routes[index] : null;
        var selected = Config.ProviderById(SelectedProvider);
        var provider = selected is not null && selected.ClientType == route?.ClientType ? selected : ProviderForRoute(route);
        if (route is null || provider is null)
        {
            Notice = "请先新增服务商";
            return null;
        }

        return new RouteEditor
        {
            Index = index,
            Name = route.Name,
            Provider = provider.Id,
            ProviderName = provider.Name,
            ProviderChanged = provider.Id != route.CurrentProviderId,
            ClientType = route.ClientType,
            Port = route.ListenPort.ToString(),
            Retries = route.MaxRetries.ToString(),
            Timeout = FormatNumber(route.TimeoutSeconds),
            GenerationTimeout = FormatNumber(route.GenerationTimeoutSeconds),
            TotalTimeout = FormatNumber(route.TotalTimeoutSeconds),
            BaseDelay = FormatNumber(route.BaseDelaySeconds),
            MaxDelay = FormatNumber(route.MaxDelaySeconds),
            PassThroughCompression = route.PassThroughCompression,
        };
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
        // 启停、保活开关与本地口令属于通道本身，改端口或换服务商时原样保留；名称与客户端固定。
        var existing = editor.Index < Config.Routes.Count ? Config.Routes[editor.Index] : throw new WorkspaceException("找不到转发通道");
        var providerChanged = editor.Provider != existing.CurrentProviderId;
        var route = new ProxyRoute
        {
            Id = existing.Id,
            Name = existing.Name,
            ClientType = existing.ClientType,
            CurrentProviderId = editor.Provider,
            // Key 属于服务商：换服务商时改用新服务商的第一个 Key，没有 Key 时透传客户端凭据。
            CurrentKeyId = providerChanged ? Config.ProviderById(editor.Provider)?.Keys.FirstOrDefault()?.Id ?? string.Empty : existing.CurrentKeyId,
            LocalToken = existing.LocalToken,
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
            KeepaliveReasoningEffort = existing.KeepaliveReasoningEffort,
            PassThroughCompression = editor.PassThroughCompression,
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
        candidate.Routes[editor.Index] = route.Clone();
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

        if (route.CurrentProviderId != Config.Routes[editor.Index].CurrentProviderId)
        {
            // 换了服务商：旧会话属于原上游，保活看门狗按新服务商重建。
            RouteKeepAlives.Remove(route.Id);
        }

        SelectedProvider = route.CurrentProviderId;
        SelectedRoute = route.Id;
        Config = candidate;
        RefreshServices();
        Save();
        return null;
    }

    // ---------------------------------------------------------------- 保活

    public void SetKeepAlive(string routeId, bool enabled, double idleMinutes, long contextLimit, ReasoningEffort? reasoningEffort = null)
    {
        var route = Config.Routes.FirstOrDefault(candidate => candidate.Id == routeId);
        if (route is null)
        {
            return;
        }

        var effort = reasoningEffort ?? route.KeepaliveReasoningEffort;
        if (!effort.IsSupportedBy(route.ClientType))
        {
            throw new WorkspaceException("该客户端不支持所选保活思考强度，请重新选择");
        }

        route.KeepaliveEnabled = enabled;
        route.KeepaliveIdleMinutes = idleMinutes;
        route.KeepaliveContextLimit = contextLimit;
        route.KeepaliveReasoningEffort = effort;
        if (RouteKeepAlives.TryGetValue(routeId, out var watchdog))
        {
            watchdog.Configure(enabled, TimeSpan.FromSeconds(idleMinutes * 60.0));
            watchdog.SetContextLimit((ulong)Math.Max(contextLimit, 1));
            watchdog.SetReasoningEffort(effort);
        }

        Config = Config.Clone().Normalize();
        Save();
    }

    /// <summary>
    /// 保活行的输入落地：分钟文本认不出来时回落原值，再夹到 0.5–1440；只有真正变化才写配置。
    /// </summary>
    public void ApplyKeepAliveInput(bool enabled, string minutesText, long contextLimit, ReasoningEffort? reasoningEffort = null)
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
        var effort = reasoningEffort ?? route.KeepaliveReasoningEffort;
        if (enabled != route.KeepaliveEnabled || minutes != route.KeepaliveIdleMinutes || contextLimit != route.KeepaliveContextLimit
            || effort != route.KeepaliveReasoningEffort)
        {
            SetKeepAlive(route.Id, enabled, minutes, contextLimit, effort);
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
            status = "自动保活已关闭";
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
        if (route is null)
        {
            return false;
        }

        if (!RouteKeepAlives.TryGetValue(route.Id, out var watchdog))
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
            var logger = Logger.Route(route.Name).WithActivity(LogActivity.Preparation);
            switch (result)
            {
                case PreparationResult.Ready:
                    notice = $"通道“{route.Name}”：准备完成";
                    logger.Info(notice);
                    break;
                case PreparationResult.Failed failed:
                    notice = $"通道“{route.Name}”：准备未完成，{failed.Reason}";
                    logger.Warn(notice);
                    break;
                default:
                    notice = $"通道“{route.Name}”：准备已终止";
                    logger.Info(notice);
                    break;
            }

            Notice = notice;
            return true;
        }

        return false;
    }
}
