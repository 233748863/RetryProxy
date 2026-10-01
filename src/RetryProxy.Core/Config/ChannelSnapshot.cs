using System;
using System.Collections.Generic;
using RetryProxy.Core.Cli;

namespace RetryProxy.Core.Config;

/// <summary>
/// 通道在某一时刻的"供应商 · Key"与重试参数（PRD-供应商管理 §9）。代理在每次尝试开头读取一份，
/// 切换 Key、改供应商或改参数时整体换成新的一份；已经发出的尝试继续用旧的那份。
/// 例：Claude Code 通道当前是 "Any · 主号"：地址 https://anyrouter.top，所有进入该通道的请求按 Bearer 注入主号的密钥。
/// </summary>
public sealed class ChannelSnapshot
{
    public ClientType ClientType { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderName { get; init; } = string.Empty;

    public string KeyId { get; init; } = string.Empty;

    public string KeyName { get; init; } = string.Empty;

    /// <summary>当前 Key 的密钥；为空表示当前供应商还没有 Key。</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>仅 Claude：注入 Key 用 Authorization: Bearer 还是 x-api-key；Codex 固定 Bearer。</summary>
    public ClaudeAuthMode AuthMode { get; init; } = ClaudeAuthMode.Bearer;

    public string UpstreamBaseUrl { get; init; } = string.Empty;

    /// <summary>写入客户端的占位口令，用于识别配置是否已接管；普通通道统一注入当前 Key，不以它鉴权。后台准备代理仍校验口令。</summary>
    public string LocalToken { get; init; } = string.Empty;

    public ProviderModels Models { get; init; } = new();

    /// <summary>当前 Key 的模型覆盖；为空表示沿用供应商的模型设置。</summary>
    public KeyModelOverride? ModelOverride { get; init; }

    /// <summary>
    /// 仅 Codex：已知模型（供应商模型、各 Key 的覆盖模型、最近一次「获取模型」的结果，不区分大小写）。
    /// 请求的模型在其中时不改写。
    /// </summary>
    public IReadOnlySet<string> KnownModels { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public long MaxRetries { get; init; } = ConfigDefaults.MaxRetries;

    public double TimeoutSeconds { get; init; } = ConfigDefaults.TimeoutSeconds;

    public double GenerationTimeoutSeconds { get; init; } = ConfigDefaults.GenerationTimeoutSeconds;

    public double TotalTimeoutSeconds { get; init; } = ConfigDefaults.TotalTimeoutSeconds;

    public double BaseDelaySeconds { get; init; } = ConfigDefaults.BaseDelaySeconds;

    public double MaxDelaySeconds { get; init; } = ConfigDefaults.MaxDelaySeconds;

    public bool PassThroughCompression { get; init; }

    public bool HasKey => ApiKey.Length > 0;

    /// <summary>
    /// 日志里的"供应商 · Key"，例：<c>Any · 主号</c>；没有 Key 时只写供应商名称；
    /// 没有供应商名称（后台临时准备代理、测试）时为空，日志不写这一段。
    /// </summary>
    public string Label => ProviderName.Length == 0 ? string.Empty : HasKey ? $"{ProviderName} · {KeyName}" : ProviderName;

    /// <summary>是否仍是同一个"供应商 · Key"。换了就算切换：未输出的请求改用新 Key 重发，保活会话重置。</summary>
    public bool SameKeyAs(ChannelSnapshot other) => ProviderId == other.ProviderId && KeyId == other.KeyId;

    /// <summary>由运行时配置生成：只有地址与参数，没有 Key 和本地口令（后台临时准备代理、测试）。</summary>
    public static ChannelSnapshot FromRuntime(ProxyConfig config) => new()
    {
        ClientType = config.ClientType,
        UpstreamBaseUrl = config.UpstreamBaseUrl,
        MaxRetries = config.MaxRetries,
        TimeoutSeconds = config.TimeoutSeconds,
        GenerationTimeoutSeconds = config.GenerationTimeoutSeconds,
        TotalTimeoutSeconds = config.TotalTimeoutSeconds,
        BaseDelaySeconds = config.BaseDelaySeconds,
        MaxDelaySeconds = config.MaxDelaySeconds,
        PassThroughCompression = config.PassThroughCompression,
    };

    /// <summary>同一份快照换上另一套 Key 与口令（后台临时准备代理用：Key 来自准备任务，口令是随机生成的访问密钥）。</summary>
    public ChannelSnapshot WithKey(string apiKey, string localToken, ClaudeAuthMode authMode) => new()
    {
        ClientType = ClientType,
        ProviderId = ProviderId,
        ProviderName = ProviderName,
        KeyId = KeyId,
        KeyName = KeyName,
        ApiKey = apiKey,
        AuthMode = authMode,
        UpstreamBaseUrl = UpstreamBaseUrl,
        LocalToken = localToken,
        Models = Models,
        ModelOverride = ModelOverride,
        KnownModels = KnownModels,
        MaxRetries = MaxRetries,
        TimeoutSeconds = TimeoutSeconds,
        GenerationTimeoutSeconds = GenerationTimeoutSeconds,
        TotalTimeoutSeconds = TotalTimeoutSeconds,
        BaseDelaySeconds = BaseDelaySeconds,
        MaxDelaySeconds = MaxDelaySeconds,
        PassThroughCompression = PassThroughCompression,
    };

    /// <summary>调试输出绝不带出密钥与口令。</summary>
    public override string ToString() => $"ChannelSnapshot {{ {Label}, {UpstreamBaseUrl} }}";
}
