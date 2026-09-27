using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using RetryProxy.Core.Service;

namespace RetryProxy.Core.Tls;

/// <summary>
/// SocketsHttpHandler 的 ConnectCallback：自行连通上游（直连或经系统代理的 CONNECT / SOCKS5 隧道），
/// 再用指纹 TLS 握手，外面再套一层 <see cref="HeaderOrderStream"/> 按客户端原顺序重排请求头。
/// 请求地址须改写成 http://主机:端口，HttpClient 才不会再套一层系统 TLS。
/// </summary>
internal sealed class TlsFingerprintConnector
{
    private readonly IProxyResolver _resolver;
    private readonly Func<ClientHelloFingerprint> _fingerprint;

    public TlsFingerprintConnector(IProxyResolver resolver, Func<ClientHelloFingerprint> fingerprint)
    {
        _resolver = resolver;
        _fingerprint = fingerprint;
    }

    /// <summary>仅供测试：额外信任的根证书。</summary>
    public X509Certificate2? TrustedRoot { get; set; }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var proxy = _resolver.Resolve(new UriBuilder(Uri.UriSchemeHttps, host, port).Uri);
        var transport = await UpstreamDialer.ConnectAsync(host, port, proxy?.ProxyUrl, cancellationToken).ConfigureAwait(false);
        try
        {
            var tls = await FingerprintTlsStream.ConnectAsync(transport, host, _fingerprint(), CreateAuthentication(host), cancellationToken)
                .ConfigureAwait(false);
            return new HeaderOrderStream(tls);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private TlsAuthentication CreateAuthentication(string host) => new ServerCertificateValidator(host, TrustedRoot);

    /// <summary>
    /// https 目标换成同主机同端口的 http 地址，交给 HttpClient 走明文（TLS 由本连接器完成）。
    /// 例：<c>https://api.example.com/v1/messages</c> → <c>http://api.example.com:443/v1/messages</c>。
    /// </summary>
    public static Uri PlainRequestUri(Uri target) => new UriBuilder(target) { Scheme = Uri.UriSchemeHttp, Port = target.Port }.Uri;

    /// <summary>
    /// 与直连 https 时一致的 Host 头：默认端口不写端口号。
    /// 例：<c>https://api.example.com/v1</c> → <c>api.example.com</c>；<c>https://relay.example.com:8443/v1</c> → <c>relay.example.com:8443</c>。
    /// </summary>
    public static string HostHeader(Uri target)
    {
        var host = target.HostNameType == UriHostNameType.IPv6 ? target.Host : target.IdnHost;
        return target.IsDefaultPort ? host : $"{host}:{target.Port}";
    }
}

/// <summary>
/// 建立到目标主机的 TCP 通道：直连，或经 HTTP 代理 CONNECT、SOCKS5 代理建立隧道。
/// 例：系统代理 <c>http://127.0.0.1:7897</c> → 先发 <c>CONNECT api.example.com:443 HTTP/1.1</c>，收到 200 后这条连接就直通上游。
/// </summary>
internal static class UpstreamDialer
{
    private const int MaxProxyResponseBytes = 16 * 1024;

    public static async Task<Stream> ConnectAsync(string host, int port, Uri? proxy, CancellationToken cancellationToken)
    {
        if (proxy is null)
        {
            return await DialAsync(host, port, cancellationToken).ConfigureAwait(false);
        }

        var scheme = proxy.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "socks5" or "socks5h"))
        {
            throw new FingerprintConnectException($"模拟 TLS 指纹时不支持 {proxy.Scheme.ToLowerInvariant()} 代理，请改用 HTTP 或 SOCKS5 代理");
        }

