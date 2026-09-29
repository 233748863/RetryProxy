using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace RetryProxy.Core.Config;

/// <summary>
/// 代理配置（schema 7）。顶层字段镜像当前选中通道，providers/routes 为完整列表：
/// 供应商按客户端分开并带 Key，每个客户端一条通道（PRD-供应商管理 §8）。
/// 通过 <see cref="ProxyConfigJsonConverter"/> 以固定键序的 snake_case JSON 存取，
/// 读取时自动执行旧版本迁移。
/// </summary>
[JsonConverter(typeof(ProxyConfigJsonConverter))]
public sealed class ProxyConfig : IEquatable<ProxyConfig>
{
    public string UpstreamBaseUrl { get; set; } = string.Empty;

    public ClientType ClientType { get; set; } = ClientType.Codex;

    public int ListenPort { get; set; } = ConfigDefaults.ListenPort;

    public long MaxRetries { get; set; } = ConfigDefaults.MaxRetries;

    public double TimeoutSeconds { get; set; } = ConfigDefaults.TimeoutSeconds;

    public double GenerationTimeoutSeconds { get; set; } = ConfigDefaults.GenerationTimeoutSeconds;

    public double TotalTimeoutSeconds { get; set; } = ConfigDefaults.TotalTimeoutSeconds;

    public double BaseDelaySeconds { get; set; } = ConfigDefaults.BaseDelaySeconds;

    public double MaxDelaySeconds { get; set; } = ConfigDefaults.MaxDelaySeconds;

    public bool DesiredRunning { get; set; }

    public bool KeepaliveEnabled { get; set; }

    public double KeepaliveIdleMinutes { get; set; } = ConfigDefaults.KeepaliveIdleMinutes;

    public long KeepaliveContextLimit { get; set; } = ConfigDefaults.KeepaliveContextLimit;

    public ReasoningEffort KeepaliveReasoningEffort { get; set; }

    /// <summary>见 <see cref="ProxyRoute.PassThroughCompression"/>。</summary>
    public bool PassThroughCompression { get; set; }

    public List<ProviderEndpoint> Providers { get; set; } = new();

    public List<ProxyRoute> Routes { get; set; } = new();

    public string SelectedRouteId { get; set; } = string.Empty;

    /// <summary>每个客户端的接管状态；<see cref="EnsureClientRoutes"/> 保证两个客户端都有一项。</summary>
    public Dictionary<ClientType, ClientTakeoverState> ClientTakeover { get; set; } = new();

    public int SchemaVersion { get; set; } = ConfigDefaults.CurrentSchemaVersion;

    /// <summary>
    /// 环境变量带来的临时覆盖；不参与持久化。
    /// </summary>
    public RouteRuntimeOverrides? RuntimeOverrides { get; set; }

    public string ListenHost => ConfigDefaults.ListenHost;

    public string LocalUrl => $"http://{ListenHost}:{ListenPort}";

    /// <summary>
    /// 规整字段并把选中通道镜像到顶层。返回自身以便链式调用；会原地修改。
    /// </summary>
    public ProxyConfig Normalize()
    {
        UpstreamBaseUrl = UrlRules.NormalizeBaseUrl(UpstreamBaseUrl);
        SelectedRouteId = SelectedRouteId.Trim();
        foreach (var provider in Providers)
        {
            provider.NormalizeInPlace();
        }

        foreach (var route in Routes)
        {
            route.NormalizeInPlace();
        }

        if (Routes.Count > 0)
        {
            if (SelectedRouteId.Length == 0)
            {
                SelectedRouteId = Routes[0].Id;
            }

            MirrorSelectedRoute();
            return this;
        }

        if (UpstreamBaseUrl.Length == 0 && Providers.Count > 0)
        {
            UpstreamBaseUrl = Providers[0].BaseUrl;
        }

        if (UpstreamBaseUrl.Length > 0 && !Providers.Any(provider => provider.BaseUrl == UpstreamBaseUrl))
        {
            var name = UrlRules.GeneratedProviderName(UpstreamBaseUrl, Providers);
            Providers.Add(new ProviderEndpoint(name, UpstreamBaseUrl) { Id = NewId(), ClientType = ClientType });
        }

        ApplyRuntimeOverrides(string.Empty);
        return this;
    }

