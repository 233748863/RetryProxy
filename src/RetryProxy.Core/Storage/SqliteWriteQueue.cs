using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace RetryProxy.Core.Storage;

/// <summary>
/// 单写者写队列：把写入动作排到一条后台任务上串行执行（SQLite 同一时刻只允许一个写事务，
/// 串行化同时避免 SQLITE_BUSY）。入队不阻塞调用线程；返回的任务在动作执行完后完成，
/// 失败时抛出动作的异常。例：<c>await queue.ExecuteAsync(c =&gt; Insert(c, row));</c>，
/// 退出前 <c>await queue.FlushAsync()</c> 等全部落库。
/// 注意：不要在写动作里再入队（会等自己而卡死）。
/// </summary>
public sealed class SqliteWriteQueue : IDisposable
{
    private sealed class WorkItem
    {
        public WorkItem(Action<SqliteConnection> action, TaskCompletionSource completion)
        {
            Action = action;
            Completion = completion;
        }

        public Action<SqliteConnection> Action { get; }

        public TaskCompletionSource Completion { get; }
    }

    private readonly Channel<WorkItem> _channel;
    private readonly Task _worker;

    public SqliteWriteQueue(SqliteDatabase database, int capacity = 1024)
    {
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = Task.Run(() => WorkerAsync(database));
    }

    /// <summary>排队一个写动作；返回的任务在写完时完成，失败时以动作的异常结束。队列满时异步等待。</summary>
    public async Task ExecuteAsync(Action<SqliteConnection> action, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(action, completion);
        try
        {
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException("写队列已关闭，无法再写入");
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>排队一个写动作并取回结果（例如 UPSERT ... RETURNING 或 last_insert_rowid）。</summary>
    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> action, CancellationToken cancellationToken = default)
    {
        T result = default!;
        await ExecuteAsync(connection => result = action(connection), cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>同步版：排队并等写完（统计写入沿用它，保持与旧文件追加一致的同步语义）。</summary>
    public void Execute(Action<SqliteConnection> action, CancellationToken cancellationToken = default)
        => ExecuteAsync(action, cancellationToken).GetAwaiter().GetResult();

    /// <summary>等队列里已排的动作全部执行完（退出前与测试用）。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(static _ => { }, cancellationToken).ConfigureAwait(false);

    private async Task WorkerAsync(SqliteDatabase database)
    {
        SqliteConnection? connection = null;
        try
        {
            connection = database.Connect();
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    try
                    {
                        item.Action(connection);
                        item.Completion.TrySetResult();
                    }
                    catch (Exception error)
                    {
                        // 单条动作失败不影响后续：调用方各自看到自己的异常。
                        item.Completion.TrySetException(error);
                    }
                }
            }
        }
        catch (Exception error)
        {
            // 写连接建不起来或读循环崩溃：把已排队的和之后入队的都标记失败，避免调用方永远等待。
            _channel.Writer.TryComplete(error);
            while (_channel.Reader.TryRead(out var item))
            {
                item.Completion.TrySetException(error);
            }
        }
        finally
        {
            connection?.Dispose();
        }
    }

    /// <summary>停止接收新写入并等队列排空（最多 5 秒）；超时不阻塞退出。</summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // 工作循环自身出错时已在 WorkerAsync 内把等待方标记失败，这里只等退出。
        }
    }
}
