using System;
using Microsoft.Win32;

namespace RetryProxy.Core.Config;

/// <summary>
/// 读取 Rust 版遗留在注册表中的配置（仅读取，C# 版不再回写注册表）。
/// </summary>
public static class RegistryConfigStore
{
    public const string SubKey = @"Software\LLM Retry Proxy";
    public const string ValueName = "ConfigJson";

    /// <summary>
    /// 返回注册表中的配置 JSON 文本；键或值不存在时返回 null。
    /// </summary>
    public static string? ReadLegacyJson()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SubKey);
            if (key is null)
            {
                return null;
            }

            var value = key.GetValue(ValueName);
            return value switch
            {
                null => null,
                string text => text,
                _ => throw new ConfigException("无法读取 Windows 配置：值不是字符串"),
            };
        }
        catch (ConfigException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ConfigException($"无法读取 Windows 配置：{error.Message}");
        }
    }
}
