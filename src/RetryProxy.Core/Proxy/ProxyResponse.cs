using System;
using System.Collections.Generic;
using System.Threading;

namespace RetryProxy.Core.Proxy;

/// <summary>交给客户端的响应：状态、头，以及按需拉取的正文流。正文流失败时抛 <see cref="ResponseStreamException"/>，宿主据此切断连接。</summary>
internal sealed class ProxyResponse
{
    public ProxyResponse(int status, Internal.HeaderList headers, Func<CancellationToken, IAsyncEnumerable<ReadOnlyMemory<byte>>> body, long? contentLength)
    {
        Status = status;
        Headers = headers;
        Body = body;
        ContentLength = contentLength;
    }

    public int Status { get; }

    public Internal.HeaderList Headers { get; }

    public Func<CancellationToken, IAsyncEnumerable<ReadOnlyMemory<byte>>> Body { get; }

    /// <summary>已知长度的缓冲正文；流式转发为 null。</summary>
    public long? ContentLength { get; }

    public static ProxyResponse Buffered(int status, Internal.HeaderList headers, ReadOnlyMemory<byte> body)
    {
        return new ProxyResponse(status, headers, _ => Single(body), body.Length);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Single(ReadOnlyMemory<byte> body)
    {
        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
        if (!body.IsEmpty)
        {
            yield return body;
        }
    }
}

/// <summary>响应正文在转发途中失败（对应 Rust 正文流里 yield 的 io::Error）。</summary>
internal sealed class ResponseStreamException : Exception
{
    public ResponseStreamException(string message)
        : base(message)
    {
    }
}

/// <summary>等待生成阶段没有拿到可转发的内容（对应 AttemptReadError::NoGeneration）。</summary>
internal sealed class NoGenerationException : Exception
{
    public NoGenerationException(string reason)
        : base(reason)
    {
    }
}

internal sealed record BufferedResponse(int Status, Internal.HeaderList Headers, ReadOnlyMemory<byte> Body);

internal enum ProxyErrorKind
{
    None,
    Cancelled,
    DeadlineExceeded,
    Body,
    Dropped,
}
