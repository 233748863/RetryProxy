using System;
using System.Collections.Generic;
using System.Linq;

namespace RetryProxy.Core.Config;

internal static class UrlRules
{
    public static string NormalizeBaseUrl(string value)
    {
        return value.Trim().TrimEnd('/');
    }

    public static void ValidateBaseUrl(string value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != "http" && parsed.Scheme != "https")
            || string.IsNullOrEmpty(parsed.Host))
        {
            throw new ConfigException($"{label}必须是有效的 http 或 https URL");
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new ConfigException($"{label}不能包含用户名或密码");
        }

        // 与 Rust 的 Url::query()/fragment() 一致：只要出现 ? 或 # 即视为存在。
        if (value.Contains('?') || value.Contains('#'))
        {
            throw new ConfigException($"{label}不能包含查询参数或片段");
        }
    }

    public static void ValidateRetrySettings(
        int listenPort,
        double timeoutSeconds,
        double generationTimeoutSeconds,
        double totalTimeoutSeconds,
        double baseDelaySeconds,
        double maxDelaySeconds,
        string prefix)
    {
        if (listenPort < 1 || listenPort > 65535)
        {
            throw new ConfigException($"{prefix}本地端口必须在 1 到 65535 之间");
        }

        if (!(double.IsFinite(timeoutSeconds) && 0.0 < timeoutSeconds && timeoutSeconds <= 86400.0))
        {
            throw new ConfigException($"{prefix}单次超时必须大于 0 且不超过 86400 秒");
        }

        if (!(double.IsFinite(totalTimeoutSeconds) && 0.0 < totalTimeoutSeconds && totalTimeoutSeconds <= 86400.0))
        {
            throw new ConfigException($"{prefix}总等待上限必须大于 0 且不超过 86400 秒");
        }

        if (!(double.IsFinite(generationTimeoutSeconds) && 0.0 < generationTimeoutSeconds && generationTimeoutSeconds <= 86400.0))
        {
            throw new ConfigException($"{prefix}等待生成上限必须大于 0 且不超过 86400 秒");
        }

        if (!double.IsFinite(baseDelaySeconds) || baseDelaySeconds < 0.0 || baseDelaySeconds > 3600.0)
        {
            throw new ConfigException($"{prefix}初始重试间隔必须在 0 到 3600 秒之间");
        }

        if (!double.IsFinite(maxDelaySeconds) || maxDelaySeconds < baseDelaySeconds || maxDelaySeconds > 86400.0)
        {
            throw new ConfigException($"{prefix}最长重试间隔不能小于初始间隔，且不能超过 86400 秒");
        }
    }

    public static string GeneratedProviderName(string baseUrl, IEnumerable<ProviderEndpoint> providers)
    {
        var hostname = Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.Host)
            ? parsed.Host
            : "默认服务商";
        var names = new HashSet<string>(providers.Select(provider => provider.Name.ToLowerInvariant()));
        if (!names.Contains(hostname.ToLowerInvariant()))
        {
            return hostname;
        }

        var suffix = 2;
        while (true)
        {
            var candidate = $"{hostname} ({suffix})";
            if (!names.Contains(candidate.ToLowerInvariant()))
            {
                return candidate;
            }

            suffix++;
        }
    }
}
