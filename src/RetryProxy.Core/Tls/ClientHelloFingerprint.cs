using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RetryProxy.Core.Tls;

/// <summary>
/// 从抓到的 ClientHello 记录（含 5 字节记录头）解析出的 TLS 指纹：密码套件、扩展顺序、密钥组、签名算法等。
/// 出站握手按它配置 BouncyCastle 并按原顺序写扩展；只接受本实现能够逐字节复现的指纹。
/// </summary>
public sealed class ClientHelloFingerprint
{
    /// <summary>能按指纹参数复现、且 BouncyCastle 能正确处理服务器回应的扩展。</summary>
    private static readonly HashSet<int> SupportedExtensions = new()
    {
        ExtensionTypes.ServerName, ExtensionTypes.StatusRequest, ExtensionTypes.SupportedGroups, ExtensionTypes.EcPointFormats,
        ExtensionTypes.SignatureAlgorithms, ExtensionTypes.Alpn, ExtensionTypes.SignedCertificateTimestamp,
        ExtensionTypes.ExtendedMasterSecret, ExtensionTypes.SessionTicket, ExtensionTypes.SupportedVersions,
        ExtensionTypes.PskKeyExchangeModes, ExtensionTypes.KeyShare, ExtensionTypes.RenegotiationInfo,
    };

    private ClientHelloFingerprint(byte[] record)
    {
        Record = record;
    }

    /// <summary>原始记录字节（随机数、会话 ID 与公钥可能已清零）。</summary>
    public byte[] Record { get; }

    public int LegacyVersion { get; private set; }

    public int SessionIdLength { get; private set; }

    public int[] CipherSuites { get; private set; } = Array.Empty<int>();

    /// <summary>扩展类型，按出现顺序。</summary>
    public int[] ExtensionOrder { get; private set; } = Array.Empty<int>();

    public int[] SupportedGroups { get; private set; } = Array.Empty<int>();

    public int[] KeyShareGroups { get; private set; } = Array.Empty<int>();

    public int[] SignatureAlgorithms { get; private set; } = Array.Empty<int>();

    public string[] Protocols { get; private set; } = Array.Empty<string>();

    /// <summary>supported_versions 列出的版本；没有该扩展时只有 legacy_version。</summary>
    public int[] Versions { get; private set; } = Array.Empty<int>();

    public string? ServerName { get; private set; }

    public string Ja4 { get; private set; } = string.Empty;

    public bool Has(int extensionType) => Array.IndexOf(ExtensionOrder, extensionType) >= 0;

    public byte[]? ExtensionData(int extensionType) => _extensions.TryGetValue(extensionType, out var data) ? data : null;

    private readonly Dictionary<int, byte[]> _extensions = new();

    /// <summary>解析 ClientHello 记录；格式不对时抛出 <see cref="FormatException"/>。</summary>
    public static ClientHelloFingerprint Parse(ReadOnlySpan<byte> record)
    {
        try
        {
            return ParseCore(record);
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new FormatException("ClientHello 数据被截断");
        }
    }

