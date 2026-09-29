using System;
using System.Security.Cryptography;

namespace RetryProxy.Core.Config;

/// <summary>
/// 转发通道：每个客户端一条（PRD-供应商管理 P3），一个本地端口对应当前"供应商 · Key"与一套重试/保活参数。
/// </summary>
public sealed class ProxyRoute : IEquatable<ProxyRoute>
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>当前供应商的 ID；为空表示还没有选供应商。</summary>
    public string CurrentProviderId { get; set; } = string.Empty;

    /// <summary>当前 Key 的 ID；为空表示当前供应商还没有 Key，此时请求原样透传客户端自带的凭据。</summary>
    public string CurrentKeyId { get; set; } = string.Empty;

    /// <summary>
    /// 本地口令：32 位十六进制随机串，接管时写进客户端配置代替真实 Key（PRD-供应商管理 §7）。
    /// </summary>
    public string LocalToken { get; set; } = string.Empty;

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

    /// <summary>原样转发客户端的 Accept-Encoding，代理边转发压缩流边解压解析；关闭时要求上游不压缩。</summary>
    public bool PassThroughCompression { get; set; }

    public string LocalUrl => $"http://{ConfigDefaults.ListenHost}:{ListenPort}";

    internal void NormalizeInPlace()
    {
        Id = Id.Trim();
        Name = Name.Trim();
        CurrentProviderId = CurrentProviderId.Trim();
        CurrentKeyId = CurrentKeyId.Trim();
        LocalToken = LocalToken.Trim();
    }

    /// <summary>生成新的本地口令，例：<c>9f86d081884c7d659a2feaa0c55ad015</c>。</summary>
    public static string NewLocalToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public void Validate()
    {
        if (!KeepaliveReasoningEffort.IsSupportedBy(ClientType))
        {
            throw new ConfigException("该客户端不支持所选保活思考强度，请重新选择");
        }

        if (Id.Length == 0)
        {
            throw new ConfigException("转发通道 ID 不能为空");
        }

        if (Name.Length == 0)
        {
            throw new ConfigException("转发通道名称不能为空");
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
            && CurrentProviderId == other.CurrentProviderId
            && CurrentKeyId == other.CurrentKeyId
            && LocalToken == other.LocalToken
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
            && KeepaliveReasoningEffort == other.KeepaliveReasoningEffort
            && KeepaliveContextLimit == other.KeepaliveContextLimit
            && PassThroughCompression == other.PassThroughCompression;
    }

    public override bool Equals(object? obj) => Equals(obj as ProxyRoute);

    public override int GetHashCode() => HashCode.Combine(Id, Name, CurrentProviderId, ListenPort);
}
