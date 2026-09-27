using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Logging;

namespace RetryProxy.Core.Tls;

/// <summary>更新指纹的结果。</summary>
public enum FingerprintUpdateOutcome
{
    Unchanged,
    Updated,
    Rejected,
}

public sealed record FingerprintUpdate(FingerprintUpdateOutcome Outcome, ClientHelloFingerprint Current, ClientHelloFingerprint? Previous, string? Reason);

/// <summary>
/// 当前使用的 Claude Code ClientHello 指纹：内置一份实抓结果；从本机 Claude Code 重新抓到结构不同、
/// 且本实现能逐字节复现的新指纹时替换并保存，下次启动沿用。新连接立即使用新指纹。
/// </summary>
public sealed class TlsFingerprintStore
{
    public const string FileName = "claude-code-client-hello.bin";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);

    private static readonly Lazy<ClientHelloFingerprint> BuiltinFingerprint = new(LoadBuiltin);

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Func<CancellationToken, Task<byte[]>> _capture;
    private ClientHelloFingerprint _current;
    private string? _directory;
    /// <summary>上次检查的单调时钟时间戳（Stopwatch），不受改系统时间影响；null 表示还没检查过。</summary>
    private long? _lastRefresh;

    public TlsFingerprintStore(Func<CancellationToken, Task<byte[]>>? capture = null)
    {
        _capture = capture ?? (cancellationToken => ClaudeHelloCapture.CaptureAsync(null, ClaudeHelloCapture.DefaultTimeout, cancellationToken));
        _current = Builtin;
        Source = "内置";
    }

    /// <summary>应用内所有通道共用的实例；启动时由界面层指定保存目录。测试程序集启动时换成不抓取本机 Claude Code 的实例。</summary>
    public static TlsFingerprintStore Shared { get; internal set; } = new();

    /// <summary>随程序发布的指纹（Claude Code 2.1.283，Bun / BoringSSL）。</summary>
    public static ClientHelloFingerprint Builtin => BuiltinFingerprint.Value;

    public ClientHelloFingerprint Current => Volatile.Read(ref _current);

    /// <summary>当前指纹的来源说明，用于日志。</summary>
    public string Source { get; private set; }

    /// <summary>指定保存目录，并载入上次保存的指纹；文件缺失或不可用时沿用内置指纹。</summary>
    public void Configure(string directory)
    {
        _directory = directory;
        var path = Path.Combine(directory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var saved = ClientHelloFingerprint.Parse(File.ReadAllBytes(path));
            if (Check(saved) is null)
            {
                Volatile.Write(ref _current, saved);
                Source = $"本机抓取于 {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
        }
    }

    /// <summary>
    /// 采用抓到的 ClientHello：不可用时拒绝，结构与当前相同时不变，否则替换并保存。
    /// 例：Claude Code 升级前后只是随机数、公钥不同 → Unchanged；扩展顺序变了且能复现 → Updated；
    /// 新增了本实现不支持的扩展（如 GREASE 随机值）→ Rejected，继续用旧指纹。
    /// </summary>
    public FingerprintUpdate Apply(byte[] record)
    {
        ClientHelloFingerprint captured;
        try
        {
            captured = ClientHelloFingerprint.Parse(record);
        }
        catch (FormatException error)
        {
            return new FingerprintUpdate(FingerprintUpdateOutcome.Rejected, Current, null, error.Message);
        }

        if (Check(captured) is { } reason)
        {
            return new FingerprintUpdate(FingerprintUpdateOutcome.Rejected, Current, null, reason);
        }

        var previous = Current;
        if (captured.SameStructure(previous))
        {
            return new FingerprintUpdate(FingerprintUpdateOutcome.Unchanged, previous, null, null);
        }

        var masked = ClientHelloFingerprint.Parse(captured.Masked());
        Volatile.Write(ref _current, masked);
        Source = $"本机抓取于 {DateTime.Now:yyyy-MM-dd HH:mm}";
        string? saveError = null;
        if (_directory is { } directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, FileName);
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, masked.Record);
                File.Move(temporary, path, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 只记异常类型，不记原始错误文本。
                saveError = $"保存失败（{error.GetType().Name}），本次运行内有效";
            }
        }

        return new FingerprintUpdate(FingerprintUpdateOutcome.Updated, masked, previous, saveError);
    }

    /// <summary>
    /// 距上次检查超过 <see cref="RefreshInterval"/> 时从本机 Claude Code 重新抓取并比对；
    /// 多个通道同时调用只执行一次。失败只记日志，继续使用现有指纹。
    /// </summary>
    public async Task RefreshAsync(RouteLogger logger, CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRefresh is { } last && Stopwatch.GetElapsedTime(last) < RefreshInterval)
            {
                return;
            }

            _lastRefresh = Stopwatch.GetTimestamp();
            byte[] record;
            try
            {
                record = await _capture(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // 抓取组件只抛固定文案的 CliException / TimeoutException；其他异常只记类型，不记原始错误文本。
                var reason = error is CliException or TimeoutException ? error.Message : $"抓取时出现异常（{error.GetType().Name}）";
                logger.Warn($"抓取本机 Claude Code TLS 指纹失败：{reason}；继续使用{Source}指纹（JA4 {Current.Ja4}）");
                return;
            }

            var update = Apply(record);
            switch (update.Outcome)
            {
                case FingerprintUpdateOutcome.Unchanged:
                    logger.Info($"已核对本机 Claude Code TLS 指纹，与当前一致（JA4 {update.Current.Ja4}）");
                    break;
                case FingerprintUpdateOutcome.Updated:
                    logger.Info($"本机 Claude Code TLS 指纹已变化，新连接改用新指纹：JA4 {update.Previous!.Ja4} → {update.Current.Ja4}"
                                + (update.Reason is { } saveError ? $"（{saveError}）" : string.Empty));
                    break;
                default:
                    logger.Warn($"本机 Claude Code TLS 指纹无法复现：{update.Reason}；继续使用{Source}指纹（JA4 {Current.Ja4}）");
                    break;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>仅供测试：让下一次 <see cref="RefreshAsync"/> 立即检查。</summary>
    internal void MakeRefreshDueForTest()
    {
        _lastRefresh = null;
    }

    /// <summary>
    /// 指纹可用返回 null，否则返回原因：须只含支持的参数，且本实现生成的 ClientHello 与之逐字节一致（随机字段除外）。
    /// 例：抓到的 status_request 扩展带了 responder 列表，而本实现只会发空列表 → 两者清零随机字段后仍不相等 → 拒绝。
    /// </summary>
    internal static string? Check(ClientHelloFingerprint fingerprint)
    {
        if (fingerprint.UnsupportedReason() is { } reason)
        {
            return reason;
        }

        try
        {
            return Reproduce(fingerprint).SameStructure(fingerprint) ? null : "按该指纹生成的握手与抓到的不一致";
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException)
        {
            return $"按该指纹生成握手失败（{error.GetType().Name}）";
        }
    }

    /// <summary>用本实现按指纹生成一份 ClientHello 记录（不联网）。</summary>
    internal static ClientHelloFingerprint Reproduce(ClientHelloFingerprint fingerprint)
    {
        var host = fingerprint.ServerName ?? "localhost";
        var protocol = new OrderedClientProtocol(fingerprint);
        protocol.Connect(new FingerprintTlsClient(fingerprint, host, new ServerCertificateValidator(host)));
        var output = new byte[protocol.GetAvailableOutputBytes()];
        protocol.ReadOutput(output, 0, output.Length);
        if (output.Length < 5)
        {
            throw new IOException("未生成 ClientHello");
        }

        var length = 5 + ((output[3] << 8) | output[4]);
        return ClientHelloFingerprint.Parse(output.AsSpan(0, Math.Min(length, output.Length)));
    }

    private static ClientHelloFingerprint LoadBuiltin()
    {
        using var stream = typeof(TlsFingerprintStore).Assembly.GetManifestResourceStream("RetryProxy.Core.Tls.claude-code-client-hello.bin")
            ?? throw new InvalidOperationException("缺少内置 TLS 指纹资源");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ClientHelloFingerprint.Parse(buffer.ToArray());
    }
}
