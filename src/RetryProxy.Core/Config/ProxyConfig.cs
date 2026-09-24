using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace RetryProxy.Core.Config;

/// <summary>
/// 代理配置（schema 6）。顶层字段镜像当前选中通道，providers/routes 为完整列表。
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

    public List<ProviderEndpoint> Providers { get; set; } = new();

    public List<ProxyRoute> Routes { get; set; } = new();

    public string SelectedRouteId { get; set; } = string.Empty;

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
            Providers.Add(new ProviderEndpoint(name, UpstreamBaseUrl));
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
        var provider = ProviderByName(route.ProviderName);
        if (provider is not null)
        {
            UpstreamBaseUrl = provider.BaseUrl;
        }

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

    public ProviderEndpoint? ProviderByName(string name)
    {
        return Providers.FirstOrDefault(provider =>
            string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 生成某条通道的运行时配置（routes 为空、顶层字段取自该通道），并做完整校验。
    /// </summary>
    public ProxyConfig RuntimeConfigFor(string routeId)
    {
        var route = Routes.FirstOrDefault(candidate => candidate.Id == routeId)
            ?? throw new ConfigException("找不到转发通道");
        var provider = ProviderByName(route.ProviderName)
            ?? throw new ConfigException($"转发通道“{route.Name}”引用的服务商不存在");

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

        var providerNames = new HashSet<string>();
        foreach (var provider in Providers)
        {
            provider.Validate();
            if (!providerNames.Add(provider.Name.ToLowerInvariant()))
            {
                throw new ConfigException($"服务商名称重复：{provider.Name}");
            }

        }

        var routeIds = new HashSet<string>();
        var routeNames = new HashSet<string>();
        var routePorts = new HashSet<int>();
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

            if (!providerNames.Contains(route.ProviderName.ToLowerInvariant()))
            {
                throw new ConfigException($"转发通道“{route.Name}”引用的服务商不存在：{route.ProviderName}");
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
    /// Rust 版首次运行时使用的内置配置：两个服务商、两个通道，以及原有的
    /// 18080/18081 端口和重试参数。
    /// </summary>
    public static ProxyConfig Builtin()
    {
        return new ProxyConfig
        {
            UpstreamBaseUrl = "https://anyrouter.top",
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
            Providers =
            {
                new ProviderEndpoint("anyrouter.top", "https://anyrouter.top"),
                new ProviderEndpoint("sotamodel.net", "https://sotamodel.net"),
            },
            Routes =
            {
                new ProxyRoute
                {
                    Id = "legacy-default",
                    Name = "默认通道",
                    ProviderName = "anyrouter.top",
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
                    Name = "Claude Code",
                    ProviderName = "anyrouter.top",
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
        }.Normalize();
    }

    public ProxyConfig Clone()
    {
        var copy = (ProxyConfig)MemberwiseClone();
        copy.Providers = Providers.Select(provider => provider.Clone()).ToList();
        copy.Routes = Routes.Select(route => route.Clone()).ToList();
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
            && Providers.SequenceEqual(other.Providers)
            && Routes.SequenceEqual(other.Routes)
            && SelectedRouteId == other.SelectedRouteId
            && SchemaVersion == other.SchemaVersion
            && Equals(RuntimeOverrides, other.RuntimeOverrides);
    }

    public override bool Equals(object? obj) => Equals(obj as ProxyConfig);

    public override int GetHashCode() => HashCode.Combine(UpstreamBaseUrl, ListenPort, SelectedRouteId, Routes.Count);
}
