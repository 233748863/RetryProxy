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
public sealed partial class ProxyWorkspace
{
    private readonly Action<ProxyConfig>? _save;
    private readonly HashSet<string> _reportedServiceErrors = new();
    /// <summary>仅本次运行有效：用户手动停止的通道不被自动启动流程再次拉起。</summary>
    private readonly HashSet<string> _manuallyStoppedRoutes = new();
    private bool _startupRequested;
    private Action? _uiNotifier;

    public ProxyWorkspace(ProxyLogger logger, ProxyConfig config, Action<ProxyConfig>? save)
    {
        Logger = logger;
        Config = config.Clone().Normalize();
        _save = save;
        SelectedRoute = Config.SelectedRoute?.Id ?? string.Empty;
        SelectedClient = Config.SelectedRoute?.ClientType ?? Config.ClientType;
        SelectedProvider = ProviderForRoute(Config.SelectedRoute)?.Id ?? string.Empty;
    }

    public ProxyLogger Logger { get; }

    public ProxyConfig Config { get; private set; }

    /// <summary>测试用：注入的 CLI 命令，让保活看门狗不去找本机 CLI。</summary>
    internal CliCommand? TestCliCommand { get; set; }

    public Dictionary<string, ProxyService> Services { get; } = new();

    public Dictionary<string, KeepAliveWatchdog> RouteKeepAlives { get; } = new();

    /// <summary>各页面共用的客户端选择；即使该客户端没有供应商也保留。</summary>
    public ClientType SelectedClient { get; private set; }

    private string _selectedProvider = string.Empty;
    private string _selectedRoute = string.Empty;

