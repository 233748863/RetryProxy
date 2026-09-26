using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace RetryProxy.Core.Internal;

/// <summary>
/// 增量解压上游响应正文（Content-Encoding：gzip / deflate / br / zstd），只供代理解析统计与事件；
/// 转发给客户端的仍是原始压缩字节。解压出错或单块解出过大后置 <see cref="Failed"/>，此后不再输出，
/// 调用方按“无法解析的正文”处理（与未开启压缩透传前遇到压缩响应时一致）。
/// </summary>
internal sealed class ContentDecoder : IDisposable
{
    /// <summary>单块压缩数据最多解出的字节数；超过视为异常数据，停止解析以免内存被撑爆。</summary>
    public const int MaxDecodedBytesPerChunk = 32 * 1024 * 1024;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private readonly string _coding;
    private readonly ByteBuffer _output = new(4096);
    private readonly byte[] _scratch = new byte[16 * 1024];
    private FeedStream? _input;
    private Stream? _stream;
    private BrotliDecoder _brotli;
    private ZstdSharp.Decompressor? _zstd;
    private bool _zlib;
    private bool _rawDeflate;
    private uint _crc = 0xFFFFFFFF;
    private uint _adlerA = 1;
    private uint _adlerB;
    private ulong _decodedBytes;
    private readonly byte[] _tail = new byte[8];
    private int _tailLength;

    private ContentDecoder(string coding)
    {
        _coding = coding;
        if (coding == "zstd")
        {
            _zstd = new ZstdSharp.Decompressor();
        }
        else if (coding != "br")
        {
            _input = new FeedStream();
        }
    }

    /// <summary>规范化后的编码名（小写）。</summary>
    public string Coding => _coding;

    public bool Failed { get; private set; }

    /// <summary>
    /// 压缩流已完整结束（br / zstd 由解码器报告；gzip / zlib 以尾部校验值与已解出内容吻合为准）。
    /// 裸 deflate 没有结束标记，始终为 false。
    /// </summary>
    public bool Ended { get; private set; }

    /// <summary>能否识别结束标记；裸 deflate 不能。</summary>
    public bool EndDetectable => !_rawDeflate;

    /// <summary>响应是否声明了 identity 以外的内容编码。</summary>
    public static bool IsEncoded(string? contentEncoding) => Normalize(contentEncoding) is not ("" or "identity");

    /// <summary>该编码能否由本类解压（多重编码不支持）。</summary>
    public static bool IsSupported(string? contentEncoding) => Normalize(contentEncoding) is "gzip" or "x-gzip" or "deflate" or "br" or "zstd";

