using System;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Client;

/// <summary>客户端配置中可保存为供应商的部分；输出文本只显示客户端类型，避免带出密钥。</summary>
public sealed class ClientProfile
{
    public ClientType ClientType { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public ClaudeAuthMode AuthMode { get; init; } = ClaudeAuthMode.Bearer;
    public ProviderModels Models { get; init; } = new();
    public string ProviderName { get; init; } = string.Empty;
    public string WireApi { get; init; } = string.Empty;
    public bool HasApiKeyHelper { get; init; }
    public bool HasConflictingSettings { get; init; }

    public override string ToString() => $"ClientProfile {{ {ClientType} }}";
}

/// <summary>
/// 写入客户端的目标。例：接管时 UseProxy=true，写入本机端口和口令；退出时 false，写当前 Key。
/// ModelsOnly 只同步模型，始终保留文件当前的地址、凭据和其他字段。
/// </summary>
public sealed class ClientConfigRequest
{
    public required ChannelSnapshot Channel { get; init; }
    public int ListenPort { get; init; }
    public bool UseProxy { get; init; }
    public bool ModelsOnly { get; init; }

    public ProviderModels EffectiveModels
    {
        get
        {
            var models = Channel.Models.Clone();
            if (Channel.ModelOverride is { Model.Length: > 0 } modelOverride)
            {
                models.Model = modelOverride.Model;
                models.Context1M = modelOverride.Context1M;
            }
            return models;
        }
    }

    public override string ToString() => $"ClientConfigRequest {{ {Channel.ClientType}, UseProxy={UseProxy}, ModelsOnly={ModelsOnly} }}";
}

/// <summary>纯文本编辑器：不读写磁盘；磁盘原子写入、备份和回滚由 ClientConfigStore 统一处理。</summary>
public interface IClientConfigEditor
{
    ClientType ClientType { get; }
    ClientProfile Read(string text, string? authText = null);
    string Write(string text, ClientConfigRequest request);
}

internal enum ClientConfigStage
{
    ReadProfile, ReadAuth, CheckHash, ReadOriginal, Edit, VerifyOriginal, Backup,
    WriteTemporary, VerifyBeforeReplace, Replace, VerifyWritten, PersistState,
    RollbackRead, RollbackVerify, RollbackDelete,
}

/// <summary>仅含可直接显示的中文说明，不附带原始文件片段或解析器错误（其中可能有 Key）。</summary>
public sealed class ClientConfigException(string message) : Exception(message)
{
    // 阶段来自固定枚举，错误只保留数值。例如 Replace/0x80070020 能定位共享冲突，不暴露文件名。
    internal string? Diagnostic { get; private init; }

    internal ClientConfigException(string message, ClientConfigStage stage, Exception error,
        ClientConfigStage? rollbackStage = null, Exception? rollbackError = null) : this(message)
    {
        Diagnostic = $"阶段 {stage}，HRESULT 0x{error.HResult:X8}";
        if (rollbackStage is { } failedStage && rollbackError is not null)
            Diagnostic += $"；恢复阶段 {failedStage}，HRESULT 0x{rollbackError.HResult:X8}";
    }
}