    private void MirrorSelectedRoute()
    {
        var route = SelectedRoute;
        if (route is null)
        {
            return;
        }

        ListenPort = route.ListenPort;
        ClientType = route.ClientType;
        MaxRetries = route.MaxRetries;
        TimeoutSeconds = route.TimeoutSeconds;
        GenerationTimeoutSeconds = route.GenerationTimeoutSeconds;
        TotalTimeoutSeconds = route.TotalTimeoutSeconds;
        BaseDelaySeconds = route.BaseDelaySeconds;
        MaxDelaySeconds = route.MaxDelaySeconds;
        DesiredRunning = route.DesiredRunning;
        KeepaliveEnabled = route.KeepaliveEnabled;
        KeepaliveIdleMinutes = route.KeepaliveIdleMinutes;
        KeepaliveContextLimit = route.KeepaliveContextLimit;
        KeepaliveReasoningEffort = route.KeepaliveReasoningEffort;
        PassThroughCompression = route.PassThroughCompression;
        UpstreamBaseUrl = ProviderById(route.CurrentProviderId)?.BaseUrl ?? string.Empty;
        ApplyRuntimeOverrides(route.Id);
    }

    private void ApplyRuntimeOverrides(string routeId)
    {
        var overrides = RuntimeOverrides;
        if (overrides is null || overrides.RouteId != routeId)
        {
            return;
        }

        if (overrides.UpstreamBaseUrl is not null)
        {
            UpstreamBaseUrl = UrlRules.NormalizeBaseUrl(overrides.UpstreamBaseUrl);
        }

        if (overrides.ListenPort is { } listenPort)
        {
            ListenPort = listenPort;
        }

        if (overrides.MaxRetries is { } maxRetries)
        {
            MaxRetries = maxRetries;
        }

        if (overrides.TimeoutSeconds is { } timeout)
        {
            TimeoutSeconds = timeout;
        }

        if (overrides.GenerationTimeoutSeconds is { } generationTimeout)
        {
            GenerationTimeoutSeconds = generationTimeout;
        }

        if (overrides.TotalTimeoutSeconds is { } totalTimeout)
        {
            TotalTimeoutSeconds = totalTimeout;
        }

        if (overrides.BaseDelaySeconds is { } baseDelay)
        {
            BaseDelaySeconds = baseDelay;
        }

        if (overrides.MaxDelaySeconds is { } maxDelay)
        {
            MaxDelaySeconds = maxDelay;
        }
    }

    public ProxyRoute? SelectedRoute => Routes.FirstOrDefault(route => route.Id == SelectedRouteId);

    public ProviderEndpoint? ProviderById(string id)
    {
        return id.Length == 0 ? null : Providers.FirstOrDefault(provider => provider.Id == id);
    }

    /// <summary>某客户端的通道；每个客户端最多一条。</summary>
    public ProxyRoute? RouteFor(ClientType clientType) => Routes.FirstOrDefault(route => route.ClientType == clientType);

    public IEnumerable<ProviderEndpoint> ProvidersFor(ClientType clientType) =>
        Providers.Where(provider => provider.ClientType == clientType);

    /// <summary>通道的当前 Key；当前供应商没有 Key 时为 null。</summary>
    public ProviderKey? CurrentKeyOf(ProxyRoute route) => ProviderById(route.CurrentProviderId)?.KeyById(route.CurrentKeyId);

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// 保证两个客户端各有一条通道、每条通道有本地口令、每个客户端有接管状态。
    /// 缺通道时按客户端补建：Codex 优先 18080、Claude Code 优先 18081，端口被占用时顺延。
    /// 返回是否有需要写回的改动（补建通道、生成口令、修正选中通道）。
    /// </summary>
    public bool EnsureClientRoutes()
    {
        var changed = false;
        foreach (var client in new[] { ClientType.Codex, ClientType.Claude })
        {
            if (RouteFor(client) is null)
            {
                var port = client == ClientType.Claude ? 18081 : 18080;
                while (Routes.Any(route => route.ListenPort == port))
                {
                    port++;
                }

                Routes.Add(new ProxyRoute
                {
                    Id = NewId(),
                    Name = client.Label(),
                    ClientType = client,
                    ListenPort = port,
                });
                changed = true;
            }

            // 缺省的接管状态与"未接管"等价，补上不算改动。
            ClientTakeover.TryAdd(client, new ClientTakeoverState());
        }

        foreach (var route in Routes.Where(route => route.LocalToken.Length == 0))
        {
            route.LocalToken = ProxyRoute.NewLocalToken();
            changed = true;
        }

        if (SelectedRoute is null)
        {
            SelectedRouteId = Routes[0].Id;
            changed = true;
        }

        Normalize();
        return changed;
    }

