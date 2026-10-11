using System;
using System.Net;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Service;

/// <summary>固定供应商/Key 的选择；只有继承模式读取系统设置，指定代理不应用系统绕过列表。</summary>
internal sealed class NetworkProxyResolver : IProxyResolver
{
    private readonly NetworkProxyMode _mode;
    private readonly Uri? _proxy;
    private readonly IProxyResolver _fallback;

    public NetworkProxyResolver(NetworkProxySettings settings, IProxyResolver fallback)
    {
        var normalized = settings.Normalize();
        normalized.Validate();
        _mode = normalized.Mode;
        _proxy = _mode == NetworkProxyMode.Custom ? normalized.ProxyUri() : null;
        _fallback = fallback;
    }

    public ProxyDecision? Resolve(Uri target) => _mode == NetworkProxyMode.Inherit ? _fallback.Resolve(target)
        : _proxy is null ? null : new ProxyDecision(_proxy, false);

    /// <summary>.NET 的 SOCKS5 使用远端域名解析；socks5h 按相同行为传给处理器。凭据单独交给代理认证。</summary>
    internal static WebProxy WebProxyFor(Uri proxy)
    {
        var builder = new UriBuilder(proxy) { UserName = string.Empty, Password = string.Empty };
        if (builder.Scheme == "socks5h") builder.Scheme = "socks5";
        var result = new WebProxy(builder.Uri);
        if (proxy.UserInfo.Length > 0)
        {
            var colon = proxy.UserInfo.IndexOf(':');
            var user = colon < 0 ? proxy.UserInfo : proxy.UserInfo[..colon];
            var password = colon < 0 ? string.Empty : proxy.UserInfo[(colon + 1)..];
            result.Credentials = new NetworkCredential(Uri.UnescapeDataString(user), Uri.UnescapeDataString(password));
        }
        return result;
    }
}

/// <summary>一个连接池只使用一个已经确定的出口，避免建连接时再次读取变化中的系统设置。</summary>
internal sealed class FixedNetworkProxyResolver(Uri? proxy) : IProxyResolver
{
    public ProxyDecision? Resolve(Uri target) => proxy is null ? null : new ProxyDecision(proxy, false);
}
