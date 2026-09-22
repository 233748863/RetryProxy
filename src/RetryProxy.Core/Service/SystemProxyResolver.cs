using System;
using System.Net;
using Microsoft.Win32;

namespace RetryProxy.Core.Service;

public sealed class ProxyDecision
{
    public ProxyDecision(Uri proxyUrl, bool fromSystem)
    {
        ProxyUrl = proxyUrl;
        FromSystem = fromSystem;
    }

    public Uri ProxyUrl { get; }

    public bool FromSystem { get; }
}

public interface IProxyResolver
{
    ProxyDecision? Resolve(Uri target);
}

/// <summary>
/// 跟随系统代理（对应 system_proxy.rs）：环境变量优先，其次 Windows 注册表的 Internet Settings；
/// NO_PROXY / ProxyOverride 匹配或回环地址则直连。
/// </summary>
public sealed class SystemProxyResolver : IProxyResolver
{
    public static SystemProxyResolver Shared { get; } = new();

    public ProxyDecision? Resolve(Uri target)
    {
        if (BypassFromEnvironment(target))
        {
            return null;
        }

        var scheme = target.Scheme;
        var proxy = EnvironmentProxy(scheme) ?? WindowsRegistryProxy(scheme, target);
        if (proxy is null || !Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUrl))
        {
            return null;
        }

        return new ProxyDecision(proxyUrl, true);
    }

    private static string? EnvironmentProxy(string scheme)
    {
        foreach (var name in new[] { $"{scheme.ToLowerInvariant()}_proxy", $"{scheme.ToUpperInvariant()}_PROXY", "all_proxy", "ALL_PROXY" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return NormalizeProxyUrl(value);
            }
        }

        return null;
    }

    private static bool BypassFromEnvironment(Uri target)
    {
        var raw = Environment.GetEnvironmentVariable("NO_PROXY") ?? Environment.GetEnvironmentVariable("no_proxy") ?? string.Empty;
        return BypassPatternList(target, raw);
    }

    internal static bool BypassPatternList(Uri target, string raw)
    {
        var host = target.Host;
        if (string.IsNullOrEmpty(host))
        {
            return true;
        }

        host = host.ToLowerInvariant();
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        // 与 Windows/Python 的系统代理绕过行为保持一致：本机回环地址始终直连。
        if (host == "localhost" || host == "::1" || host.StartsWith("127.", StringComparison.Ordinal) || host == "0.0.0.0")
        {
            return true;
        }

        var port = target.Port;
        foreach (var item in raw.Split(',', ';'))
        {
            var pattern = item.Trim();
            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern == "*" || (pattern == "<local>" && !host.Contains('.')))
            {
                return true;
            }

            if (pattern.StartsWith("http://", StringComparison.Ordinal))
            {
                pattern = pattern["http://".Length..];
            }

            if (pattern.StartsWith("https://", StringComparison.Ordinal))
            {
                pattern = pattern["https://".Length..];
            }

            pattern = pattern.Split('/')[0];
            var patternHost = pattern;
            int? patternPort = null;
            var colon = pattern.LastIndexOf(':');
            if (colon >= 0)
            {
                patternHost = pattern[..colon];
                patternPort = int.TryParse(pattern[(colon + 1)..], out var parsed) ? parsed : null;
            }

            if (patternPort is not null && patternPort != port)
            {
                continue;
            }

            if (patternHost.StartsWith("*.", StringComparison.Ordinal))
            {
                patternHost = patternHost[2..];
            }

            patternHost = patternHost.ToLowerInvariant();
            if (host == patternHost || host.EndsWith("." + patternHost, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static string NormalizeProxyUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : $"http://{trimmed}";
    }

    private static string? WindowsRegistryProxy(string scheme, Uri target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings");
            if (key is null)
            {
                return null;
            }

            if (key.GetValue("ProxyEnable") is not int enabled || enabled == 0)
            {
                return null;
            }

            var overrides = key.GetValue("ProxyOverride") as string ?? string.Empty;
            if (BypassPatternList(target, overrides))
            {
                return null;
            }

            if (key.GetValue("ProxyServer") is not string server)
            {
                return null;
            }

            return SelectProxyServer(server, scheme);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? SelectProxyServer(string server, string scheme)
    {
        string? selected = null;
        foreach (var entry in server.Split(';'))
        {
            var separator = entry.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            if (string.Equals(entry[..separator], scheme, StringComparison.OrdinalIgnoreCase))
            {
                selected = entry[(separator + 1)..];
                break;
            }
        }

        if (selected is null && !server.Contains('='))
        {
            selected = server;
        }

        return selected is null ? null : NormalizeProxyUrl(selected);
    }
}

/// <summary>把 <see cref="IProxyResolver"/> 接到 HttpClient 的 IWebProxy 上。</summary>
internal sealed class ResolverWebProxy : IWebProxy
{
    private readonly IProxyResolver _resolver;

    public ResolverWebProxy(IProxyResolver resolver)
    {
        _resolver = resolver;
    }

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) => _resolver.Resolve(destination)?.ProxyUrl;

    public bool IsBypassed(Uri host) => _resolver.Resolve(host) is null;
}
