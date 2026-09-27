using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace RetryProxy.Core.Tls;

/// <summary>按 <see cref="ClientHelloFingerprint"/> 配置握手参数的 BouncyCastle 客户端。</summary>
internal sealed class FingerprintTlsClient : DefaultTlsClient
{
    private readonly ClientHelloFingerprint _fingerprint;
    private readonly string _host;
    private readonly TlsAuthentication _authentication;

    public FingerprintTlsClient(ClientHelloFingerprint fingerprint, string host, TlsAuthentication authentication)
        : base(new BcTlsCrypto(new SecureRandom()))
    {
        _fingerprint = fingerprint;
        _host = host;
        _authentication = authentication;
    }

    protected override int[] GetSupportedCipherSuites() => TlsUtilities.GetSupportedCipherSuites(Crypto, _fingerprint.CipherSuites);

    protected override ProtocolVersion[] GetSupportedVersions() =>
        _fingerprint.Versions.Select(version => ProtocolVersion.Get(version >> 8, version & 0xff)).ToArray();

    protected override IList<ProtocolName>? GetProtocolNames() => _fingerprint.Protocols.Length == 0
        ? null
        : _fingerprint.Protocols.Select(ProtocolName.AsUtf8Encoding).ToList();

    /// <summary>IP 地址按规范不带 SNI（与真实客户端一致）。</summary>
    protected override IList<ServerName>? GetSniServerNames() =>
        !_fingerprint.Has(ExtensionTypes.ServerName) || IPAddress.TryParse(_host, out _)
            ? null
            : new List<ServerName> { new(NameType.host_name, Encoding.ASCII.GetBytes(_host)) };

    protected override IList<int> GetSupportedGroups(IList<int> namedGroupRoles) => _fingerprint.SupportedGroups;

    protected override IList<SignatureAndHashAlgorithm> GetSupportedSignatureAlgorithms() => _fingerprint.SignatureAlgorithms
        .Select(value => new SignatureAndHashAlgorithm((short)(value >> 8), (short)(value & 0xff)))
        .ToList();

    protected override CertificateStatusRequest? GetCertificateStatusRequest() =>
        _fingerprint.Has(ExtensionTypes.StatusRequest) ? base.GetCertificateStatusRequest() : null;

    public override IList<int>? GetEarlyKeyShareGroups() => _fingerprint.KeyShareGroups.Length == 0 ? null : _fingerprint.KeyShareGroups;

    public override bool ShouldUseCompatibilityMode() => _fingerprint.SessionIdLength > 0;

    public override bool ShouldUseExtendedMasterSecret() => _fingerprint.Has(ExtensionTypes.ExtendedMasterSecret);

    /// <summary>不少 HTTP 服务器直接断开而不发 close_notify；报文边界由 HTTP 自身界定。</summary>
    public override bool RequiresCloseNotify() => false;

    public override IDictionary<int, byte[]> GetClientExtensions()
    {
        var extensions = base.GetClientExtensions();
        extensions.Remove(ExtensionType.encrypt_then_mac);
        if (!_fingerprint.Has(ExtensionTypes.EcPointFormats))
        {
            extensions.Remove(ExtensionTypes.EcPointFormats);
        }

        // 以下扩展内容固定，照抄指纹；服务器的回应由 BouncyCastle 按标准处理。
        foreach (var type in new[]
                 {
                     ExtensionTypes.RenegotiationInfo, ExtensionTypes.SessionTicket,
                     ExtensionTypes.SignedCertificateTimestamp, ExtensionTypes.PskKeyExchangeModes,
                 })
        {
            if (_fingerprint.ExtensionData(type) is { } data)
            {
                extensions[type] = data;
            }
        }

        return extensions;
    }

    public override TlsAuthentication GetAuthentication() => _authentication;
}

/// <summary>
/// 按指纹的扩展顺序自行编码 ClientHello。BouncyCastle 默认把空扩展排在最前，无法复现真实客户端的顺序。
/// 例：Claude Code 的顺序是 server_name, extended_master_secret(空), renegotiation_info, supported_groups, …；
/// BouncyCastle 原样会写成 extended_master_secret(空), session_ticket(空), sct(空), server_name, …，JA3 随之不同。
/// 需要 PSK binder 的握手（会话恢复）交回原实现；本项目每条连接都完整握手，正常走不到。
/// </summary>
internal sealed class OrderedClientProtocol : TlsClientProtocol
{
    private readonly int[] _order;

    public OrderedClientProtocol(ClientHelloFingerprint fingerprint)
    {
        _order = fingerprint.ExtensionOrder;
    }

