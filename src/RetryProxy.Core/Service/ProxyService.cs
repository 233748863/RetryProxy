using System;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Proxy;
using RetryProxyPipeline = RetryProxy.Core.Proxy.RetryProxy;

namespace RetryProxy.Core.Service;

public enum ServiceState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error,
}

/// <summary>
/// 一条通道的生命周期管理（对应 service.rs 的 ProxyService）：非阻塞启停、状态机、界面通知。
/// </summary>
public sealed class ProxyService
{
    /// <summary>空闲保活的轮询间隔。只是决定「最多晚这么久发现空闲」，不影响阈值本身。</summary>
    public static readonly TimeSpan KeepAlivePollInterval = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly ProxyLogger _logger;
    private ServiceState _state = ServiceState.Stopped;
    private ProxyConfig? _runtimeConfig;
    private string? _startupError;
    private CancellationTokenSource? _cancel;
    private TaskCompletionSource<bool>? _stopSignal;
    private Task? _task;
    private Action? _notifier;
    private string? _upstreamApiKey;
    private string? _localAccessKey;
    private Func<string>? _logLabel;

    public ProxyService(ProxyLogger logger, string routeName)
        : this(logger, routeName, new ProxyMetrics())
    {
    }

    private ProxyService(ProxyLogger logger, string routeName, ProxyMetrics metrics)
    {
        _logger = logger;
        RouteName = routeName.Trim();
        Metrics = metrics;
        KeepAlive = new KeepAliveWatchdog(false, TimeSpan.FromSeconds(ConfigDefaults.KeepaliveIdleMinutes * 60.0));
    }

    /// <summary>从日志目录恢复当日统计后创建服务；恢复结果写进该通道的日志。</summary>
    public static ProxyService WithDailyStatistics(ProxyLogger logger, string routeId, string routeName)
    {
        var metrics = ProxyMetrics.FromDailyLogs(logger.DirectoryPath, routeId, routeName);
        var snapshot = metrics.Snapshot();
        var routeLogger = logger.Route(routeName);
        if (snapshot.StatisticsWarning is { } warning)
        {
            routeLogger.Warn(warning);
        }
        else if (snapshot.TotalRequests > 0)
        {
            routeLogger.Info(
                $"已恢复 {snapshot.StatisticsDate} 当日统计：请求 {snapshot.TotalRequests}，成功 {snapshot.SuccessfulRequests}，失败 {snapshot.FailedRequests}，重试 {snapshot.RetryCount}，历史未完成 {snapshot.HistoricalUnfinishedRequests}");
        }

        return new ProxyService(logger, routeName, metrics);
    }

    /// <summary>沿用已有统计实例新建服务（通道改名或换看门狗后替换服务时使用）。</summary>
    public static ProxyService WithMetrics(ProxyLogger logger, string routeName, ProxyMetrics metrics)
    {
        return new ProxyService(logger, routeName, metrics);
    }

    public string RouteName { get; }

    public ProxyMetrics Metrics { get; }

    public KeepAliveWatchdog KeepAlive { get; private set; }

    public ProxyService WithLogLabel(Func<string> logLabel)
    {
        _logLabel = logLabel;
        return this;
    }

    /// <summary>通道状态、请求统计和保活状态变化时都通知界面；替换指标或看门狗后需重新调用。</summary>
    public void SetUiNotifier(Action? notifier)
    {
        lock (_lock)
        {
            _notifier = notifier;
        }

        Metrics.SetUiNotifier(notifier);
        KeepAlive.SetUiNotifier(notifier);
    }

    public ServiceState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public string? StartupError
    {
        get
        {
            lock (_lock)
            {
                return _startupError;
            }
        }
    }

    public bool IsRunning => State == ServiceState.Running;

    public ProxyService WithKeepAliveWatchdog(KeepAliveWatchdog watchdog)
    {
        KeepAlive = watchdog;
        return this;
    }

    public ProxyService WithUpstreamApiKey(string apiKey, string localAccessKey)
    {
        _upstreamApiKey = apiKey;
        _localAccessKey = localAccessKey;
        return this;
    }

    public void ConfigureKeepAlive(bool enabled, TimeSpan idle) => KeepAlive.Configure(enabled, idle);

    /// <summary>按通道当前配置准备；通道未运行时抛 <see cref="InvalidOperationException"/>。</summary>
    public bool RequestPreparation()
    {
        EnsureRunning();
        return KeepAlive.RequestPreparation();
    }

    /// <summary><paramref name="credential"/> 非 null 时切换到该 Key，null 切回本机默认配置；之后的自动保活也随之切换。</summary>
    public bool RequestPreparationWith(Core.Cli.CliCredential? credential)
    {
        EnsureRunning();
        return KeepAlive.RequestPreparationWith(credential);
    }

    private void EnsureRunning()
    {
        lock (_lock)
        {
            if (_state != ServiceState.Running || (_cancel?.IsCancellationRequested ?? false))
            {
                throw new InvalidOperationException("通道未运行，请先启用本通道");
            }
        }
    }

    public TimeSpan? KeepAliveIdleFor() => IsRunning ? KeepAlive.IdleFor() : null;

    public bool KeepAliveHasTemplate => KeepAlive.Template() is not null;

