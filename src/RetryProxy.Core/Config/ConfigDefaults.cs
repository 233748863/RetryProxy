namespace RetryProxy.Core.Config;

/// <summary>
/// 配置默认值与常量，对应 Rust 版 config.rs / keepalive.rs 的常量。
/// </summary>
public static class ConfigDefaults
{
    public const int ListenPort = 18080;
    public const long MaxRetries = 6;
    public const double TimeoutSeconds = 300.0;
    public const double GenerationTimeoutSeconds = 300.0;
    public const double TotalTimeoutSeconds = 600.0;
    public const double BaseDelaySeconds = 0.5;
    public const double MaxDelaySeconds = 4.0;
    public const int CurrentSchemaVersion = 6;

    /// <summary>
    /// 空闲多久算需要保活。代理只要还在正常回 200 就什么都不做，超过这个时长
    /// 没有新请求才补发一条。
    /// </summary>
    public const double KeepaliveIdleMinutes = 3.0;

    public const double MinKeepaliveIdleMinutes = 0.5;
    public const double MaxKeepaliveIdleMinutes = 1440.0;
    public const long KeepaliveContextLimit = 50_000;

    public const string ListenHost = "127.0.0.1";
}