    // BouncyCastle 2.7.0 的 internal 成员；版本变化导致找不到时首次握手即抛 MissingMemberException（有单测守护）。
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_handshakeHash")]
    private static extern ref TlsHandshakeHash HandshakeHash(TlsProtocol protocol);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WriteHandshakeMessage")]
    private static extern void WriteHandshakeMessage(TlsProtocol protocol, byte[] buffer, int offset, int length);

    protected override void SendClientHelloMessage()
    {
        var hello = m_clientHello;
        var extensions = hello.Extensions;
        if (extensions.ContainsKey(ExtensionTypes.PreSharedKey) || hello.Cookie is not null)
        {
            base.SendClientHelloMessage();
            return;
        }

        var body = new MemoryStream();
        TlsUtilities.WriteVersion(hello.Version, body);
        body.Write(hello.Random);
        TlsUtilities.WriteOpaque8(hello.SessionID, body);
        TlsUtilities.WriteUint16ArrayWithUint16Length(hello.CipherSuites, body);
        TlsUtilities.WriteUint8ArrayWithUint8Length(new[] { CompressionMethod.cls_null }, body);

        var encoded = new MemoryStream();
        foreach (var type in _order.Where(extensions.ContainsKey).Concat(extensions.Keys.Where(type => Array.IndexOf(_order, type) < 0)))
        {
            TlsUtilities.WriteUint16(type, encoded);
            TlsUtilities.WriteOpaque16(extensions[type], encoded);
        }

        TlsUtilities.WriteOpaque16(encoded.ToArray(), body);

        var message = new byte[4 + body.Length];
        message[0] = (byte)HandshakeType.client_hello;
        TlsUtilities.WriteUint24((int)body.Length, message, 1);
        body.ToArray().CopyTo(message, 4);

        HandshakeHash(this).Update(message, 0, message.Length);
        WriteHandshakeMessage(this, message, 0, message.Length);
    }
}

/// <summary>用 Windows 证书库校验服务器证书链与主机名（BouncyCastle 本身不做校验）。</summary>
internal sealed class ServerCertificateValidator : TlsAuthentication
{
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

    private readonly string _host;
    private readonly X509Certificate2? _trustedRoot;

    public ServerCertificateValidator(string host, X509Certificate2? trustedRoot = null)
    {
        _host = host;
        _trustedRoot = trustedRoot;
    }

    public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
    {
        var list = serverCertificate.Certificate.GetCertificateList();
        if (list.Length == 0)
        {
            throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }

        var intermediates = new List<X509Certificate2>();
        try
        {
            using var leaf = X509CertificateLoader.LoadCertificate(list[0].GetEncoded());
            using var chain = new X509Chain();
            // 与系统 TLS 路径一致：HttpClient 默认 CertificateRevocationCheckMode 也是 NoCheck。
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(ServerAuthentication);
            if (_trustedRoot is not null)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(_trustedRoot);
            }

            foreach (var certificate in list.Skip(1))
            {
                var intermediate = X509CertificateLoader.LoadCertificate(certificate.GetEncoded());
                intermediates.Add(intermediate);
                chain.ChainPolicy.ExtraStore.Add(intermediate);
            }

            if (!chain.Build(leaf))
            {
                var reason = string.Join("；", chain.ChainStatus.Select(status => status.StatusInformation.Trim()).Where(text => text.Length > 0));
                throw new ServerCertificateException("上游证书不受信任", $"上游证书不受信任：{(reason.Length > 0 ? reason : "证书链无效")}");
            }

            if (!leaf.MatchesHostname(_host))
            {
                throw new ServerCertificateException("上游证书与网址不符", $"上游证书与主机名 {_host} 不匹配");
            }
        }
        catch (CryptographicException error)
        {
            throw new ServerCertificateException("上游证书无法解析", $"无法解析上游证书：{error.Message}");
        }
        finally
        {
            foreach (var intermediate in intermediates)
            {
                intermediate.Dispose();
            }
        }
    }

    public TlsCredentials? GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest) => null;
}

/// <summary>证书校验失败；BouncyCastle 会先向服务器发 bad_certificate 告警再抛出。</summary>
internal sealed class ServerCertificateException : TlsFatalAlert
{
    public ServerCertificateException(string plain, string message)
        : base(Org.BouncyCastle.Tls.AlertDescription.bad_certificate, message)
    {
        Plain = plain;
    }

    /// <summary>不含主机名的简短说明，用于日志。</summary>
    public string Plain { get; }
}
