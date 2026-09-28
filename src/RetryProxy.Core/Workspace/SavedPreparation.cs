namespace RetryProxy.Core.Workspace;

/// <summary>
/// 一项准备任务写入 User\config.json 的内容（preparations 数组的一个元素）。
/// 只存设置，不存服务、端口与会话：重启软件后任务以“已停止”出现，由用户手动开始。
/// 例：{"id":"3f2a…","number":1,"clientType":"codex","providerSource":"custom","providerUrl":"https://api.example.com",
///      "apiKey":"sk-…","model":"gpt-5","reasoningEffort":"high","idleMinutes":"5"}
/// </summary>
public sealed class SavedPreparation
{
    public const string LocalSource = "local";
    public const string CustomSource = "custom";

    public string Id { get; set; } = string.Empty;

    /// <summary>任务编号，对应标题与日志里的“准备 N”。</summary>
    public int Number { get; set; }

    /// <summary><c>codex</c> / <c>claude</c>。</summary>
    public string ClientType { get; set; } = string.Empty;

    /// <summary><see cref="LocalSource"/>（本机客户端当前供应商）/ <see cref="CustomSource"/>（手动配置）。</summary>
    public string ProviderSource { get; set; } = string.Empty;

    /// <summary>手动配置时是填写的供应商地址；本机供应商时是上次开始读到的地址，只用于显示，开始时会重新读取。</summary>
    public string ProviderUrl { get; set; } = string.Empty;

    /// <summary>
    /// 手动配置的 API Key，按用户决定（2026-09-28）明文保存，重启后可直接开始；
    /// 本机供应商的密钥不保存，每次开始时从本机客户端配置重新读取。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary><c>default</c> / <c>low</c> / … / <c>ultra</c>，与通道配置的写法一致。</summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>保活间隔分钟数，保存对话框里提交成功的文本，例如 <c>7.5</c>。</summary>
    public string IdleMinutes { get; set; } = string.Empty;
}
