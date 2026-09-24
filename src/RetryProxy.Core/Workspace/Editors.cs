using RetryProxy.Core.Config;

namespace RetryProxy.Core.Workspace;

/// <summary>新增/编辑服务商对话框的字段。</summary>
public sealed class ProviderEditor
{
    /// <summary>编辑的服务商下标；新增时为 null。</summary>
    public int? Index { get; init; }

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public bool IsEditing => Index is not null;
}

/// <summary>新增/编辑通道对话框的字段（全部为文本，提交时再解析）。</summary>
public sealed class RouteEditor
{
    /// <summary>编辑的通道下标；新增时为 null。</summary>
    public int? Index { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>所属服务商；编辑时只做展示，提交时沿用原通道的服务商。</summary>
    public string Provider { get; set; } = string.Empty;

    public ClientType? ClientType { get; set; }

    public string Port { get; set; } = string.Empty;

    public string Retries { get; set; } = string.Empty;

    public string Timeout { get; set; } = string.Empty;

    public string GenerationTimeout { get; set; } = string.Empty;

    public string TotalTimeout { get; set; } = string.Empty;

    public string BaseDelay { get; set; } = string.Empty;

    public string MaxDelay { get; set; } = string.Empty;

    public bool IsEditing => Index is not null;
}

/// <summary>编辑器字段解析失败时抛出，消息即界面文案。</summary>
public sealed class WorkspaceException : System.Exception
{
    public WorkspaceException(string message) : base(message)
    {
    }
}