    private static ClientHelloFingerprint ParseCore(ReadOnlySpan<byte> record)
    {
        if (record.Length < 9 || record[0] != 0x16 || record[5] != 0x01)
        {
            throw new FormatException("不是 TLS ClientHello 记录");
        }

        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(record[3..]);
        var bodyLength = (record[6] << 16) | (record[7] << 8) | record[8];
        if (record.Length != 5 + recordLength || recordLength != 4 + bodyLength)
        {
            throw new FormatException("ClientHello 长度与记录不符（可能跨了多个记录）");
        }

        var fingerprint = new ClientHelloFingerprint(record.ToArray());
        var body = record[9..];
        var p = 0;
        fingerprint.LegacyVersion = BinaryPrimitives.ReadUInt16BigEndian(body[p..]);
        p += 2 + 32;
        fingerprint.SessionIdLength = body[p];
        p += 1 + body[p];
        var cipherLength = BinaryPrimitives.ReadUInt16BigEndian(body[p..]);
        fingerprint.CipherSuites = ReadUInt16List(body.Slice(p + 2, cipherLength));
        p += 2 + cipherLength;
        var compression = body.Slice(p + 1, body[p]);
        if (compression.Length != 1 || compression[0] != 0)
        {
            throw new FormatException("ClientHello 声明了压缩方法");
        }

        p += 1 + body[p];
        var extensionsEnd = p + 2 + BinaryPrimitives.ReadUInt16BigEndian(body[p..]);
        if (extensionsEnd != body.Length)
        {
            throw new FormatException("ClientHello 扩展长度不符");
        }

        p += 2;
        var order = new List<int>();
        while (p < extensionsEnd)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(body[p..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(body[(p + 2)..]);
            var data = body.Slice(p + 4, length);
            if (fingerprint._extensions.ContainsKey(type))
            {
                throw new FormatException($"ClientHello 扩展 {type} 重复");
            }

            fingerprint._extensions[type] = data.ToArray();
            order.Add(type);
            p += 4 + length;
        }

        if (p != extensionsEnd)
        {
            throw new FormatException("ClientHello 扩展长度不符");
        }

        fingerprint.ExtensionOrder = order.ToArray();
        fingerprint.ParseExtensions();
        fingerprint.Ja4 = ComputeJa4(fingerprint);
        return fingerprint;
    }

    private void ParseExtensions()
    {
        if (ExtensionData(ExtensionTypes.SupportedGroups) is { } groups)
        {
            SupportedGroups = ReadUInt16List(groups.AsSpan(2, BinaryPrimitives.ReadUInt16BigEndian(groups)));
        }

        if (ExtensionData(ExtensionTypes.SignatureAlgorithms) is { } signatures)
        {
            SignatureAlgorithms = ReadUInt16List(signatures.AsSpan(2, BinaryPrimitives.ReadUInt16BigEndian(signatures)));
        }

        if (ExtensionData(ExtensionTypes.Alpn) is { } alpn)
        {
            var protocols = new List<string>();
            for (var q = 2; q < alpn.Length; q += 1 + alpn[q])
            {
                protocols.Add(Encoding.ASCII.GetString(alpn, q + 1, alpn[q]));
            }

            Protocols = protocols.ToArray();
        }

        Versions = ExtensionData(ExtensionTypes.SupportedVersions) is { } versions
            ? ReadUInt16List(versions.AsSpan(1, versions[0]))
            : new[] { LegacyVersion };

        if (ExtensionData(ExtensionTypes.KeyShare) is { } keyShare)
        {
            var shares = new List<int>();
            for (var q = 2; q + 4 <= keyShare.Length; q += 4 + BinaryPrimitives.ReadUInt16BigEndian(keyShare.AsSpan(q + 2)))
            {
                shares.Add(BinaryPrimitives.ReadUInt16BigEndian(keyShare.AsSpan(q)));
            }

            KeyShareGroups = shares.ToArray();
        }

        if (ExtensionData(ExtensionTypes.ServerName) is { Length: > 5 } serverName)
        {
            ServerName = Encoding.ASCII.GetString(serverName, 5, serverName.Length - 5);
        }
    }

    /// <summary>本实现无法复现或无法安全处理时返回原因；可用时返回 null。</summary>
    public string? UnsupportedReason()
    {
        var all = CipherSuites.Concat(ExtensionOrder).Concat(SupportedGroups).Concat(Versions).Concat(KeyShareGroups);
        if (all.Any(IsGrease))
        {
            return "包含 GREASE 随机值";
        }

        foreach (var type in ExtensionOrder)
        {
            if (!SupportedExtensions.Contains(type))
            {
                return $"包含暂不支持的扩展 {type}";
            }
        }

        if (!Has(ExtensionTypes.SupportedGroups) || !Has(ExtensionTypes.SignatureAlgorithms))
        {
            return "缺少 supported_groups 或 signature_algorithms";
        }

        if (Versions.Any(version => version is not (0x0303 or 0x0304)))
        {
            return "只支持 TLS 1.2 与 TLS 1.3";
        }

        if (SessionIdLength is not (0 or 32))
        {
            return $"会话 ID 长度 {SessionIdLength} 不受支持";
        }

        return null;
    }

    /// <summary>
    /// 清零随机数、会话 ID 与 key_share 公钥后的字节；两份指纹结构相同当且仅当它们相等。
    /// 例：同一个 Claude Code 连两次，random（32 字节）和 X25519MLKEM768 公钥（1216 字节）每次都不同，清零后逐字节相同。
    /// </summary>
    public byte[] Masked()
    {
        var masked = (byte[])Record.Clone();
        var p = 9 + 2;
        Array.Clear(masked, p, 32);
        p += 32;
        Array.Clear(masked, p + 1, masked[p]);
        p += 1 + masked[p];
        p += 2 + BinaryPrimitives.ReadUInt16BigEndian(masked.AsSpan(p));
        p += 1 + masked[p];
        var end = p + 2 + BinaryPrimitives.ReadUInt16BigEndian(masked.AsSpan(p));
        p += 2;
        while (p < end)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(masked.AsSpan(p));
            var length = BinaryPrimitives.ReadUInt16BigEndian(masked.AsSpan(p + 2));
            if (type == ExtensionTypes.KeyShare)
            {
                for (var q = p + 4 + 2; q + 4 <= p + 4 + length;)
                {
                    var keyLength = BinaryPrimitives.ReadUInt16BigEndian(masked.AsSpan(q + 2));
                    Array.Clear(masked, q + 4, keyLength);
                    q += 4 + keyLength;
                }
            }

            p += 4 + length;
        }

        return masked;
    }

