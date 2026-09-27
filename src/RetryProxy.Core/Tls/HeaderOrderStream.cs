using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Tls;

/// <summary>
/// 套在 TLS 连接外面的明文层（Claude 指纹 TLS 或 Codex 的系统 TLS）：把 HttpClient 写出的 HTTP/1.1 请求头按客户端原顺序重排后再加密发出。
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
/// 按顺序头重排请求头：分帧交给 <see cref="Http1RequestFramer"/>，这里只改写请求头。
/// 分帧无法识别时抛 <see cref="IOException"/> 让本次请求失败，绝不把内部顺序头漏给上游。
/// </summary>
internal sealed class RequestHeadReorderer : Http1RequestFramer
{
    /// <summary>重排后的请求头与紧随其后的一小段正文合并成一次写入，避免多拆出一个 TLS 记录。</summary>
    private const int CoalesceBytes = 64 * 1024;

    /// <summary>处理一段写入，返回应依次写给下层的数据段。</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Process(ReadOnlyMemory<byte> input) => Coalesce(Frame(input));

    /// <summary>
    /// 顺序头里列出的头按其顺序、用其大小写输出；没列出的头（例如代理自己加的）保持原相对顺序排在最后。
    /// 没有顺序头的请求原样输出。
    /// </summary>
    protected override ReadOnlyMemory<byte> OnHead(byte[] head, string requestLine, IReadOnlyList<HeadField> fields)
    {
        string[]? plan = null;
        var others = new List<HeadField>(fields.Count);
        foreach (var field in fields)
        {
            if (field.Name.Equals(HeaderOrderStream.PlanHeader, StringComparison.OrdinalIgnoreCase))
            {
                plan = field.Line[(field.Name.Length + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                others.Add(field);
            }
        }

        if (plan is null)
        {
            return head;
        }

        var builder = new StringBuilder(head.Length);
        builder.Append(requestLine).Append("\r\n");
        var used = new bool[others.Count];
        foreach (var planned in plan)
        {
            for (var index = 0; index < others.Count; index++)
            {
                if (!used[index] && others[index].Name.Equals(planned, StringComparison.OrdinalIgnoreCase))
                {
                    used[index] = true;
                    // 头名换成客户端原来的大小写，值原样保留。例：accept-encoding: gzip → Accept-Encoding: gzip。
                    var line = others[index].Line;
                    builder.Append(planned).Append(line, others[index].Name.Length, line.Length - others[index].Name.Length).Append("\r\n");
                }
            }
        }

        for (var index = 0; index < others.Count; index++)
        {
            if (!used[index])
            {
                builder.Append(others[index].Line).Append("\r\n");
            }
        }

        builder.Append("\r\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
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
