using RetryProxy.Core.Config;

namespace RetryProxy.Core.Workspace;

/// <summary>新增/编辑服务商对话框的字段。</summary>
public sealed class ProviderEditor
{
    /// <summary>编辑的服务商下标；新增时为 null。</summary>
    public int? Index { get; init; }

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>服务商所属客户端；只有新增时可选，编辑时沿用原值。</summary>
    public ClientType ClientType { get; set; }

    public bool IsEditing => Index is not null;
}

/// <summary>
/// 编辑通道对话框的字段（全部为文本，提交时再解析）。每个客户端固定一条通道，只能编辑；
/// 名称与客户端只做展示。
/// </summary>
public sealed class RouteEditor
{
    /// <summary>编辑的通道下标。</summary>
    public int Index { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>保存后通道使用的服务商 ID，取自界面上选中的服务商。</summary>
    public string Provider { get; init; } = string.Empty;

    public string ProviderName { get; init; } = string.Empty;

    /// <summary>保存后是否会换成另一个服务商，对话框据此提示。</summary>
    public bool ProviderChanged { get; init; }

    public ClientType ClientType { get; init; }

    public string Port { get; set; } = string.Empty;

    public string Retries { get; set; } = string.Empty;

    public string Timeout { get; set; } = string.Empty;

    public string GenerationTimeout { get; set; } = string.Empty;

    public string TotalTimeout { get; set; } = string.Empty;

    public string BaseDelay { get; set; } = string.Empty;

    public string MaxDelay { get; set; } = string.Empty;

    public bool PassThroughCompression { get; set; }

    /// <summary>新抽屉一次提交通道与保活设置；旧对话框留空时沿用通道原值。</summary>
    public bool? KeepaliveEnabled { get; set; }

    public double? KeepaliveIdleMinutes { get; set; }

    public long? KeepaliveContextLimit { get; set; }

    public ReasoningEffort? KeepaliveReasoningEffort { get; set; }
}

/// <summary>编辑器字段解析失败时抛出，消息即界面文案。</summary>
public sealed class WorkspaceException : System.Exception
{
    public WorkspaceException(string message) : base(message)
    {
    }
}
