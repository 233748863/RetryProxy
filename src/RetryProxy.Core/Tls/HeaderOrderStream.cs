using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RetryProxy.Core.Tls;

/// <summary>
/// 套在指纹 TLS 连接外面的明文层：把 HttpClient 写出的 HTTP/1.1 请求头按客户端原顺序重排后再加密发出。
/// HttpClient 固定把 Content-Type / Content-Length 写在最后，这一层按请求里附带的内部头
/// <see cref="PlanHeader"/> 给出的顺序与大小写重写请求头，并删掉这个内部头；正文逐字节原样透传。
/// 例：HttpClient 写 <c>Host, Accept, User-Agent, Authorization, Content-Type, Content-Length</c>，
/// 顺序头为 <c>Accept,Authorization,Content-Type,User-Agent,Host,Content-Length</c> → 上游按后者收到。
/// 同一连接上的多个请求按 Content-Length / chunked 分帧依次处理，读方向不做任何改动。
/// </summary>
internal sealed class HeaderOrderStream : Stream
{
    /// <summary>内部顺序头：值为逗号分隔的头名，只在本层消费，不会发到上游。</summary>
    public const string PlanHeader = "x-retry-header-order";

    private readonly Stream _inner;
    private readonly RequestHeadReorderer _reorderer = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public HeaderOrderStream(Stream inner) => _inner = inner;

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var segment in _reorderer.Process(buffer))
            {
                await _inner.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
            }
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

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 请求方向的分帧与重排状态机（不涉及网络，便于单测）。
/// 请求头 → 攒到空行后重排输出；正文 → 按 Content-Length 计数或按 chunked 分块透传；结束后回到下一个请求头。
/// 分帧信息缺失或格式不对时抛 <see cref="IOException"/> 让本次请求失败，绝不把内部顺序头漏给上游。
/// </summary>
internal sealed class RequestHeadReorderer
{
    /// <summary>请求头上限；HttpClient 写出的请求头远小于此值，超出说明分帧已错乱。</summary>
    private const int MaxHeadBytes = 1024 * 1024;
    /// <summary>chunked 分块长度行或尾部字段的单行上限。</summary>
    private const int MaxChunkLineBytes = 8 * 1024;
    /// <summary>重排后的请求头与紧随其后的一小段正文合并成一次写入，避免多拆出一个 TLS 记录。</summary>
    private const int CoalesceBytes = 64 * 1024;

    private enum State
    {
        Head,
        Body,
        ChunkSize,
        ChunkData,
        ChunkDataEnd,
        ChunkTrailer,
    }

    private State _state = State.Head;
    private readonly MemoryStream _line = new();
    private long _remaining;

    /// <summary>处理一段写入，返回应依次写给下层的数据段。</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Process(ReadOnlyMemory<byte> input)
    {
        var output = new List<ReadOnlyMemory<byte>>();
        while (!input.IsEmpty)
        {
            var consumed = _state switch
            {
                State.Head => ConsumeHead(input.Span, output),
                State.Body => PassThrough(input, output),
                State.ChunkData => PassThrough(input, output),
                State.ChunkDataEnd => PassThrough(input, output),
                _ => ConsumeChunkLine(input, output),
            };
            input = input[consumed..];
        }

        return Coalesce(output);
    }

    private int ConsumeHead(ReadOnlySpan<byte> input, List<ReadOnlyMemory<byte>> output)
    {
        // 逐字节找 "\r\n\r\n"：请求头已缓存的部分可能以 "\r\n\r" 结尾，所以连同缓存一起判断。
        for (var index = 0; index < input.Length; index++)
        {
            _line.WriteByte(input[index]);
            if (_line.Length > MaxHeadBytes)
            {
                throw new IOException("请求头过长，无法按原顺序发送");
            }

            if (input[index] == (byte)'\n' && EndsWithBlankLine(_line))
            {
                var head = _line.ToArray();
                _line.SetLength(0);
                output.Add(Rewrite(head));
                return index + 1;
            }
        }

        return input.Length;
    }

    private static bool EndsWithBlankLine(MemoryStream line)
    {
        if (line.Length < 4)
        {
            return false;
        }

        var buffer = line.GetBuffer();
        var end = (int)line.Length;
        return buffer[end - 4] == '\r' && buffer[end - 3] == '\n' && buffer[end - 2] == '\r' && buffer[end - 1] == '\n';
    }

    /// <summary>
    /// 重排一个完整请求头，并据此决定正文的分帧方式。
    /// 顺序头里列出的头按其顺序、用其大小写输出；没列出的头（例如代理自己加的）保持原相对顺序排在最后。
    /// </summary>
    private byte[] Rewrite(byte[] head)
    {
        var text = Encoding.Latin1.GetString(head, 0, head.Length - 4);
        var lines = text.Split("\r\n");
        var fields = new List<(string Name, string Line)>(lines.Length);
        string[]? plan = null;
        long? contentLength = null;
        var chunked = false;
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new IOException("请求头格式无法识别，无法按原顺序发送");
            }

