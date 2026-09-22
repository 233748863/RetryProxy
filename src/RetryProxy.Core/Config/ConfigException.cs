using System;

namespace RetryProxy.Core.Config;

/// <summary>
/// 配置解析或校验失败。消息文案面向用户，逐字沿用 Rust 版。
/// </summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message)
    {
    }
}