    /// <summary>非阻塞地启动通道；实际绑定监听端口在后台完成。已在运行则返回 false。</summary>
    public bool RequestStart(ProxyConfig config)
    {
        config.Validate(true);
        lock (_lock)
        {
            if (_state is not (ServiceState.Stopped or ServiceState.Error))
            {
                return false;
            }

            _state = ServiceState.Starting;
            _runtimeConfig = config.Clone();
            _startupError = null;
            KeepAlive.Configure(config.KeepaliveEnabled, TimeSpan.FromSeconds(config.KeepaliveIdleMinutes * 60.0));
            KeepAlive.SetContextLimit((ulong)Math.Max(config.KeepaliveContextLimit, 1));
            var flavor = KeepAliveFlavorExtensions.FromClientType(config.ClientType);
            KeepAlive.ConfigureFlavor(flavor);
            KeepAlive.SetReasoningEffort(config.KeepaliveReasoningEffort);
            var stopSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopSignal = stopSignal;
            var runtime = _runtimeConfig;
            _task = Task.Run(() => RunServiceAsync(runtime, stopSignal.Task, flavor));
            return true;
        }
    }

    /// <summary>同步启动，主要供自动化测试使用；界面应调用 <see cref="RequestStart"/>。</summary>
    public void Start(ProxyConfig config, TimeSpan timeout)
    {
        if (!RequestStart(config))
        {
            return;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            switch (State)
            {
                case ServiceState.Running:
                    return;
                case ServiceState.Error:
                    throw new ConfigException(StartupError ?? "代理服务启动失败");
            }

            if (DateTime.UtcNow >= deadline)
            {
                RequestStop();
                throw new ConfigException("代理服务启动超时");
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>非阻塞请求停止。取消令牌会同时打断上游请求和退避等待。</summary>
    public void RequestStop()
    {
        lock (_lock)
        {
            if (_state is ServiceState.Stopped or ServiceState.Error or ServiceState.Stopping)
            {
                return;
            }

            _state = ServiceState.Stopping;
            _cancel?.Cancel();
            _stopSignal?.TrySetResult(true);
            _stopSignal = null;
        }

        Notify();
    }

    public void Stop(TimeSpan timeout)
    {
        RequestStop();
        Task? task;
        lock (_lock)
        {
            task = _task;
            _task = null;
        }

        if (task is null)
        {
            return;
        }

        if (task.Wait(timeout))
        {
            return;
        }

        lock (_lock)
        {
            _state = ServiceState.Error;
            _startupError = $"代理服务未能在 {timeout.TotalSeconds:F0} 秒内停止";
        }

        Notify();
    }

    private async Task RunServiceAsync(ProxyConfig config, Task stopSignal, KeepAliveFlavor flavor)
    {
        var cancel = new CancellationTokenSource();
        lock (_lock)
        {
            _cancel = cancel;
        }

        var logger = _logger.Route(RouteName, _logLabel);
        RetryProxyPipeline proxy;
        try
        {
            proxy = new RetryProxyPipeline(config, _logger, Metrics, cancel.Token)
                .WithRouteLogger(logger)
                .WithKeepAliveWatchdog(KeepAlive)
                .WithUpstreamApiKey(_upstreamApiKey, _localAccessKey);
        }
        catch (ConfigException error)
        {
            SetError(error.Message);
            return;
        }

        ProxyHost host;
        try
        {
            host = await ProxyHost.StartAsync(proxy, config.ListenPort).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            SetError($"无法监听 {config.LocalUrl}：{error.Message}");
            return;
        }

        ulong discarded = 0;
        using (KeepAlive.RegisterService(flavor))
        {
            SetState(ServiceState.Running);
            logger.Info($"代理服务已启动：{config.LocalUrl}（上游请求跟随系统代理）");
            var keepAliveTask = KeepAlivePollLoopAsync(proxy, cancel.Token);
            // 停用通道要立刻放弃在处理中的请求：先读「处理中」，再取消，再硬停 Kestrel。
            await Task.WhenAny(stopSignal, Task.Delay(Timeout.Infinite, cancel.Token)).ConfigureAwait(false);
            discarded = Metrics.Snapshot().ActiveRequests;
            cancel.Cancel();
            await host.StopAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            await host.DisposeAsync().ConfigureAwait(false);
            try
            {
                await keepAliveTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        SetStateIfNotError(ServiceState.Stopped);
        logger.Info(discarded > 0 ? $"代理服务已停止，丢弃 {discarded} 个处理中的请求" : "代理服务已停止");
        cancel.Dispose();
    }

    private static async Task KeepAlivePollLoopAsync(RetryProxyPipeline proxy, CancellationToken cancel)
    {
        Task? wake = null;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                wake ??= proxy.KeepAlive.PreparationRequestedAsync(cancel);
                var completed = await Task.WhenAny(Task.Delay(KeepAlivePollInterval, cancel), wake).ConfigureAwait(false);
                if (completed == wake)
                {
                    wake = null;
                }

                if (cancel.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await proxy.SendDueKeepAliveProbeAsync().ConfigureAwait(false);
        }
    }

    private void SetState(ServiceState state)
    {
        lock (_lock)
        {
            _state = state;
        }

        Notify();
    }

    private void SetStateIfNotError(ServiceState state)
    {
        lock (_lock)
        {
            if (_state != ServiceState.Error)
            {
                _state = state;
            }

            _cancel = null;
        }

        Notify();
    }

    private void SetError(string error)
    {
        lock (_lock)
        {
            _state = ServiceState.Error;
            _startupError = error;
            _cancel = null;
        }

        Notify();
    }

    private void Notify()
    {
        Action? notifier;
        lock (_lock)
        {
            notifier = _notifier;
        }

        notifier?.Invoke();
    }

    /// <summary>仅供测试：不启动线程直接改状态。</summary>
    internal void ForceStateForTest(ServiceState state) => SetState(state);

    internal void SetStateIfNotErrorForTest(ServiceState state) => SetStateIfNotError(state);

    internal void SetErrorForTest(string error) => SetError(error);
}
