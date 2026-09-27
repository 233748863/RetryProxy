using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RetryProxy.Core.Internal;

/// <summary>
/// HTTP/1.1 请求方向的分帧状态机（不涉及网络，便于单测）：从连续字节流里切出每个请求的请求头，交给
/// <see cref="OnHead"/> 处理；正文按 Content-Length 计数或按 chunked 分块原样透传，结束后回到下一个请求头。
/// 例：<c>POST / …Content-Length: 2\r\n\r\n{}GET /next …\r\n\r\n</c> → 依次回调两次 OnHead，<c>{}</c> 原样输出。
/// 分帧信息缺失或格式不对时抛 <see cref="IOException"/>，由派生类决定失败还是放弃。
/// </summary>
internal abstract class Http1RequestFramer
{
    /// <summary>请求头上限；正常请求头远小于此值，超出说明分帧已错乱。</summary>
    private const int MaxHeadBytes = 1024 * 1024;
    /// <summary>chunked 分块长度行或尾部字段的单行上限。</summary>
    private const int MaxChunkLineBytes = 8 * 1024;

    private enum State
    {
        Head,
        Body,
        ChunkSize,
        ChunkData,
        ChunkDataEnd,
        ChunkTrailer,
    }

    private readonly MemoryStream _line = new();
    private State _state = State.Head;
    private long _remaining;

    /// <summary>请求头里的一行字段：头名（原大小写）与整行原文。</summary>
    protected readonly record struct HeadField(string Name, string Line);

    /// <summary>处理一段数据，返回应依次输出的数据段（请求头换成 <see cref="OnHead"/> 的结果，正文为输入切片）。</summary>
    protected List<ReadOnlyMemory<byte>> Frame(ReadOnlyMemory<byte> input)
    {
        var output = new List<ReadOnlyMemory<byte>>();
        while (!input.IsEmpty)
        {
            var consumed = _state switch
            {
                State.Head => ConsumeHead(input.Span, output),
                State.Body or State.ChunkData or State.ChunkDataEnd => PassThrough(input, output),
                _ => ConsumeChunkLine(input, output),
            };
            input = input[consumed..];
        }

        return output;
    }

    /// <summary>收到一个完整请求头（请求行 + 各字段，不含结尾空行），返回要输出的请求头字节（含结尾空行）。</summary>
    protected abstract ReadOnlyMemory<byte> OnHead(byte[] head, string requestLine, IReadOnlyList<HeadField> fields);

    private int ConsumeHead(ReadOnlySpan<byte> input, List<ReadOnlyMemory<byte>> output)
    {
        // 逐字节找 "\r\n\r\n"：已缓存的部分可能以 "\r\n\r" 结尾，所以连同缓存一起判断。
        for (var index = 0; index < input.Length; index++)
        {
            _line.WriteByte(input[index]);
            if (_line.Length > MaxHeadBytes)
            {
                throw new IOException("请求头过长");
            }

            if (input[index] == (byte)'\n' && EndsWithBlankLine())
            {
                var head = _line.ToArray();
                _line.SetLength(0);
                output.Add(ParseHead(head));
                return index + 1;
            }
        }

        return input.Length;
    }

    private bool EndsWithBlankLine()
    {
        if (_line.Length < 4)
        {
            return false;
        }

        var buffer = _line.GetBuffer();
        var end = (int)_line.Length;
        return buffer[end - 4] == '\r' && buffer[end - 3] == '\n' && buffer[end - 2] == '\r' && buffer[end - 1] == '\n';
    }

    private ReadOnlyMemory<byte> ParseHead(byte[] head)
    {
        var lines = Encoding.Latin1.GetString(head, 0, head.Length - 4).Split("\r\n");
        var fields = new List<HeadField>(lines.Length);
        long? contentLength = null;
        var chunked = false;
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new IOException("请求头格式无法识别");
            }

            var name = line[..colon];
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                {
                    throw new IOException("请求头 Content-Length 无效");
                }

                contentLength = length;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                     && value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }

            fields.Add(new HeadField(name, line));
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

        return OnHead(head, lines[0], fields);
    }

    private int PassThrough(ReadOnlyMemory<byte> input, List<ReadOnlyMemory<byte>> output)
    {
        var count = (int)Math.Min(input.Length, _remaining);
        output.Add(input[..count]);
        _remaining -= count;
        if (_remaining == 0)
        {
            (_state, _remaining) = _state switch
            {
                State.Body => (State.Head, 0L),
                // 分块数据之后固定跟一个 "\r\n"。
                State.ChunkData => (State.ChunkDataEnd, 2L),
                _ => (State.ChunkSize, 0L),
            };
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
            throw new IOException("分块长度行过长");
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
            throw new IOException("分块长度无效");
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
}