    /// <summary>
    /// 生成某条通道的运行时配置（routes 为空、顶层字段取自该通道），并做完整校验。
    /// </summary>
    public ProxyConfig RuntimeConfigFor(string routeId)
    {
        var route = Routes.FirstOrDefault(candidate => candidate.Id == routeId)
            ?? throw new ConfigException("找不到转发通道");
        var provider = ProviderById(route.CurrentProviderId)
            ?? throw new ConfigException($"请先为 {route.ClientType.Label()} 新增并选择服务商");

        var runtime = new ProxyConfig
        {
            UpstreamBaseUrl = provider.BaseUrl,
            ClientType = route.ClientType,
            ListenPort = route.ListenPort,
            MaxRetries = route.MaxRetries,
            TimeoutSeconds = route.TimeoutSeconds,
            GenerationTimeoutSeconds = route.GenerationTimeoutSeconds,
            TotalTimeoutSeconds = route.TotalTimeoutSeconds,
            BaseDelaySeconds = route.BaseDelaySeconds,
            MaxDelaySeconds = route.MaxDelaySeconds,
            DesiredRunning = route.DesiredRunning,
            KeepaliveEnabled = route.KeepaliveEnabled,
            KeepaliveIdleMinutes = route.KeepaliveIdleMinutes,
            KeepaliveContextLimit = route.KeepaliveContextLimit,
            KeepaliveReasoningEffort = route.KeepaliveReasoningEffort,
            PassThroughCompression = route.PassThroughCompression,
            Providers = Providers.Select(item => item.Clone()).ToList(),
            Routes = new List<ProxyRoute>(),
            SelectedRouteId = string.Empty,
            SchemaVersion = ConfigDefaults.CurrentSchemaVersion,
            RuntimeOverrides = null,
        };
        if (RuntimeOverrides is { } overrides && overrides.RouteId == route.Id)
        {
            runtime.RuntimeOverrides = overrides;
            runtime.ApplyRuntimeOverrides(route.Id);
            runtime.RuntimeOverrides = null;
        }

        runtime.Validate(true);
        return runtime;
    }

    public ProxyConfig WithSelectedRoute(string routeId)
    {
        var value = Clone();
        value.SelectedRouteId = routeId;
        return value.Normalize();
    }

    public ProxyConfig WithRouteRunning(string routeId, bool running)
    {
        var value = Clone();
        foreach (var route in value.Routes)
        {
            if (route.Id == routeId)
            {
                route.DesiredRunning = running;
            }
        }

        return value.Normalize();
    }

    public ProxyConfig WithRunningState(bool running)
    {
        var selected = SelectedRoute;
        if (selected is not null)
        {
            return WithRouteRunning(selected.Id, running);
        }

        var value = Clone();
        value.DesiredRunning = running;
        return value;
    }

