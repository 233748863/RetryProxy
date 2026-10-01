using System;
using System.Collections.Generic;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Workspace;

public enum PrepareMode
{
    LocalProvider,
    CustomProvider,
    ListProvider,
}

/// <summary>独立准备任务的编辑副本；提交成功后随任务写入配置文件（见 <see cref="SavedPreparation"/>）。</summary>
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

    public string ProviderId { get; set; } = string.Empty;

    /// <summary>编辑单项任务的 Key；批量新增使用 KeyIds。</summary>
    public string KeyId { get; set; } = string.Empty;

    public List<string> KeyIds { get; set; } = new();

    public int StartedCount { get; internal set; }

    public string? Error { get; set; }

    public void ClearModels()
    {
        Models = Array.Empty<string>();
        SelectedModel = null;
    }

    /// <summary>独立草稿保留原 TaskId；模型获取结果是候选列表，不属于已配置字段。</summary>
    public PreparationDialogState Copy() => new()
    {
        TaskId = TaskId,
        Mode = Mode,
        ClientType = ClientType,
        ProviderId = ProviderId,
        KeyId = KeyId,
        KeyIds = new List<string>(KeyIds),
        ProviderUrl = ProviderUrl,
        ApiKey = ApiKey,
        Models = new List<string>(Models),
        SelectedModel = SelectedModel,
        ReasoningEffort = ReasoningEffort,
        IdleMinutes = IdleMinutes,
    };
}
