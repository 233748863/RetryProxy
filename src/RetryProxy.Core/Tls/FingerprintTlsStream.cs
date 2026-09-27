using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;

namespace RetryProxy.Core.Tls;

/// <summary>
/// 用 BouncyCastle 非阻塞模式在底层连接上跑 TLS，对外是全异步的明文流，交给 HttpClient 当作普通连接使用。
/// 非阻塞模式 = BouncyCastle 不自己读写网络：收到的密文用 OfferInput 喂进去，要发的密文用 ReadOutput 取出来自己发。
/// 例：HttpClient 写入 "POST /v1/messages …" → WriteApplicationData 加密 → ReadOutput 取出 TLS 记录 → 写到 TCP。
/// 协议对象的调用都在 <see cref="_gate"/> 内完成；写底层连接由 <see cref="_writeLock"/> 串行化，保证记录顺序。
/// </summary>
internal sealed class FingerprintTlsStream : Stream
{
    private readonly Stream _transport;
    private readonly TlsClientProtocol _protocol;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly byte[] _cipherBuffer = new byte[32 * 1024];
    private readonly byte[] _plainBuffer = new byte[16 * 1024];
    /// <summary>待发密文的复用缓冲，只在持有 <see cref="_writeLock"/> 时使用；不够大时按需扩容。</summary>
    private byte[] _outputBuffer = new byte[32 * 1024];
    private int _disposed;

    private FingerprintTlsStream(Stream transport, TlsClientProtocol protocol)
    {
        _transport = transport;
        _protocol = protocol;
    }

    /// <summary>在已连通的底层流上完成握手；失败时抛出 <see cref="IOException"/>，底层流由调用方释放。</summary>
    public static async Task<FingerprintTlsStream> ConnectAsync(Stream transport, string host, ClientHelloFingerprint fingerprint,
        TlsAuthentication authentication, CancellationToken cancellationToken)
    {
        var protocol = new OrderedClientProtocol(fingerprint);
        var stream = new FingerprintTlsStream(transport, protocol);
        try
        {
            protocol.Connect(new FingerprintTlsClient(fingerprint, host, authentication));
            await stream.FlushOutputAsync(cancellationToken).ConfigureAwait(false);
            while (protocol.IsHandshaking)
            {
                var read = await transport.ReadAsync(stream._cipherBuffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new FingerprintConnectException("加密握手时上游断开了连接");
                }

                protocol.OfferInput(stream._cipherBuffer, 0, read);
                await stream.FlushOutputAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ServerCertificateException error)
        {
            await stream.TrySendPendingAsync().ConfigureAwait(false);
            throw new FingerprintConnectException(error.Plain, error);
        }
        catch (TlsFatalAlertReceived error)
        {
            throw new FingerprintConnectException($"上游拒绝了加密握手（TLS 告警 {AlertDescription.GetText(error.AlertDescription)}）", error);
        }
        catch (TlsException error)
        {
            await stream.TrySendPendingAsync().ConfigureAwait(false);
            throw new FingerprintConnectException("加密连接（TLS 指纹）没建立起来", error);
        }

        return stream;
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                bool pendingOutput;
                lock (_gate)
                {
                    // 零长度读取用于等待数据到达：有数据即返回 0，不消耗。
                    if (_protocol.GetAvailableInputBytes() > 0)
                    {
                        // 调用方给的是数组时直接解密到里面，省一次复制（HttpClient 的读缓冲就是数组）。
                        if (MemoryMarshal.TryGetArray<byte>(buffer, out var segment))
                        {
                            return _protocol.ReadInput(segment.Array!, segment.Offset, segment.Count);
                        }

                        var count = _protocol.ReadInput(_plainBuffer, 0, Math.Min(buffer.Length, _plainBuffer.Length));
                        _plainBuffer.AsSpan(0, count).CopyTo(buffer.Span);
                        return count;
                    }

                    if (_protocol.IsClosed)
                    {
                        return 0;
                    }
                }

                var read = await _transport.ReadAsync(_cipherBuffer, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    try
                    {
                        if (read == 0)
                        {
                            _protocol.CloseInput();
                        }
                        else
                        {
                            _protocol.OfferInput(_cipherBuffer, 0, read);
                        }
                    }
                    catch (EndOfStreamException error)
                    {
                        throw new IOException("上游连接在 TLS 记录中途断开", error);
                    }

                    pendingOutput = _protocol.GetAvailableOutputBytes() > 0;
                }

                if (pendingOutput)
                {
                    await FlushOutputAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _protocol.WriteApplicationData(buffer.Span);
            }

            await DrainOutputAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private async Task FlushOutputAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DrainOutputAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>调用方须持有 <see cref="_writeLock"/>：取出与写出之间不被别的写入插队。</summary>
    private async Task DrainOutputAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int count;
            lock (_gate)
            {
                var available = _protocol.GetAvailableOutputBytes();
                if (available == 0)
                {
                    return;
                }

                if (available > _outputBuffer.Length)
                {
                    _outputBuffer = new byte[Math.Max(available, _outputBuffer.Length * 2)];
                }

                count = _protocol.ReadOutput(_outputBuffer, 0, available);
            }

            await _transport.WriteAsync(_outputBuffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>握手失败后尽量把告警发给服务器，不影响抛出原异常。</summary>
    private async Task TrySendPendingAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await FlushOutputAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _transport.Dispose();
        }

        base.Dispose(disposing);
    }
}