    /// <summary>
    /// 校验整份配置；<paramref name="requireUpstream"/> 为 true 时上游地址不能为空。
    /// </summary>
    public void Validate(bool requireUpstream)
    {
        if (KeepaliveContextLimit == 0)
        {
            throw new ConfigException("保活会话用量阈值必须大于 0");
        }

        if (!KeepaliveReasoningEffort.IsSupportedBy(ClientType))
        {
            throw new ConfigException("该客户端不支持所选保活思考强度，请重新选择");
        }

        UrlRules.ValidateRetrySettings(
            ListenPort,
            TimeoutSeconds,
            GenerationTimeoutSeconds,
            TotalTimeoutSeconds,
            BaseDelaySeconds,
            MaxDelaySeconds,
            string.Empty);

        var providerIds = new HashSet<string>();
        var providerNames = new HashSet<(ClientType, string)>();
        foreach (var provider in Providers)
        {
            provider.Validate();
            // 例外：运行时配置（routes 为空）里由上游地址生成或复制来的服务商可以没有 ID。
            if (provider.Id.Length == 0 && Routes.Count > 0)
            {
                throw new ConfigException($"服务商“{provider.Name}”缺少 ID");
            }

            if (provider.Id.Length > 0 && !providerIds.Add(provider.Id))
            {
                throw new ConfigException($"服务商 ID 重复：{provider.Id}");
            }

            // 名称只要求在同一客户端内唯一，例：Codex 与 Claude Code 可以各有一个 "Any"。
            if (!providerNames.Add((provider.ClientType, provider.Name.ToLowerInvariant())))
            {
                throw new ConfigException($"服务商名称重复：{provider.Name}");
            }
        }

        var routeIds = new HashSet<string>();
        var routeNames = new HashSet<string>();
        var routePorts = new HashSet<int>();
        var routeClients = new HashSet<ClientType>();
        foreach (var route in Routes)
        {
            route.Validate();
            if (!routeIds.Add(route.Id))
            {
                throw new ConfigException($"转发通道 ID 重复：{route.Id}");
            }

            if (!routeNames.Add(route.Name.ToLowerInvariant()))
            {
                throw new ConfigException($"转发通道名称重复：{route.Name}");
            }

            if (!routePorts.Add(route.ListenPort))
            {
                throw new ConfigException($"本地端口重复：{route.ListenPort}");
            }

            if (!routeClients.Add(route.ClientType))
            {
                throw new ConfigException($"{route.ClientType.Label()} 只能有一条通道");
            }

            if (route.CurrentProviderId.Length == 0)
            {
                if (route.CurrentKeyId.Length > 0)
                {
                    throw new ConfigException($"通道“{route.Name}”选了 Key 却没有选服务商");
                }

                continue;
            }

            var provider = ProviderById(route.CurrentProviderId)
                ?? throw new ConfigException($"通道“{route.Name}”的当前服务商不存在");
            if (provider.ClientType != route.ClientType)
            {
                throw new ConfigException($"服务商“{provider.Name}”属于 {provider.ClientType.Label()}，不能用于 {route.ClientType.Label()}");
            }

            if (route.CurrentKeyId.Length > 0 && provider.KeyById(route.CurrentKeyId) is null)
            {
                throw new ConfigException($"通道“{route.Name}”的当前 Key 不存在");
            }
        }

        if (Routes.Count > 0 && !routeIds.Contains(SelectedRouteId))
        {
            throw new ConfigException("当前选中的转发通道不存在");
        }

        if (RuntimeOverrides is { ListenPort: { } overridePort } overrides
            && routeIds.Contains(overrides.RouteId)
            && Routes.Any(route => route.Id != overrides.RouteId && route.ListenPort == overridePort))
        {
            throw new ConfigException($"本地端口重复：{overridePort}");
        }

        if (UpstreamBaseUrl.Length == 0)
        {
            if (requireUpstream)
            {
                throw new ConfigException("请先新增并选择服务商");
            }

            return;
        }

        UrlRules.ValidateBaseUrl(UpstreamBaseUrl, "上游基础地址");
    }

