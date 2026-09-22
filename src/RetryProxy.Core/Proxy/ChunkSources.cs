using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RetryProxy.Core.Proxy;

/// <summary>按块读取上游响应正文；EOF 返回 null，网络错误抛 <see cref="UpstreamException"/>。</summary>
internal interface IChunkSource : IDisposable
{
    Task<ReadOnlyMemory<byte>?> ReadAsync();
}

/// <summary>
/// 给 <see cref="IChunkSource"/> 加一层“未消费的挂起读取”：
/// 等待生成到期时如果决定继续转发，正在进行的读取不能丢，下一次读继续用它。
/// </summary>
internal sealed class ChunkReader : IDisposable
{
    private readonly IChunkSource _source;
    private Task<ReadOnlyMemory<byte>?>? _pending;
    private bool _finished;

    public ChunkReader(IChunkSource source)
    {
        _source = source;
    }

    public bool Finished => _finished;

    /// <summary>取当前挂起的读取（没有就发起一次）。完成后必须调用 <see cref="Consume"/>。</summary>
    public Task<ReadOnlyMemory<byte>?> Peek()
    {
        if (_finished)
        {
            return Task.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        return _pending ??= _source.ReadAsync();
    }

    /// <summary>取走已完成的读取结果；读取失败时把异常原样抛出。</summary>
    public ReadOnlyMemory<byte>? Consume(Task<ReadOnlyMemory<byte>?> completed)
    {
        _pending = null;
        var chunk = completed.GetAwaiter().GetResult();
        if (chunk is null)
        {
            _finished = true;
        }

        return chunk;
    }

    public void Dispose()
    {
        var pending = _pending;
        _pending = null;
        _source.Dispose();
        if (pending is not null && !pending.IsCompleted)
        {
            _ = pending.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}

/// <summary>HttpClient 响应正文的分块读取，带每次读取的空闲超时与可选的整体超时。</summary>
internal sealed class HttpChunkSource : IChunkSource
{
    private const int ChunkSize = 64 * 1024;

    private readonly HttpResponseMessage _response;
    private readonly Stream _stream;
    private readonly TimeSpan _readTimeout;
    private readonly CancellationTokenSource? _overall;
    private bool _disposed;

    public HttpChunkSource(HttpResponseMessage response, Stream stream, TimeSpan readTimeout, CancellationTokenSource? overall)
    {
        _response = response;
        _stream = stream;
        _readTimeout = readTimeout;
        _overall = overall;
    }

    public async Task<ReadOnlyMemory<byte>?> ReadAsync()
    {
        var buffer = new byte[ChunkSize];
        using var timeout = _overall is null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(_overall.Token);
        timeout.CancelAfter(_readTimeout);
        try
        {
            var read = await _stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            return new ReadOnlyMemory<byte>(buffer, 0, read);
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
        {
            throw UpstreamException.Timeout(false, error);
        }
        catch (ObjectDisposedException error)
        {
            throw new UpstreamException("response disposed", false, false, error);
        }
        catch (Exception error) when (error is not UpstreamException)
        {
            throw UpstreamException.From(error);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream.Dispose();
        }
        catch (Exception)
        {
        }

        _response.Dispose();
        _overall?.Dispose();
    }
}

/// <summary>先重放一段已读的前缀（或一次已发生的错误），再继续读底层源。</summary>
internal sealed class ReplayChunkSource : IChunkSource
{
    private readonly Queue<(ReadOnlyMemory<byte>? Chunk, Exception? Error)> _prefix;
    private readonly IChunkSource _inner;

    public ReplayChunkSource(IEnumerable<(ReadOnlyMemory<byte>? Chunk, Exception? Error)> prefix, IChunkSource inner)
    {
        _prefix = new Queue<(ReadOnlyMemory<byte>?, Exception?)>(prefix);
        _inner = inner;
    }

    public Task<ReadOnlyMemory<byte>?> ReadAsync()
    {
        if (_prefix.Count > 0)
        {
            var (chunk, error) = _prefix.Dequeue();
            if (error is not null)
            {
                return Task.FromException<ReadOnlyMemory<byte>?>(error);
            }

            return Task.FromResult(chunk);
        }

        return _inner.ReadAsync();
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>测试用：固定块序列。</summary>
internal sealed class ListChunkSource : IChunkSource
{
    private readonly Queue<ReadOnlyMemory<byte>> _chunks;

    public ListChunkSource(IEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        _chunks = new Queue<ReadOnlyMemory<byte>>(chunks);
    }

    public Task<ReadOnlyMemory<byte>?> ReadAsync()
    {
        return Task.FromResult(_chunks.Count > 0 ? _chunks.Dequeue() : (ReadOnlyMemory<byte>?)null);
    }

    public void Dispose()
    {
    }
}
