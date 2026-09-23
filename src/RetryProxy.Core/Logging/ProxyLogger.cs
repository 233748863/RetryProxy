using System;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace RetryProxy.Core.Logging;

/// <summary>
/// 文件日志器（对应 Rust logging.rs 的 Logger）：
/// <c>logs\retry-proxy.log</c>，行格式 <c>YYYY-MM-DD HH:MM:SS LEVEL message</c>，
/// 5 MiB 轮转 × 3；可选的界面队列有界 10000 行，满则丢弃。
/// </summary>
public sealed class ProxyLogger : IDisposable
{
    public const string FileName = "retry-proxy.log";
    public const long MaxLogBytes = 5 * 1024 * 1024;
    public const int BackupCount = 3;
    public const int QueueCapacity = 10_000;

    private readonly object _lock = new();
    private readonly string _path;
    private readonly Channel<string>? _uiQueue;
    private FileStream? _file;
    private Action? _notifier;
    private bool _disposed;

    private ProxyLogger(string directory, bool withUiQueue)
    {
        System.IO.Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, FileName);
        _file = OpenAppend(_path);
        if (withUiQueue)
        {
            // FullMode.Wait 下 TryWrite 在队列满时返回 false 且不阻塞，正好对应 Rust 的 try_send。
            _uiQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        }
    }

    /// <summary>
    /// 创建带界面队列的日志器。
    /// </summary>
    public static ProxyLogger Create(string directory) => new(directory, withUiQueue: true);

    /// <summary>
    /// 创建只写文件、不向界面投递的日志器（CLI / 测试）。
    /// </summary>
    public static ProxyLogger Silent(string directory) => new(directory, withUiQueue: false);

    public string DirectoryPath => Path.GetDirectoryName(_path)!;

    public string FilePath => _path;

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

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARNING", message);

    public void Error(string message) => Write("ERROR", message);

    public RouteLogger Route(string routeName, Func<string>? logLabel = null) => new(this, routeName, logLabel);

    private void Write(string level, string message)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        Action? notifier = null;
        var queued = false;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_file is not null && _file.Length + bytes.Length > MaxLogBytes)
                {
                    _file.Dispose();
                    _file = null;
                    RotateFiles(_path);
                    _file = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                }

                _file ??= OpenAppend(_path);
                _file.Write(bytes, 0, bytes.Length);
                _file.Flush();
            }
            catch (IOException)
            {
                // 与 Rust 版一致：写文件失败不影响主流程。
            }
            catch (UnauthorizedAccessException)
            {
            }

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

    private static FileStream OpenAppend(string path)
    {
        return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    private static void RotateFiles(string path)
    {
        for (var index = BackupCount; index >= 1; index--)
        {
            var source = index == 1 ? path : BackupPath(path, index - 1);
            var destination = BackupPath(path, index);
            try
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                if (File.Exists(source))
                {
                    File.Move(source, destination);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static string BackupPath(string path, int index) => $"{path}.{index}";

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _file?.Dispose();
            _file = null;
            _uiQueue?.Writer.TryComplete();
        }
    }
}
