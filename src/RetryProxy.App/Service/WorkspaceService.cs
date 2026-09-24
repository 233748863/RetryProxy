using Microsoft.Extensions.Logging;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Workspace;
using RetryProxy.Helpers.Win32;
using RetryProxy.Service.Interface;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RetryProxy.Service;

/// <summary>
/// 把 Core 的 <see cref="ProxyWorkspace"/> 接到 WPF：后台通知合并成 40 ms 一次的界面刷新、
/// 日志队列进内存缓冲、提示走 Snackbar + 任务栏闪烁、保活倒计时每秒一次。
/// </summary>
public sealed class WorkspaceService
{
    /// <summary>后台通知合并间隔。</summary>
    public static readonly TimeSpan NotifyRepaintDelay = TimeSpan.FromMilliseconds(40);

    private readonly ILogger<WorkspaceService> _logger;
    private readonly IConfigService _configService;
    private readonly ISnackbarService _snackbar;
    private readonly ProxyLogger _proxyLogger;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _hintTimer;
    private readonly HashSet<object> _hintSubscribers = new();
    private int _refreshQueued;
    private bool _started;

    public WorkspaceService(ILogger<WorkspaceService> logger, IConfigService configService, ProxyLogger proxyLogger, ISnackbarService snackbar)
    {
        _logger = logger;
        _configService = configService;
        _proxyLogger = proxyLogger;
        _snackbar = snackbar;
        var all = configService.Get();
        Workspace = new ProxyWorkspace(proxyLogger, all.Proxy ?? ProxyConfig.Builtin(), config =>
        {
            all.Proxy = config;
            configService.Save();
        });
        Workspace.NoticePosted += OnNoticePosted;
        Preparations = new PreparationWorkspace(proxyLogger);
        Preparations.NoticePosted += ShowNotice;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = NotifyRepaintDelay };
        _refreshTimer.Tick += (_, _) => Flush();
        _hintTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _hintTimer.Tick += (_, _) => Tick?.Invoke();
        Workspace.SetUiNotifier(RequestRefresh);
        Preparations.SetUiNotifier(RequestRefresh);
        Workspace.RefreshServices();
    }

    public ProxyWorkspace Workspace { get; }

    public PreparationWorkspace Preparations { get; }

    /// <summary>界面线程上的日志缓冲。</summary>
    public LogBuffer Logs { get; } = new();

    /// <summary>通道状态、统计、保活或配置有变化（界面线程）。</summary>
    public event Action? Refreshed;

    /// <summary>日志缓冲有新行或被清空（界面线程）。</summary>
    public event Action? LogsChanged;

    /// <summary>保活提示需要按秒刷新时每秒一次（界面线程）。</summary>
    public event Action? Tick;

    /// <summary>窗口显示后调用：开始消费日志队列并拉起上次运行的通道。</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = Task.Run(ConsumeLogsAsync);
        Workspace.StartDesiredRoutes();
        Flush();
    }

    public void Shutdown()
    {
        Preparations.Shutdown();
        Workspace.Shutdown();
        _refreshTimer.Stop();
        _hintTimer.Stop();
    }

    /// <summary>页面在需要保活倒计时时登记自己；没有订阅者或提示不再随时间变化时停掉定时器。</summary>
    public void SetHintTimerWanted(object subscriber, bool wanted)
    {
        if (wanted)
        {
            _hintSubscribers.Add(subscriber);
        }
        else
        {
            _hintSubscribers.Remove(subscriber);
        }

        UpdateHintTimer();
    }

    private void UpdateHintTimer()
    {
        var wanted = _hintSubscribers.Count > 0
            && (Workspace.KeepAliveHintChangesOverTime() || Preparations.HintChangesOverTime());
        if (wanted && !_hintTimer.IsEnabled)
        {
            _hintTimer.Start();
        }
        else if (!wanted && _hintTimer.IsEnabled)
        {
            _hintTimer.Stop();
        }
    }

    /// <summary>后台线程调用：合并后在界面线程刷新。</summary>
    public void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }
        });
    }

    /// <summary>立即在界面线程做一次同步：拉取服务错误、待提交的准备、准备结果，再通知页面。</summary>
    public void Flush()
    {
        _refreshTimer.Stop();
        Interlocked.Exchange(ref _refreshQueued, 0);
        try
        {
            Workspace.PollServiceErrors();
            Preparations.Poll();
            while (Workspace.PollPreparationEvents())
            {
            }
        }
        catch (Exception error)
        {
            _logger.LogError(error, "刷新代理状态失败");
        }

        Refreshed?.Invoke();
        UpdateHintTimer();
    }

    public void ClearLogs()
    {
        Logs.Clear();
        LogsChanged?.Invoke();
    }

    private async Task ConsumeLogsAsync()
    {
        var reader = _proxyLogger.UiLines;
        if (reader is null)
        {
            return;
        }

        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new List<string>();
                while (reader.TryRead(out var line))
                {
                    batch.Add(line);
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted)
                {
                    return;
                }

                await dispatcher.InvokeAsync(() =>
                {
                    foreach (var line in batch)
                    {
                        Logs.Push(line);
                    }

                    LogsChanged?.Invoke();
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogError(error, "日志队列消费中断");
        }
    }

    private void OnNoticePosted(string message)
    {
        // 提示已交给 Snackbar，清掉后下一条不会拼接在后面（对应 Rust 版模态框关掉后再显示下一条）。
        Workspace.Notice = null;
        ShowNotice(message);
    }

    private void ShowNotice(string message)
    {
        _logger.LogInformation("提示：{Notice}", message);
        try
        {
            _snackbar.Show(
                "提示",
                message,
                ControlAppearance.Secondary,
                new SymbolIcon(SymbolRegular.Info24),
                TimeSpan.FromSeconds(6));
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Snackbar 显示失败");
        }

        WindowFlash.Flash(Application.Current?.MainWindow);
    }
}