        var stream = await DialAsync(proxy.IdnHost, proxy.Port, cancellationToken).ConfigureAwait(false);
        try
        {
            var (user, password) = Credentials(proxy);
            if (scheme == "http")
            {
                await HttpConnectAsync(stream, host, port, user, password, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Socks5ConnectAsync(stream, host, port, user, password, cancellationToken).ConfigureAwait(false);
            }

            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Stream> DialAsync(string host, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static (string? User, string? Password) Credentials(Uri proxy)
    {
        if (proxy.UserInfo.Length == 0)
        {
            return (null, null);
        }

        var separator = proxy.UserInfo.IndexOf(':');
        return separator < 0
            ? (Uri.UnescapeDataString(proxy.UserInfo), string.Empty)
            : (Uri.UnescapeDataString(proxy.UserInfo[..separator]), Uri.UnescapeDataString(proxy.UserInfo[(separator + 1)..]));
    }

    private static async Task HttpConnectAsync(Stream stream, string host, int port, string? user, string? password, CancellationToken cancellationToken)
    {
        var authority = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
        var request = new StringBuilder($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n");
        if (user is not null)
        {
            request.Append("Proxy-Authorization: Basic ")
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")))
                .Append("\r\n");
        }

        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancellationToken).ConfigureAwait(false);

        // 逐字节读到空行为止，不多读隧道里的数据。
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (head.Length >= MaxProxyResponseBytes)
            {
                throw new FingerprintConnectException("代理 CONNECT 响应头过长");
            }

            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new FingerprintConnectException("代理在建立隧道时断开了连接");
            }

            head.Append((char)one[0]);
        }

        var statusLine = head.ToString()[..head.ToString().IndexOf("\r\n", StringComparison.Ordinal)];
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var status))
        {
            throw new FingerprintConnectException("代理建立隧道的回复格式不对");
        }

        if (status is < 200 or > 299)
        {
            throw new FingerprintConnectException(status == 407 ? "代理要求认证（HTTP 407）" : $"代理拒绝建立隧道（HTTP {status}）");
        }
    }

    private static async Task Socks5ConnectAsync(Stream stream, string host, int port, string? user, string? password, CancellationToken cancellationToken)
    {
        var methods = user is null ? new byte[] { 5, 1, 0 } : new byte[] { 5, 2, 0, 2 };
        await stream.WriteAsync(methods, cancellationToken).ConfigureAwait(false);
        var reply = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);
        if (reply[0] != 5)
        {
            throw new FingerprintConnectException("SOCKS5 代理响应无效");
        }

        if (reply[1] == 2 && user is not null)
        {
            var userBytes = Encoding.UTF8.GetBytes(user);
            var passwordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
            if (userBytes.Length > 255 || passwordBytes.Length > 255)
            {
                throw new FingerprintConnectException("SOCKS5 代理的用户名或密码过长");
            }

            var auth = new byte[3 + userBytes.Length + passwordBytes.Length];
            auth[0] = 1;
            auth[1] = (byte)userBytes.Length;
            userBytes.CopyTo(auth, 2);
            auth[2 + userBytes.Length] = (byte)passwordBytes.Length;
            passwordBytes.CopyTo(auth, 3 + userBytes.Length);
            await stream.WriteAsync(auth, cancellationToken).ConfigureAwait(false);
            var status = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);
            if (status[1] != 0)
            {
                throw new FingerprintConnectException("SOCKS5 代理认证失败");
            }
        }
        else if (reply[1] != 0)
        {
            throw new FingerprintConnectException(reply[1] == 0xff ? "SOCKS5 代理不接受可用的认证方式" : "SOCKS5 代理要求不支持的认证方式");
        }

        byte[] address;
        if (IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            address = new byte[1 + bytes.Length];
            address[0] = (byte)(ip.AddressFamily == AddressFamily.InterNetworkV6 ? 4 : 1);
            bytes.CopyTo(address, 1);
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length > 255)
            {
                throw new FingerprintConnectException("目标主机名过长");
            }

            address = new byte[2 + name.Length];
            address[0] = 3;
            address[1] = (byte)name.Length;
            name.CopyTo(address, 2);
        }

        var connect = new byte[3 + address.Length + 2];
        connect[0] = 5;
        connect[1] = 1;
        address.CopyTo(connect, 3);
        BinaryPrimitives.WriteUInt16BigEndian(connect.AsSpan(3 + address.Length), (ushort)port);
        await stream.WriteAsync(connect, cancellationToken).ConfigureAwait(false);

        var head = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        if (head[1] != 0)
        {
            throw new FingerprintConnectException($"SOCKS5 代理连不上上游（错误码 {head[1]}）");
        }

        var remaining = head[3] switch
        {
            1 => 4 + 2,
            4 => 16 + 2,
            3 => (await ReadExactAsync(stream, 1, cancellationToken).ConfigureAwait(false))[0] + 2,
            _ => throw new FingerprintConnectException("SOCKS5 代理响应无效"),
        };
        await ReadExactAsync(stream, remaining, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }
}

/// <summary>
/// 指纹 TLS 连接失败。<see cref="Plain"/> 是固定的大白话，不含地址、主机名与密钥，可直接写进日志。
/// </summary>
internal sealed class FingerprintConnectException : IOException
{
    public FingerprintConnectException(string plain, Exception? inner = null)
        : base(plain, inner)
    {
        Plain = plain;
    }

    public string Plain { get; }
}