    /// <summary>
    /// 首次运行时使用的内置配置：只有 Codex（18080）与 Claude Code（18081）两条通道，沿用 Rust 版的
    /// 重试参数；不再预置服务商（PRD-供应商管理 §8），由首次使用向导或手动新增。
    /// </summary>
    public static ProxyConfig Builtin()
    {
        var config = new ProxyConfig
        {
            ClientType = ClientType.Codex,
            ListenPort = 18080,
            MaxRetries = 100,
            TimeoutSeconds = 600.0,
            GenerationTimeoutSeconds = ConfigDefaults.GenerationTimeoutSeconds,
            TotalTimeoutSeconds = ConfigDefaults.TotalTimeoutSeconds,
            BaseDelaySeconds = 0.5,
            MaxDelaySeconds = 8.0,
            DesiredRunning = true,
            KeepaliveEnabled = false,
            KeepaliveIdleMinutes = ConfigDefaults.KeepaliveIdleMinutes,
            KeepaliveContextLimit = ConfigDefaults.KeepaliveContextLimit,
            Routes =
            {
                new ProxyRoute
                {
                    Id = "legacy-default",
                    Name = ClientType.Codex.Label(),
                    ListenPort = 18080,
                    MaxRetries = 100,
                    TimeoutSeconds = 600.0,
                    GenerationTimeoutSeconds = ConfigDefaults.GenerationTimeoutSeconds,
                    TotalTimeoutSeconds = ConfigDefaults.TotalTimeoutSeconds,
                    BaseDelaySeconds = 0.5,
                    MaxDelaySeconds = 8.0,
                    DesiredRunning = true,
                },
                new ProxyRoute
                {
                    Id = "2da608c46f0842039fdf8ad07e46cf20",
                    Name = ClientType.Claude.Label(),
                    ClientType = ClientType.Claude,
                    ListenPort = 18081,
                    MaxRetries = 200,
                    TimeoutSeconds = 300.0,
                    GenerationTimeoutSeconds = ConfigDefaults.GenerationTimeoutSeconds,
                    TotalTimeoutSeconds = ConfigDefaults.TotalTimeoutSeconds,
                    BaseDelaySeconds = 0.5,
                    MaxDelaySeconds = 1.0,
                    DesiredRunning = false,
                },
            },
            SelectedRouteId = "legacy-default",
            SchemaVersion = ConfigDefaults.CurrentSchemaVersion,
            RuntimeOverrides = null,
        };
        config.EnsureClientRoutes();
        return config;
    }

    public ProxyConfig Clone()
    {
        var copy = (ProxyConfig)MemberwiseClone();
        copy.Providers = Providers.Select(provider => provider.Clone()).ToList();
        copy.Routes = Routes.Select(route => route.Clone()).ToList();
        copy.ClientTakeover = ClientTakeover.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        copy.RuntimeOverrides = RuntimeOverrides?.Clone();
        return copy;
    }

    public bool Equals(ProxyConfig? other)
    {
        return other is not null
            && UpstreamBaseUrl == other.UpstreamBaseUrl
            && ClientType == other.ClientType
            && ListenPort == other.ListenPort
            && MaxRetries == other.MaxRetries
            && TimeoutSeconds == other.TimeoutSeconds
            && GenerationTimeoutSeconds == other.GenerationTimeoutSeconds
            && TotalTimeoutSeconds == other.TotalTimeoutSeconds
            && BaseDelaySeconds == other.BaseDelaySeconds
            && MaxDelaySeconds == other.MaxDelaySeconds
            && DesiredRunning == other.DesiredRunning
            && KeepaliveEnabled == other.KeepaliveEnabled
            && KeepaliveIdleMinutes == other.KeepaliveIdleMinutes
            && KeepaliveContextLimit == other.KeepaliveContextLimit
            && KeepaliveReasoningEffort == other.KeepaliveReasoningEffort
            && PassThroughCompression == other.PassThroughCompression
            && Providers.SequenceEqual(other.Providers)
            && Routes.SequenceEqual(other.Routes)
            && SelectedRouteId == other.SelectedRouteId
            && ClientTakeover.Count == other.ClientTakeover.Count
            && ClientTakeover.All(pair => other.ClientTakeover.TryGetValue(pair.Key, out var state) && pair.Value.Equals(state))
            && SchemaVersion == other.SchemaVersion
            && Equals(RuntimeOverrides, other.RuntimeOverrides);
    }

    public override bool Equals(object? obj) => Equals(obj as ProxyConfig);

    public override int GetHashCode() => HashCode.Combine(UpstreamBaseUrl, ListenPort, SelectedRouteId, Routes.Count);
}
