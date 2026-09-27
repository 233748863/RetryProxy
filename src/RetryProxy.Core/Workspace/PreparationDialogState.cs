using System;
using System.Collections.Generic;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Workspace;

public enum PrepareMode
{
    LocalProvider,
    CustomProvider,
}

/// <summary>独立准备任务的编辑副本；只保存在本次运行的内存中。</summary>
public sealed class PreparationDialogState
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");

    public PrepareMode Mode { get; set; } = PrepareMode.LocalProvider;

    public ClientType ClientType { get; set; } = ClientType.Codex;

    public string ProviderUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public IReadOnlyList<string> Models { get; set; } = Array.Empty<string>();

    public string? SelectedModel { get; set; }

    public ReasoningEffort ReasoningEffort { get; set; }

    public string IdleMinutes { get; set; } = "5";

    /// <summary>Claude Code 准备时按 Claude Code 的 TLS 指纹连供应商；Codex 忽略此项。</summary>
    public bool ClaudeTlsFingerprint { get; set; }

    public string? Error { get; set; }

    public void ClearModels()
    {
        Models = Array.Empty<string>();
        SelectedModel = null;
    }

    internal PreparationDialogState Copy() => new()
    {
        TaskId = TaskId,
        Mode = Mode,
        ClientType = ClientType,
        ProviderUrl = ProviderUrl,
        ApiKey = ApiKey,
        SelectedModel = SelectedModel,
        ReasoningEffort = ReasoningEffort,
        IdleMinutes = IdleMinutes,
        ClaudeTlsFingerprint = ClaudeTlsFingerprint,
    };
}
