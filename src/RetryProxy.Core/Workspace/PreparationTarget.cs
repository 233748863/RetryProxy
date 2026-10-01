using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Workspace;

/// <summary>准备任务绑定的供应商与 Key；只保存 ID，不复制已管理的密钥。</summary>
public readonly record struct PreparationKeyRef(string ProviderId, string KeyId)
{
    public bool IsEmpty => string.IsNullOrEmpty(ProviderId) || string.IsNullOrEmpty(KeyId);
}

/// <summary>
/// 每次开始准备时由供应商配置解析出的目标。例：当前 Key 切换后，下一次开始读取新 Key，
/// 已运行的任务继续使用原目标。DefaultModel 是包含 Claude [1M] 设置的实际模型名。
/// </summary>
public sealed class PreparationTarget
{
    public required ClientType ClientType { get; init; }
    public required PreparationKeyRef Key { get; init; }
    public required string ProviderName { get; init; }
    public required string KeyName { get; init; }
    public required CliCredential Credential { get; init; }
    public string DefaultModel { get; init; } = string.Empty;

    public override string ToString() => $"PreparationTarget {{ {ClientType}, {ProviderName} · {KeyName} }}";
}