            var name = line[..colon];
            var value = line[(colon + 1)..].Trim();
            if (name.Equals(HeaderOrderStream.PlanHeader, StringComparison.OrdinalIgnoreCase))
            {
                plan = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                continue;
            }

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                {
                    throw new IOException("请求头 Content-Length 无效，无法按原顺序发送");
                }

                contentLength = length;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                     && value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }

            fields.Add((name, line));
        }

        // 请求没有 Content-Length / chunked 就没有正文（RFC 9112 §6.3）；两者都有时以 chunked 为准。
        if (chunked)
        {
            _state = State.ChunkSize;
        }
        else if (contentLength is > 0)
        {
            _state = State.Body;
            _remaining = contentLength.Value;
        }
        else
        {
            _state = State.Head;
        }

        var builder = new StringBuilder(text.Length + 4);
        builder.Append(lines[0]).Append("\r\n");
        if (plan is not null)
        {
            var used = new bool[fields.Count];
            foreach (var planned in plan)
            {
                for (var index = 0; index < fields.Count; index++)
                {
                    if (!used[index] && fields[index].Name.Equals(planned, StringComparison.OrdinalIgnoreCase))
                    {
                        used[index] = true;
                        // 头名换成客户端原来的大小写，值原样保留。例：accept-encoding: gzip → Accept-Encoding: gzip。
                        builder.Append(planned).Append(fields[index].Line, fields[index].Name.Length, fields[index].Line.Length - fields[index].Name.Length).Append("\r\n");
                    }
                }
            }

            for (var index = 0; index < fields.Count; index++)
            {
                if (!used[index])
                {
                    builder.Append(fields[index].Line).Append("\r\n");
                }
            }
        }
        else
        {
            foreach (var field in fields)
            {
                builder.Append(field.Line).Append("\r\n");
            }
        }

        builder.Append("\r\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private int PassThrough(ReadOnlyMemory<byte> input, List<ReadOnlyMemory<byte>> output)
    {
        var count = (int)Math.Min(input.Length, _remaining);
        output.Add(input[..count]);
        _remaining -= count;
        if (_remaining == 0)
        {
            _state = _state switch
            {
                State.Body => State.Head,
                State.ChunkData => State.ChunkDataEnd,
                _ => State.ChunkSize,
            };
            if (_state == State.ChunkDataEnd)
            {
                _remaining = 2;
            }
        }

        return count;
    }

    /// <summary>
    /// chunked 的长度行与尾部字段：原样透传，同时读出分块长度。
    /// 例：<c>1a;ext=x\r\n</c> → 后面 26 字节是数据；<c>0\r\n</c> → 进入尾部，读到空行算本请求结束。
    /// </summary>
    private int ConsumeChunkLine(ReadOnlyMemory<byte> input, List<ReadOnlyMemory<byte>> output)
    {
        var span = input.Span;
        var newline = span.IndexOf((byte)'\n');
        var count = newline < 0 ? span.Length : newline + 1;
        _line.Write(span[..count]);
        output.Add(input[..count]);
        if (_line.Length > MaxChunkLineBytes)
        {
            throw new IOException("分块长度行过长，无法按原顺序发送");
        }

        if (newline < 0)
        {
            return count;
        }

        var line = Encoding.Latin1.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r', '\n');
        _line.SetLength(0);
        if (_state == State.ChunkTrailer)
        {
            if (line.Length == 0)
            {
                _state = State.Head;
            }

            return count;
        }

        var extension = line.IndexOf(';');
        var sizeText = (extension < 0 ? line : line[..extension]).Trim();
        if (!long.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
        {
            throw new IOException("分块长度无效，无法按原顺序发送");
        }

        if (size == 0)
        {
            _state = State.ChunkTrailer;
        }
        else
        {
            _state = State.ChunkData;
            _remaining = size;
        }

        return count;
    }

    private static IReadOnlyList<ReadOnlyMemory<byte>> Coalesce(List<ReadOnlyMemory<byte>> output)
    {
        if (output.Count < 2)
        {
            return output;
        }

        var total = 0L;
        foreach (var segment in output)
        {
            total += segment.Length;
        }

        if (total > CoalesceBytes)
        {
            return output;
        }

        var merged = new byte[total];
        var offset = 0;
        foreach (var segment in output)
        {
            segment.Span.CopyTo(merged.AsSpan(offset));
            offset += segment.Length;
        }

        return new[] { new ReadOnlyMemory<byte>(merged) };
    }
}
