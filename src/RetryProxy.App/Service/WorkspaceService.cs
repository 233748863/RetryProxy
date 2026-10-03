using Microsoft.Extensions.Logging;
using RetryProxy.Core.Balance;
using RetryProxy.Core.Config;
using RetryProxy.Core.Diagnostics;
using RetryProxy.Core.Client;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Workspace;
using RetryProxy.Helpers.Win32;
using RetryProxy.Service.Interface;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly DispatcherTimer _balanceTimer;
    private readonly BalanceFetcher _balanceFetcher;
    private ProxyConfig? _balanceConfig;
    private DateTimeOffset? _nextBalanceRefreshAt;
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
        Diagnostics = new DiagnosticStore(Path.Combine(proxyLogger.DirectoryPath, "request-diagnostics"));
        Workspace = new ProxyWorkspace(proxyLogger, all.Proxy ?? ProxyConfig.Builtin(), SaveProxyConfig, Diagnostics);
        Workspace.BeforeDestructiveChange = configService.BackupBeforeDeletion;
        Workspace.NoticePosted += OnNoticePosted;
        I18n.I18nService.Instance.PropertyChanged += (_, _) => RequestRefresh();
        Preparations = new PreparationWorkspace(proxyLogger, all.Preparations, preparations =>
        {
            var previous = all.Preparations;
            try
            {
                all.Preparations = preparations;
                configService.SaveChecked();
            }
            catch
            {
                all.Preparations = previous;
                throw;
            }
        }, targetResolver: (client, key) => PreparationCatalog.Resolve(Workspace.Config, client, key));
        PreparationManagement = new PreparationCatalog(() => Workspace.Config, Preparations, (proxy, preparations) =>
        {
            var previousProxy = all.Proxy;
            var previousPreparations = all.Preparations;
            try
            {
                all.Proxy = proxy;
                all.Preparations = preparations;
                configService.SaveChecked();
            }
            catch
            {
                all.Proxy = previousProxy;
                all.Preparations = previousPreparations;
                throw;
            }
        });
        Preparations.NoticePosted += ShowNotice;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = NotifyRepaintDelay };
        _refreshTimer.Tick += (_, _) => Flush();
        _hintTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _hintTimer.Tick += (_, _) => Tick?.Invoke();
        _balanceFetcher = new BalanceFetcher(Global.Version);
        Balances = new BalanceWorkspace((request, cancellation) => _balanceFetcher.FetchAsync(
            request.BaseUrl, request.ApiKey, request.Query, cancellation), Workspace.RememberBalanceDetection);
        Balances.SetUiNotifier(RequestRefresh);
        SynchronizeBalances();
        _balanceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = BalanceWorkspace.BackgroundRefreshAge };
        _balanceTimer.Tick += (_, _) => RefreshBackgroundBalances();
        Workspace.SetUiNotifier(RequestRefresh);
        Preparations.SetUiNotifier(RequestRefresh);
        Workspace.RefreshServices();
        Clients = new ClientTakeoverCoordinator(Workspace, (client, state) => new ClientConfigStore(
            Global.Absolute("User/backup/client"),
            client == ClientType.Claude ? new ClaudeConfigEditor() : new CodexConfigEditor(),
            state.Enabled && state.ConfigPath.Length > 0 ? state.ConfigPath : ClientConfigPaths.Resolve(client)),
            configService.BackupBeforeClientTakeover);
    }

    public ClientTakeoverCoordinator Clients { get; }

    public ProxyWorkspace Workspace { get; }

    public PreparationWorkspace Preparations { get; }
    public PreparationCatalog PreparationManagement { get; }
    public BalanceWorkspace Balances { get; }
    public IDiagnosticRepository Diagnostics { get; }

    private void SynchronizeBalances()
    {
        if (ReferenceEquals(_balanceConfig, Workspace.Config)) return;
        _balanceConfig = Workspace.Config;
        Balances.Synchronize(_balanceConfig);
    }

    public void RefreshClientBalances(ClientType client)
    {
        SynchronizeBalances();
        Balances.RefreshClient(client);
    }

    public void RefreshProviderBalance(string providerId)
    {
        SynchronizeBalances();
        Balances.RefreshProvider(providerId);
    }

    private void RefreshBackgroundBalances()
    {
        if (!_started) return;
        SynchronizeBalances();
        var current = Workspace.Config.Routes.Select(route => new BalanceKey(route.CurrentProviderId, route.CurrentKeyId));
        var ready = Preparations.Tasks.Where(task => task.IsReady && task.Mode != PrepareMode.CustomProvider)
            .Select(task => new BalanceKey(task.ProviderId, task.KeyId));
        var keys = current.Concat(ready).Distinct().ToArray();
        Balances.RefreshBackground(keys);
        // 按最早到期项唤醒，不做固定轮询。例如第 25 分钟手动刷新后，下次在第 55 分钟查询。
        var next = Balances.NextBackgroundRefreshAt(keys);
        if (next == _nextBalanceRefreshAt && _balanceTimer.IsEnabled) return;
        _balanceTimer.Stop();
        _nextBalanceRefreshAt = next;
        if (next is null) return;
        var delay = next.Value - DateTimeOffset.UtcNow;
        _balanceTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
        _balanceTimer.Start();
    }

    private void SaveProxyConfig(ProxyConfig config) => PreparationManagement.SaveProxy(config);

    public void PrepareKeys(ClientType client, IEnumerable<PreparationKeyRef> keys)
    {
        var error = Preparations.PrepareKeys(client, keys, out var started);
        if (error is not null) ShowNotice(error);
        else if (started > 0) ShowPreparationStarted(started);
        else ShowNotice("所选 Key 已在准备或保活中");
        Flush();
    }

    public void ShowPreparationStarted(int count) => ShowNotice(string.Format(
        I18n.I18nService.Instance.Translate("已开始准备 {0} 个 Key"), count));

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
        Preparations.ResumeRunning();
        Clients.Detect();
        Flush();
    }

    public void Shutdown()
    {
        _started = false;
        _balanceTimer.Stop();
        Balances.Dispose();
        _balanceFetcher.Dispose();
        Clients.Shutdown();
        PreparationManagement.Shutdown(Workspace);
        Diagnostics.Dispose();
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
            Clients.Refresh();
            Preparations.Poll();
            // 先撤销旧地址/密钥的查询，再接纳结果；识别回写配置后同步下一轮请求快照。
            SynchronizeBalances();
            Balances.Poll();
            SynchronizeBalances();
            RefreshBackgroundBalances();
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
                I18n.I18nService.Instance.Translate("提示"),
                View.Drawers.DrawerText.Error(message),
                ControlAppearance.Secondary,
                new SymbolIcon(SymbolRegular.Info24),
                TimeSpan.FromSeconds(5));
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Snackbar 显示失败");
        }

        WindowFlash.Flash(Application.Current?.MainWindow);
    }
}