    /// <summary>Accept-Encoding 列出的编码是否都能解压（含 identity；通配符 * 不行），决定能否原样转发该请求头。</summary>
    public static bool AcceptsOnlySupported(string acceptEncoding)
    {
        foreach (var item in acceptEncoding.Split(','))
        {
            var coding = item.Split(';')[0];
            if (Normalize(coding) is not ("" or "identity") && !IsSupported(coding))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>为已编码且可解压的响应创建解码器；未编码或不支持时返回 null。</summary>
    public static ContentDecoder? Create(string? contentEncoding)
    {
        if (!IsEncoded(contentEncoding) || !IsSupported(contentEncoding))
        {
            return null;
        }

        var coding = Normalize(contentEncoding);
        return new ContentDecoder(coding == "x-gzip" ? "gzip" : coding);
    }

    /// <summary>一次性解压完整正文；未编码时原样返回，不支持或解压失败返回 null。</summary>
    public static byte[]? DecodeAll(string? contentEncoding, ReadOnlySpan<byte> body)
    {
        if (!IsEncoded(contentEncoding))
        {
            return body.ToArray();
        }

        using var decoder = Create(contentEncoding);
        if (decoder is null)
        {
            return null;
        }

        var decoded = decoder.Push(body).ToArray();
        return decoder.Failed ? null : decoded;
    }

    /// <summary>喂入一块原始字节，返回本次新解出的内容（下次调用前有效）。流结束后的多余字节忽略。</summary>
    public ReadOnlySpan<byte> Push(ReadOnlySpan<byte> chunk)
    {
        _output.Clear();
        if (Failed || Ended || chunk.IsEmpty)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        try
        {
            var ok = _coding switch
            {
                "br" => PushBrotli(chunk),
                "zstd" => PushZstd(chunk),
                _ => PushInflate(chunk),
            };
            if (!ok)
            {
                Fail();
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or ZstdSharp.ZstdException or InvalidOperationException)
        {
            Fail();
        }

        return _output.Span;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        _brotli.Dispose();
        _zstd?.Dispose();
        _zstd = null;
    }

    private bool PushBrotli(ReadOnlySpan<byte> chunk)
    {
        while (true)
        {
            var status = _brotli.Decompress(chunk, _scratch, out var consumed, out var written);
            chunk = chunk[consumed..];
            if (!Emit(written))
            {
                return false;
            }

            switch (status)
            {
                case OperationStatus.Done:
                    Ended = true;
                    return true;
                case OperationStatus.NeedMoreData:
                    return true;
                case OperationStatus.DestinationTooSmall:
                    continue;
                default:
                    return false;
            }
        }
    }

    private bool PushZstd(ReadOnlySpan<byte> chunk)
    {
        while (true)
        {
            var status = _zstd!.UnwrapStream(chunk, _scratch, out var consumed, out var written);
            chunk = chunk[consumed..];
            if (!Emit(written))
            {
                return false;
            }

            if (status == OperationStatus.Done)
            {
                Ended = true;
                return true;
            }

            if (status == OperationStatus.InvalidData)
            {
                return false;
            }

            if (status != OperationStatus.DestinationTooSmall && chunk.IsEmpty)
            {
                return true;
            }
        }
    }

    private bool PushInflate(ReadOnlySpan<byte> chunk)
    {
        _input!.Append(chunk);
        RememberTail(chunk);
        _stream ??= OpenInflate();
        if (_stream is null)
        {
            return true;
        }

        // 压缩流读空输入时返回 0 而不结束，之后喂入新数据可继续解压。
        int read;
        while ((read = _stream.Read(_scratch)) > 0)
        {
            if (!Emit(read))
            {
                return false;
            }
        }

        Ended = _coding == "gzip" ? GzipTrailerMatches() : !_rawDeflate && ZlibTrailerMatches();
        return true;
    }

    private bool Emit(int written)
    {
        if (written == 0)
        {
            return true;
        }

        if (written > MaxDecodedBytesPerChunk - _output.Length)
        {
            return false;
        }

        var bytes = _scratch.AsSpan(0, written);
        _output.Append(bytes);
        _decodedBytes += (ulong)written;
        if (_coding == "gzip")
        {
            var crc = _crc;
            foreach (var value in bytes)
            {
                crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            _crc = crc;
        }
        else if (_zlib)
        {
            var a = _adlerA;
            var b = _adlerB;
            foreach (var value in bytes)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }

            _adlerA = a;
            _adlerB = b;
        }

        return true;
    }

    private Stream? OpenInflate()
    {
        if (_coding == "gzip")
        {
            return new GZipStream(_input!, CompressionMode.Decompress, leaveOpen: true);
        }

        // HTTP 的 deflate 应为 zlib 封装，但也有服务端直接发裸 deflate：按前两字节的 zlib 头判断。
        var head = _input!.Pending;
        if (head.Length < 2)
        {
            return null;
        }

        _zlib = (head[0] & 0x0F) == 8 && ((head[0] << 8) | head[1]) % 31 == 0;
        _rawDeflate = !_zlib;
        return _zlib
            ? new ZLibStream(_input, CompressionMode.Decompress, leaveOpen: true)
            : new DeflateStream(_input, CompressionMode.Decompress, leaveOpen: true);
    }

    private void RememberTail(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length >= _tail.Length)
        {
            chunk[^_tail.Length..].CopyTo(_tail);
            _tailLength = _tail.Length;
            return;
        }

        var keep = Math.Min(_tailLength, _tail.Length - chunk.Length);
        _tail.AsSpan(_tailLength - keep, keep).CopyTo(_tail);
        chunk.CopyTo(_tail.AsSpan(keep));
        _tailLength = keep + chunk.Length;
    }

    /// <summary>gzip 尾部：CRC32 与原始长度（小端，长度取低 32 位）。</summary>
    private bool GzipTrailerMatches()
    {
        return _tailLength == 8
            && BinaryPrimitives.ReadUInt32LittleEndian(_tail) == ~_crc
            && BinaryPrimitives.ReadUInt32LittleEndian(_tail.AsSpan(4)) == (uint)_decodedBytes;
    }

    /// <summary>zlib 尾部：Adler-32（大端）。</summary>
    private bool ZlibTrailerMatches()
    {
        return _tailLength >= 4 && BinaryPrimitives.ReadUInt32BigEndian(_tail.AsSpan(_tailLength - 4)) == ((_adlerB << 16) | _adlerA);
    }

    private void Fail()
    {
        Failed = true;
        _output.Clear();
        Dispose();
    }

    private static string Normalize(string? contentEncoding) => (contentEncoding ?? string.Empty).Trim().ToLowerInvariant();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    /// <summary>可追加的只读输入流：没有待读数据时返回 0，供压缩流按需拉取。</summary>
    private sealed class FeedStream : Stream
    {
        private byte[] _buffer = new byte[4096];
        private int _start;
        private int _end;

        public ReadOnlySpan<byte> Pending => _buffer.AsSpan(_start, _end - _start);

        public void Append(ReadOnlySpan<byte> data)
        {
            var pending = _end - _start;
            if (_buffer.Length - _end < data.Length)
            {
                var target = pending + data.Length > _buffer.Length ? new byte[Math.Max(pending + data.Length, _buffer.Length * 2)] : _buffer;
                _buffer.AsSpan(_start, pending).CopyTo(target);
                _buffer = target;
                _start = 0;
                _end = pending;
            }

            data.CopyTo(_buffer.AsSpan(_end));
            _end += data.Length;
        }

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, _end - _start);
            _buffer.AsSpan(_start, count).CopyTo(buffer);
            _start += count;
            if (_start == _end)
            {
                _start = _end = 0;
            }

            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