    public bool SameStructure(ClientHelloFingerprint other) => Masked().AsSpan().SequenceEqual(other.Masked());

    internal static bool IsGrease(int value) => (value & 0x0f0f) == 0x0a0a && (value >> 8) == (value & 0xff);

    private static int[] ReadUInt16List(ReadOnlySpan<byte> data)
    {
        var values = new int[data.Length / 2];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]);
        }

        return values;
    }

    /// <summary>
    /// JA4（FoxIO 规范）：版本、SNI、套件数、扩展数、ALPN 首尾字符，以及排序后的套件与扩展/签名算法哈希。
    /// 例：Claude Code 为 <c>t13d1713h1_5b57614c22b0_6a3d802a7139</c> = TLS 1.3、带域名 SNI、17 个套件、13 个扩展、ALPN http/1.1。
    /// </summary>
    private static string ComputeJa4(ClientHelloFingerprint fingerprint)
    {
        var versions = fingerprint.Versions.Where(version => !IsGrease(version)).DefaultIfEmpty(fingerprint.LegacyVersion).Max();
        var version = versions switch
        {
            0x0304 => "13",
            0x0303 => "12",
            0x0302 => "11",
            0x0301 => "10",
            _ => "00",
        };
        var ciphers = fingerprint.CipherSuites.Where(cipher => !IsGrease(cipher)).ToArray();
        var extensions = fingerprint.ExtensionOrder.Where(type => !IsGrease(type)).ToArray();
        var alpn = fingerprint.Protocols.FirstOrDefault();
        var alpnMark = string.IsNullOrEmpty(alpn) ? "00" : $"{alpn[0]}{alpn[^1]}";
        var a = $"t{version}{(fingerprint.ServerName is null ? 'i' : 'd')}{Math.Min(ciphers.Length, 99):D2}{Math.Min(extensions.Length, 99):D2}{alpnMark}";
        var b = Hash12(string.Join(",", ciphers.Order().Select(cipher => cipher.ToString("x4"))));
        var extensionText = string.Join(",", extensions
            .Where(type => type != ExtensionTypes.ServerName && type != ExtensionTypes.Alpn)
            .Order()
            .Select(type => type.ToString("x4")));
        var signatureText = string.Join(",", fingerprint.SignatureAlgorithms.Select(algorithm => algorithm.ToString("x4")));
        var c = Hash12(signatureText.Length > 0 ? $"{extensionText}_{signatureText}" : extensionText);
        return $"{a}_{b}_{c}";
    }

    private static string Hash12(string text) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(text)))[..12].ToLowerInvariant();
}

/// <summary>用到的 TLS 扩展类型编号（与 BouncyCastle 的 ExtensionType 一致）。</summary>
internal static class ExtensionTypes
{
    public const int ServerName = 0;
    public const int StatusRequest = 5;
    public const int SupportedGroups = 10;
    public const int EcPointFormats = 11;
    public const int SignatureAlgorithms = 13;
    public const int Alpn = 16;
    public const int SignedCertificateTimestamp = 18;
    public const int ExtendedMasterSecret = 23;
    public const int SessionTicket = 35;
    public const int PreSharedKey = 41;
    public const int SupportedVersions = 43;
    public const int PskKeyExchangeModes = 45;
    public const int KeyShare = 51;
    public const int RenegotiationInfo = 65281;
}
