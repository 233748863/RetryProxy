using System;
using System.IO;
using System.Globalization;
using System.Threading.Channels;

namespace RetryProxy.Core.Logging;

/// <summary>运行日志入口：实时投递界面，后台保存到按日 SQLite 文件。</summary>
public sealed class ProxyLogger : IDisposable
{
    public const int QueueCapacity = 10_000;
    private readonly object _lock = new();
    private readonly Channel<string>? _uiQueue;
    public RuntimeLogStore Store { get; }
    private Action? _notifier;
    private bool _disposed;

    private ProxyLogger(string directory, bool withUiQueue, TimeProvider? time, RuntimeLogOptions? options)
    {
        Store = new RuntimeLogStore(directory, time, options);
        if (withUiQueue)
            _uiQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false,
            });
    }

    public static ProxyLogger Create(string directory, TimeProvider? time = null, RuntimeLogOptions? options = null)
        => new(directory, true, time, options);
    public static ProxyLogger Silent(string directory, TimeProvider? time = null, RuntimeLogOptions? options = null)
        => new(directory, false, time, options);
    public string DirectoryPath => Store.LogDirectory;
    public string FilePath => Store.CurrentPath;
    internal DateOnly Today => Store.Today;

    /// <summary>
    /// 界面消费端；Silent 日志器为 null。
    /// </summary>
    public ChannelReader<string>? UiLines => _uiQueue?.Reader;

    /// <summary>
    /// 每有一行进入界面队列就调用一次，用于触发界面刷新。
    /// </summary>
    public void SetUiNotifier(Action? notifier)
    {
        lock (_lock)
        {
            _notifier = notifier;
        }
    }

    public void Info(string message) => Write("INFO", $"[系统] {message}");

    public void Warn(string message) => Write("WARNING", $"[系统] {message}");

    public void Error(string message) => Write("ERROR", $"[系统] {message}");

    public RouteLogger Route(string routeName) => new(this, routeName, LogSource.ChannelProxy);

    public RouteLogger Preparation(string taskName) => new(this, taskName, LogSource.Preparation);

    internal void Write(string level, string message, LogSource source = LogSource.System, string routeName = "", string? requestId = null, DateOnly? requestDate = null)
    {
        var now = Store.Now;
        var text = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + $" {level} {message}";
        Action? notifier = null;
        var queued = false;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            Store.Append(new RuntimeLogEntry(0, DateOnly.FromDateTime(now.DateTime), now.ToUnixTimeMilliseconds(),
                level, source, routeName, requestId, requestDate, text));

            if (_uiQueue is not null && _uiQueue.Writer.TryWrite(text))
            {
                queued = true;
                notifier = _notifier;
            }
        }

        if (queued)
        {
            notifier?.Invoke();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _uiQueue?.Writer.TryComplete();
        }
        Store.Dispose();
    }
}
