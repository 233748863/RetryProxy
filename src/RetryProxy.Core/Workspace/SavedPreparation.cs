namespace RetryProxy.Core.Workspace;

/// <summary>
/// 一项准备任务写入 User\config.json 的内容（preparations 数组的一个元素）。
/// 只存设置，不存服务、端口与会话。
/// 例：{"id":"3f2a…","number":1,"clientType":"codex","providerSource":"custom","providerUrl":"https://api.example.com",
///      "apiKey":"sk-…","model":"gpt-5","reasoningEffort":"high","idleMinutes":"5","providerId":"","keyId":"","wasRunning":false}
/// </summary>
public sealed class SavedPreparation
{
    /// <summary>跟随客户端的当前 Key，开始时解析。</summary>
    public const string CurrentSource = "current";

    /// <summary>从供应商列表选定的"供应商 ID + Key ID"。</summary>
    public const string ListSource = "list";

    public const string CustomSource = "custom";

    /// <summary>schema 6 的"本机客户端当前供应商"，读取时按 <see cref="CurrentSource"/> 处理（PRD-供应商管理 §4.7）。</summary>
    public const string LegacyLocalSource = "local";

    public string Id { get; set; } = string.Empty;

    /// <summary>任务编号，对应标题与日志里的“准备 N”。</summary>
    public int Number { get; set; }

    /// <summary><c>codex</c> / <c>claude</c>。</summary>
    public string ClientType { get; set; } = string.Empty;

    /// <summary><see cref="CurrentSource"/> / <see cref="ListSource"/> / <see cref="CustomSource"/>。</summary>
    public string ProviderSource { get; set; } = string.Empty;

    /// <summary>手动配置时是填写的供应商地址；其余来源是上次开始读到的地址，只用于显示，开始时会重新解析。</summary>
    public string ProviderUrl { get; set; } = string.Empty;

    /// <summary>
    /// 手动配置的 API Key，按用户决定（2026-09-28）明文保存，重启后可直接开始；
    /// 其余来源的密钥不保存，每次开始时重新解析。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary><c>default</c> / <c>low</c> / … / <c>ultra</c>，与通道配置的写法一致。</summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>保活间隔分钟数，保存对话框里提交成功的文本，例如 <c>7.5</c>。</summary>
    public string IdleMinutes { get; set; } = string.Empty;

    /// <summary>管理任务上次开始时实际绑定的供应商 ID；跟随当前也保存，便于唯一性检查和联动删除。</summary>
    public string ProviderId { get; set; } = string.Empty;

    public string KeyId { get; set; } = string.Empty;

    public string ProviderName { get; set; } = string.Empty;

    public string KeyName { get; set; } = string.Empty;

    /// <summary>启动/停止意图；退出停止服务时保留，启动后由 ResumeRunning 恢复。</summary>
    public bool WasRunning { get; set; }

    /// <summary>本地日期 yyyy-MM-dd；每日轮次含成功、失败、中断，不含仍在执行的轮次。</summary>
    public string DailyDate { get; set; } = string.Empty;

    public ulong DailyRounds { get; set; }

    public ulong DailySuccesses { get; set; }
}
