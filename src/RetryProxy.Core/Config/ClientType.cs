using System;

namespace RetryProxy.Core.Config;

/// <summary>
/// 通道面向的客户端类型。序列化为 <c>codex</c> / <c>claude</c>。
/// </summary>
public enum ClientType
{
    Codex,
    Claude,
}

public static class ClientTypeExtensions
{
    public static string Label(this ClientType type) => type switch
    {
        ClientType.Claude => "Claude Code",
        _ => "Codex",
    };

    public static string AsStr(this ClientType type) => type switch
    {
        ClientType.Claude => "claude",
        _ => "codex",
    };

    /// <summary>
    /// 只用于迁移旧配置；保存后客户端类型不再随名称或端口变化。
    /// </summary>
    internal static ClientType ForLegacyRoute(string name, int port)
    {
        return name.Contains("claude", StringComparison.OrdinalIgnoreCase) || port == 18081
            ? ClientType.Claude
            : ClientType.Codex;
    }
}
