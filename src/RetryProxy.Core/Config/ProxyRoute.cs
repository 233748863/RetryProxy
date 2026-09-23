using System;

namespace RetryProxy.Core.Config;

/// <summary>
/// 转发通道：一个本地端口对应一个服务商与一套重试/保活参数。
/// </summary>
public sealed class ProxyRoute : IEquatable<ProxyRoute>
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ProviderName { get; set; } = string.Empty;

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

    public bool DedicatedPreparation { get; set; }

    public string? ProtectedApiKey { get; set; }

    public string? PreparationModel { get; set; }

    public string LocalUrl => $"http://{ConfigDefaults.ListenHost}:{ListenPort}";

    internal void NormalizeInPlace()
    {
        Id = Id.Trim();
        Name = Name.Trim();
        ProviderName = ProviderName.Trim();
    }

    public void Validate()
    {
        if (Id.Length == 0)
        {
            throw new ConfigException("转发通道 ID 不能为空");
        }

        if (Name.Length == 0)
        {
            throw new ConfigException("转发通道名称不能为空");
        }

        if (ProviderName.Length == 0)
        {
            throw new ConfigException($"转发通道“{Name}”必须选择服务商");
        }

        if (KeepaliveContextLimit == 0)
        {
            throw new ConfigException("保活会话用量阈值必须大于 0");
        }

        if (!double.IsFinite(KeepaliveIdleMinutes)
            || KeepaliveIdleMinutes < ConfigDefaults.MinKeepaliveIdleMinutes
            || KeepaliveIdleMinutes > ConfigDefaults.MaxKeepaliveIdleMinutes)
        {
            throw new ConfigException(
                $"通道“{Name}”：保活空闲时长必须在 {ConfigDefaults.MinKeepaliveIdleMinutes} 到 {ConfigDefaults.MaxKeepaliveIdleMinutes} 分钟之间");
        }

        UrlRules.ValidateRetrySettings(
            ListenPort,
            TimeoutSeconds,
            GenerationTimeoutSeconds,
            TotalTimeoutSeconds,
            BaseDelaySeconds,
            MaxDelaySeconds,
            $"转发通道“{Name}”：");
    }

    public ProxyRoute Clone() => (ProxyRoute)MemberwiseClone();

    public bool Equals(ProxyRoute? other)
    {
        return other is not null
            && Id == other.Id
            && Name == other.Name
            && ProviderName == other.ProviderName
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
            && DedicatedPreparation == other.DedicatedPreparation
            && ProtectedApiKey == other.ProtectedApiKey
            && PreparationModel == other.PreparationModel;
    }

    public override bool Equals(object? obj) => Equals(obj as ProxyRoute);

    public override int GetHashCode() => HashCode.Combine(Id, Name, ProviderName, ListenPort);
}