    /// <summary>兼容旧界面的服务商选择；显式赋值仍会切到该服务商的客户端。</summary>
    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            _selectedProvider = value;
            if (Config.ProviderById(value) is { } provider)
            {
                SelectedClient = provider.ClientType;
            }
        }
    }

    public string SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            _selectedRoute = value;
            if (Config.Routes.FirstOrDefault(route => route.Id == value) is { } route)
            {
                SelectedClient = route.ClientType;
            }
        }
    }

    /// <summary>最近一次切换实际改投的请求数；每次尝试切换（含失败与原地切换）先清零。</summary>
    public int LastSwitchResentCount { get; private set; }

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

    /// <summary>兼容旧界面：当前客户端的通道，不依赖该客户端是否已有供应商。</summary>
    public IEnumerable<ProxyRoute> VisibleRoutes() => Config.Routes.Where(route => route.ClientType == SelectedClient);

    /// <summary>通道的当前服务商；还没选时取同客户端的第一个服务商。</summary>
    private ProviderEndpoint? ProviderForRoute(ProxyRoute? route)
    {
        return route is null ? null : Config.ProviderById(route.CurrentProviderId) ?? Config.ProvidersFor(route.ClientType).FirstOrDefault();
    }

    public ProxyRoute? SelectedRouteRef() => Config.RouteFor(SelectedClient);

    public void SyncSelection()
    {
        var route = SelectedRouteRef();
        // 空客户端也有自己的通道；只在同客户端内修复供应商选择，不回退到另一客户端。
        if (Config.ProviderById(_selectedProvider)?.ClientType != SelectedClient)
        {
            _selectedProvider = ProviderForRoute(route)?.Id ?? string.Empty;
        }

        _selectedRoute = route?.Id ?? string.Empty;
        if (route is not null)
        {
            Config.SelectedRouteId = route.Id;
        }

        Config = Config.Clone().Normalize();
        SyncKeepAliveBuffer();
    }

    public void SelectClient(ClientType clientType)
    {
        var route = Config.RouteFor(clientType);
        if (route is null)
        {
            return;
        }

        var candidate = Config.WithSelectedRoute(route.Id);
        if (SaveCandidate(candidate) is { } error)
        {
            Notice = error;
            return;
        }

        SelectedClient = clientType;
        SyncSelection();
        _uiNotifier?.Invoke();
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
        ChannelSnapshot snapshot;
        try
        {
            runtime = Config.RuntimeConfigFor(routeId);
            snapshot = SnapshotFor(routeId);
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
            if (service.RequestStart(runtime, snapshot))
            {
                _manuallyStoppedRoutes.Remove(routeId);
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
        _manuallyStoppedRoutes.Add(routeId);
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

    /// <summary>自动启动已有供应商的通道，忽略历史启停标记；本次手动停止的通道保持停止。</summary>
    public void StartDesiredRoutes()
    {
        _startupRequested = true;
        RefreshServices();
        foreach (var id in Config.Routes.Where(route => !_manuallyStoppedRoutes.Contains(route.Id)
                         && Config.ProviderById(route.CurrentProviderId) is not null)
                     .Select(route => route.Id).ToList())
        {
            StartRoute(id);
        }
    }

    public int RunningCount() => Config.Routes.Count(route => RouteState(route.Id) == ServiceState.Running);

    // ---------------------------------------------------------------- 切换与热更新

    private ChannelSnapshot SnapshotFor(string routeId) => Config.SnapshotFor(routeId);

    /// <summary>
    /// 把通道的最新快照交给正在运行的服务，之后的每次尝试立即使用（PRD-供应商管理 §5.3）。
    /// 换了"供应商 · Key"时返回改用新 Key 重发的请求数；通道未运行、没换 Key 或配置无效时返回 null。
    /// </summary>
    private int? PushSnapshot(string routeId)
    {
        if (!Services.TryGetValue(routeId, out var service) || service.State is not (ServiceState.Starting or ServiceState.Running))
        {
            return null;
        }

        try
        {
            return service.UpdateSnapshot(SnapshotFor(routeId));
        }
        catch (ConfigException)
        {
            return null;
        }
    }

    /// <summary>日志与提示里的"供应商 · Key"，例：<c>Any · 主号</c>；没有 Key 时只写供应商名称。</summary>
    private string KeyLabel(string providerId, string keyId)
    {
        var provider = Config.ProviderById(providerId);
        if (provider is null)
        {
            return "未选择供应商";
        }

        return provider.KeyById(keyId) is { } key ? $"{provider.Name} · {key.Name}" : provider.Name;
    }

    /// <summary>
    /// 切换通道当前的"供应商 · Key"（PRD-供应商管理 §4.2）：保存配置，运行中的通道立即生效、不重启；
    /// 还没向客户端输出的请求改用新 Key 重发，保活会话重置。<paramref name="keyId"/> 为空时用该供应商的第一个 Key。
    /// 撤销就是再切回原来的"供应商 · Key"。返回 null 表示成功，否则为提示文案。
    /// </summary>
    public string? SwitchKey(string routeId, string providerId, string keyId)
    {
        LastSwitchResentCount = 0;
        var route = Config.Routes.FirstOrDefault(candidate => candidate.Id == routeId);
        if (route is null)
        {
            return "找不到转发通道";
        }

        var provider = Config.ProviderById(providerId);
        if (provider is null || provider.ClientType != route.ClientType)
        {
            return "找不到该供应商";
        }

        if (keyId.Length == 0)
        {
            keyId = provider.Keys.FirstOrDefault()?.Id ?? string.Empty;
        }
        else if (provider.KeyById(keyId) is null)
        {
            return "找不到该 Key";
        }

        if (route.CurrentProviderId == providerId && route.CurrentKeyId == keyId)
        {
            return null;
        }

        var previous = KeyLabel(route.CurrentProviderId, route.CurrentKeyId);
        var candidate = Config.Clone();
        var target = candidate.Routes.First(item => item.Id == routeId);
        target.CurrentProviderId = providerId;
        target.CurrentKeyId = keyId;
        if (SaveCandidate(candidate) is { } error)
        {
            return error;
        }

        ApplySwitch(routeId, route.Name, previous, KeyLabel(providerId, keyId));
        _uiNotifier?.Invoke();
        return null;
    }

    /// <summary>
    /// 换了"供应商 · Key"并保存之后调用：运行中的通道立即改用新快照（服务里顺带重置保活会话）；
    /// 未运行的通道只丢弃原来的后台会话，下次启动时用新 Key 重新建立。最后写切换日志。
    /// </summary>
    private void ApplySwitch(string routeId, string routeName, string previous, string next)
    {
        var resent = PushSnapshot(routeId);
        LastSwitchResentCount = resent ?? 0;
        if (resent is null && RouteKeepAlives.TryGetValue(routeId, out var watchdog))
        {
            watchdog.ResetSession();
        }

        LogSwitch(routeName, previous, next, resent);
    }

    /// <summary>例：<c>[通道代理][Claude Code] 已切换：Any · 主号 → Any · 群号，2 个未输出的请求改用新 Key 重发</c>。</summary>
    private void LogSwitch(string routeName, string previous, string next, int? resent)
    {
        Logger.Route(routeName).Info($"已切换：{previous} → {next}" + (resent is > 0 ? $"，{resent} 个未输出的请求改用新 Key 重发" : string.Empty));
    }

    /// <summary>保存最近一次获取的模型结果；保存成功后运行中的通道立即采用，失败保留旧列表并提示。</summary>
    public void RememberFetchedModels(string providerId, IEnumerable<string> models)
    {
        var candidate = Config.Clone();
        if (candidate.ProviderById(providerId) is not { } provider)
        {
            Notice = "找不到该供应商";
            return;
        }

        provider.FetchedModels = models.ToList();
        if (SaveCandidate(candidate) is { } error)
        {
            Notice = error;
            return;
        }

        foreach (var route in Config.Routes.Where(route => route.CurrentProviderId == providerId))
        {
            PushSnapshot(route.Id);
        }

        _uiNotifier?.Invoke();
    }

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

    /// <summary>
    /// 提交服务商编辑。返回 null 表示成功，否则为留在对话框内的错误文案。
    /// 正在使用它的通道不用停：保存后对之后的尝试立即生效（PRD-供应商管理 §5.3）。
    /// </summary>
    public string? CommitProvider(ProviderEditor editor)
    {
        // 兼容旧对话框：它只编辑名称与地址，没有 Key 输入；新抽屉统一调用 SaveProvider。
        if (editor.Index is { } index && (index < 0 || index >= Config.Providers.Count))
        {
            return "找不到该供应商";
        }

        var provider = editor.Index is { } source
            ? Config.Providers[source].Clone()
            : new ProviderEndpoint { Id = ProxyConfig.NewId(), ClientType = editor.ClientType };
        provider.Name = editor.Name;
        provider.BaseUrl = editor.Url;
        provider.NormalizeInPlace();
        if (Config.Providers.Where((_, position) => position != editor.Index).Any(value =>
                value.ClientType == provider.ClientType && string.Equals(value.Name, provider.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return "服务商名称重复";
        }

        return SaveProviderCore(provider, editor.Index is null, allowEmptyNew: true);
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

        if (RemoveProviderCore(provider.Id, selectNeighbor: true) is { } error)
        {
            Notice = error;
        }
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
            KeepaliveEnabled = editor.KeepaliveEnabled ?? existing.KeepaliveEnabled,
            KeepaliveIdleMinutes = editor.KeepaliveIdleMinutes ?? existing.KeepaliveIdleMinutes,
            KeepaliveContextLimit = editor.KeepaliveContextLimit ?? existing.KeepaliveContextLimit,
            KeepaliveReasoningEffort = editor.KeepaliveReasoningEffort ?? existing.KeepaliveReasoningEffort,
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

    /// <summary>
    /// 提交通道编辑。返回 null 表示成功，否则为留在对话框内的错误文案。
    /// 运行中的通道除端口外都能改，保存后对之后的尝试立即生效；换服务商按切换处理（PRD-供应商管理 §4.2、§5.3）。
    /// </summary>
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

        var existing = Config.Routes[editor.Index];
        var running = RouteState(existing.Id) is not (ServiceState.Stopped or ServiceState.Error);
        if (running && route.ListenPort != existing.ListenPort)
        {
            return "通道运行中不能改端口，请先停用通道";
        }

        var candidate = Config.Clone();
        candidate.Routes[editor.Index] = route.Clone();
        candidate.SelectedRouteId = route.Id;
        var previous = KeyLabel(existing.CurrentProviderId, existing.CurrentKeyId);
        var switched = route.CurrentProviderId != existing.CurrentProviderId || route.CurrentKeyId != existing.CurrentKeyId;
        if (switched)
        {
            LastSwitchResentCount = 0;
        }

        if (SaveCandidate(candidate) is { } saveError)
        {
            return saveError;
        }

        SelectedProvider = route.CurrentProviderId;
        SelectedRoute = route.Id;
        RefreshServices();
        if (switched)
        {
            ApplySwitch(route.Id, route.Name, previous, KeyLabel(route.CurrentProviderId, route.CurrentKeyId));
        }
        else if (running)
        {
            PushSnapshot(route.Id);
            Logger.Route(route.Name).Info("通道参数已更新，之后的尝试立即使用新参数");
        }

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

        var candidate = Config.Clone();
        var updated = candidate.Routes.First(item => item.Id == routeId);
        updated.KeepaliveEnabled = enabled;
        updated.KeepaliveIdleMinutes = idleMinutes;
        updated.KeepaliveContextLimit = contextLimit;
        updated.KeepaliveReasoningEffort = effort;
        if (SaveCandidate(candidate) is { } error)
        {
            Notice = error;
            return;
        }

        if (RouteKeepAlives.TryGetValue(routeId, out var watchdog))
        {
            watchdog.Configure(enabled, TimeSpan.FromSeconds(idleMinutes * 60.0));
            watchdog.SetContextLimit((ulong)Math.Max(contextLimit, 1));
            watchdog.SetReasoningEffort(effort);
        }
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
        var configuration = snapshot.WithKey ? "指定 Key 经本通道" : snapshot.ThroughChannel ? "当前 Key 经本通道" : "本机默认配置";
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
