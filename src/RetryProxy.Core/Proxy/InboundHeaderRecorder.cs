using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Proxy;

/// <summary>客户端一个请求的请求行与请求头名（原顺序、原大小写，重名保留多次）。</summary>
internal sealed record InboundHead(string Method, string Target, IReadOnlyList<string> Names);

/// <summary>
/// 记录客户端请求头的原始顺序与大小写。Kestrel 会把常见头按内部固定顺序重排、头名改成标准大小写，
/// 所以在 Kestrel 解析之前，用连接中间件旁路读取同一份原始字节，按请求依次记下头名。
/// 例：Claude Code 发 <c>Accept, Authorization, Content-Type, User-Agent, …, Connection, Host, Accept-Encoding, Content-Length</c>，
/// Kestrel 给出的是 <c>Accept, Connection, Host, User-Agent, …</c>，本类记下的是前者。
/// 只记录不改写；分帧无法识别时停止记录，之后的请求退回 Kestrel 的顺序。
/// </summary>
internal sealed class InboundHeaderRecorder : Http1RequestFramer
{
    /// <summary>未被取走的记录上限：健康检查等不经转发的请求会留下记录，超出时丢弃最旧的。</summary>
    private const int MaxPending = 16;

    private readonly Queue<InboundHead> _pending = new();
    private bool _disabled;

    /// <summary>
    /// Kestrel 连接中间件：把连接的读方向换成先经过记录器的管道，并把记录器挂到连接特性上，
    /// 请求处理时用 <see cref="Take(HttpContext)"/> 取回本请求的记录。
    /// </summary>
    public static ConnectionDelegate Middleware(ConnectionDelegate next) => async connection =>
    {
        var recorder = new InboundHeaderRecorder();
        var original = connection.Transport;
        var input = PipeReader.Create(new RecordingStream(original.Input.AsStream(), recorder));
        connection.Features.Set(recorder);
        connection.Transport = new DuplexPipe(input, original.Output);
        try
        {
            await next(connection).ConfigureAwait(false);
        }
        finally
        {
            connection.Transport = original;
            await input.CompleteAsync().ConfigureAwait(false);
        }
    };

    /// <summary>取本请求的原始请求头名；连接未挂记录器或记录对不上时返回 null（沿用 Kestrel 的顺序）。</summary>
    public static IReadOnlyList<string>? Take(HttpContext context)
    {
        var recorder = context.Features.Get<InboundHeaderRecorder>();
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        return recorder is null || target is null ? null : recorder.Take(context.Request.Method, target);
    }

    /// <summary>
    /// 按请求行对号取记录：同一连接上的请求按顺序处理，排在前面、对不上的是没经过转发的请求（如健康检查），直接丢弃。
    /// </summary>
    public IReadOnlyList<string>? Take(string method, string target)
    {
        lock (_pending)
        {
            while (_pending.TryDequeue(out var head))
            {
                if (head.Method == method && head.Target == target)
                {
                    return head.Names;
                }
            }

            return null;
        }
    }

    /// <summary>旁路观察一段刚从连接读到的原始字节。</summary>
    public void Observe(ReadOnlyMemory<byte> input)
    {
        if (_disabled || input.IsEmpty)
        {
            return;
        }

        try
        {
            Frame(input);
        }
        catch (IOException)
        {
            // 格式不对的请求交给 Kestrel 自己拒绝；这里只是停止记录，不影响连接。
            _disabled = true;
            lock (_pending)
            {
                _pending.Clear();
            }
        }
    }

    protected override ReadOnlyMemory<byte> OnHead(byte[] head, string requestLine, IReadOnlyList<HeadField> fields)
    {
        // 请求行：方法 SP 目标 SP 版本。例：POST /v1/messages?beta=true HTTP/1.1。
        var parts = requestLine.Split(' ');
        if (parts.Length != 3)
        {
            throw new IOException("请求行格式无法识别");
        }

        var names = new List<string>(fields.Count);
        foreach (var field in fields)
        {
            names.Add(field.Name);
        }

        lock (_pending)
        {
            if (_pending.Count == MaxPending)
            {
                _pending.Dequeue();
            }

            _pending.Enqueue(new InboundHead(parts[0], parts[1], names));
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }

    /// <summary>只读包装：读到的字节原样返回给 Kestrel，同时交给记录器。</summary>
    private sealed class RecordingStream(Stream inner, InboundHeaderRecorder recorder) : Stream
    {
        public override bool CanRead => true;

        public override bool CanWrite => false;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            recorder.Observe(buffer[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            recorder.Observe(buffer.AsMemory(offset, read));
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
