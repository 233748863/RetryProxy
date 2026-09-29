using System;
using System.IO;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Client;

/// <summary>只解析路径，不读取真实客户端文件；测试通过内部重载提供独立的环境变量字典。</summary>
public static class ClientConfigPaths
{
    public static string Resolve(ClientType client) => Resolve(client,
        Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>变量存在即禁止写入；空字符串也算注入配置。</summary>
    public static bool WritesBlocked => HasInjectedConfig(Environment.GetEnvironmentVariable);

    internal static bool HasInjectedConfig(Func<string, string?> environment) =>
        environment("RETRY_PROXY_CONFIG_JSON") is not null;

    internal static string Resolve(ClientType client, Func<string, string?> environment, string userHome)
    {
        try
        {
            if (!Enum.IsDefined(client))
                throw new InvalidOperationException();

            var directory = environment(client == ClientType.Claude ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME");
            if (string.IsNullOrWhiteSpace(directory))
            {
                if (string.IsNullOrWhiteSpace(userHome))
                    throw new InvalidOperationException();
                directory = Path.Combine(userHome, client == ClientType.Claude ? ".claude" : ".codex");
            }

            return Path.GetFullPath(Path.Combine(directory,
                client == ClientType.Claude ? "settings.json" : "config.toml"));
        }
        catch
        {
            throw new ClientConfigException("无法确定客户端配置位置，请检查配置目录设置。");
        }
    }
}
