using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace RetryProxy.Core.Config;

/// <summary>
/// 环境变量对当前选中通道的临时覆盖（8 项），不写回配置。
/// </summary>
public static class EnvironmentOverrides
{
    public const string UpstreamBaseUrl = "UPSTREAM_BASE_URL";
    public const string ListenPort = "RETRY_PROXY_PORT";
    public const string MaxRetries = "RETRY_MAX_RETRIES";
    public const string TimeoutSeconds = "RETRY_TIMEOUT_SECONDS";
    public const string GenerationTimeoutSeconds = "RETRY_GENERATION_TIMEOUT_SECONDS";
    public const string TotalTimeoutSeconds = "RETRY_TOTAL_TIMEOUT_SECONDS";
    public const string BaseDelaySeconds = "RETRY_BASE_DELAY_SECONDS";
    public const string MaxDelaySeconds = "RETRY_MAX_DELAY_SECONDS";

    /// <summary>
    /// 当前进程的环境变量快照（名称区分大小写，与 Rust std::env::vars 一致）。
    /// </summary>
    public static Dictionary<string, string> CurrentEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// 读取覆盖项；没有任何覆盖时返回 null。值无效时抛出 <see cref="ConfigException"/>。
    /// </summary>
    public static RouteRuntimeOverrides? Resolve(ProxyConfig config, IReadOnlyDictionary<string, string> environ)
    {
        var overrides = new RouteRuntimeOverrides
        {
            RouteId = config.Routes.Count == 0 ? string.Empty : config.SelectedRouteId,
        };
        var found = false;

        if (Value(environ, UpstreamBaseUrl) is { } upstream)
        {
            overrides.UpstreamBaseUrl = upstream;
            found = true;
        }

        if (Value(environ, ListenPort) is { } port)
        {
            if (!uint.TryParse(port, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                || port.StartsWith('-'))
            {
                throw Invalid(ListenPort);
            }

            overrides.ListenPort = parsed > int.MaxValue ? int.MaxValue : (int)parsed;
            found = true;
        }

        if (Value(environ, MaxRetries) is { } retries)
        {
            if (!ulong.TryParse(retries, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                || retries.StartsWith('-'))
            {
                throw Invalid(MaxRetries);
            }

            overrides.MaxRetries = parsed > long.MaxValue ? long.MaxValue : (long)parsed;
            found = true;
        }

        overrides.TimeoutSeconds = Double(environ, TimeoutSeconds, ref found);
        overrides.GenerationTimeoutSeconds = Double(environ, GenerationTimeoutSeconds, ref found);
        overrides.TotalTimeoutSeconds = Double(environ, TotalTimeoutSeconds, ref found);
        overrides.BaseDelaySeconds = Double(environ, BaseDelaySeconds, ref found);
        overrides.MaxDelaySeconds = Double(environ, MaxDelaySeconds, ref found);
        return found ? overrides : null;
    }

    private static string? Value(IReadOnlyDictionary<string, string> environ, string name)
    {
        if (!environ.TryGetValue(name, out var raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static double? Double(IReadOnlyDictionary<string, string> environ, string name, ref bool found)
    {
        var raw = Value(environ, name);
        if (raw is null)
        {
            return null;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            throw Invalid(name);
        }

        found = true;
        return parsed;
    }

    private static ConfigException Invalid(string name) => new($"环境变量 {name} 的值无效");
}
