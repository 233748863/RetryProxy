using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Service;

namespace RetryProxy.Core.Workspace;

/// <summary>
/// 独立准备功能：自行管理任务、配置、临时代理及保活会话；不读取或修改代理通道配置。
/// 和 ProxyWorkspace 一样只在界面线程访问，后台变化通过通知请求刷新。
/// </summary>
public sealed class PreparationWorkspace
{
    private readonly ProxyLogger _logger;
    private readonly Dictionary<string, PreparationTask> _tasks = new();
    private Action? _uiNotifier;
    private int _nextNumber;

    public PreparationWorkspace(ProxyLogger logger) => _logger = logger;

    internal CliCommand? TestCliCommand { get; set; }
    internal Func<ClientType, CliCredential> LocalProviderResolver { get; set; } = LocalProviderCredentials.Read;

    public IReadOnlyCollection<PreparationTask> Tasks => _tasks.Values;
    public event Action<string>? NoticePosted;

    public void SetUiNotifier(Action? notifier)
    {
        _uiNotifier = notifier;
        foreach (var task in _tasks.Values)
        {
            task.Service?.SetUiNotifier(notifier);
        }
    }

    public PreparationDialogState OpenPrepareDialog(string? taskId = null) =>
        taskId is not null && _tasks.TryGetValue(taskId, out var task)
            ? task.Options.Copy()
            : new PreparationDialogState();

    public PreparationTask? Find(string taskId) => _tasks.GetValueOrDefault(taskId);

    public CliCredential ResolveCredential(PreparationDialogState options)
    {
        if (!Enum.IsDefined(options.ClientType) || !Enum.IsDefined(options.Mode))
        {
            throw new WorkspaceException("请选择准备客户端和供应商来源");
        }
        try
        {
            if (options.Mode == PrepareMode.LocalProvider)
            {
                return LocalProviderResolver(options.ClientType);
            }
            var provider = new ProviderEndpoint("独立准备", options.ProviderUrl);
            provider.Validate();
            return CliCredential.Create(options.ApiKey, provider.BaseUrl);
        }
        catch (ConfigException error)
        {
            throw new WorkspaceException(error.Message);
        }
        catch (CliException error)
        {
            throw new WorkspaceException(error.Message);
        }
    }

    public string PlanText(PreparationDialogState options) => options.Mode == PrepareMode.LocalProvider
        ? $"开始时读取 {options.ClientType.Label()} 当前供应商的地址与密钥，运行中的任务沿用开始时的配置。"
        : $"使用 {options.ClientType.Label()} 为填写的供应商准备，地址、密钥与会话仅在本次运行有效。";

    internal static ProxyConfig CreateRuntimeConfig(string baseUrl, int port, double idleMinutes, ClientType clientType) => new()
    {
        ClientType = clientType,
        ListenPort = port,
        MaxRetries = 0,
        KeepaliveEnabled = false,
        KeepaliveIdleMinutes = idleMinutes,
        KeepaliveContextLimit = (long)KeepAliveWatchdog.DefaultContextLimit,
        UpstreamBaseUrl = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^3] : baseUrl,
    };

    public bool SubmitPrepareDialog(PreparationDialogState dialog)
    {
        if (_tasks.TryGetValue(dialog.TaskId, out var task) && !task.CanStart)
        {
            dialog.Error = "请先停止这项准备，再修改设置";
            return false;
        }
        var options = dialog.Copy();
        var idle = UiText.ParseIdleMinutes(options.IdleMinutes);
        if (idle is null || idle < ConfigDefaults.MinKeepaliveIdleMinutes || idle > ConfigDefaults.MaxKeepaliveIdleMinutes)
        {
            dialog.Error = "独立保活间隔请输入 0.5～1440 分钟";
            return false;
        }
        if (string.IsNullOrWhiteSpace(options.SelectedModel))
        {
            dialog.Error = "请输入模型名称，或获取模型后选择一个用于准备";
            return false;
        }
        options.SelectedModel = options.SelectedModel.Trim();

        ProxyConfig runtime;
        CliCredential upstream;
        CliCredential credential;
        try
        {
            upstream = ResolveCredential(options);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            runtime = CreateRuntimeConfig(upstream.BaseUrl, port, idle.Value, options.ClientType);
            var address = new Uri(upstream.BaseUrl);
            if (address.IsLoopback && (address.Port == port || _tasks.Values.Any(item => !item.CanStart && item.ListenPort == address.Port)))
            {
                throw new WorkspaceException("供应商地址指向独立准备服务，请填写实际供应商地址");
            }
            runtime.Validate(true);
            credential = CliCredential.Create(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), runtime.LocalUrl, options.SelectedModel);
        }
        catch (Exception error) when (error is WorkspaceException or ConfigException or CliException or SocketException)
        {
            dialog.Error = error is SocketException ? "无法分配后台准备端口，请稍后重试" : error.Message;
            return false;
        }

        options.IdleMinutes = UiText.TrimFloat(idle.Value);
        options.ProviderUrl = options.Mode == PrepareMode.CustomProvider ? upstream.BaseUrl : string.Empty;
        options.ApiKey = options.Mode == PrepareMode.CustomProvider ? upstream.ApiKey : string.Empty;
        var watchdog = TestCliCommand is { } command
            ? KeepAliveWatchdog.WithCliCommand(false, TimeSpan.FromMinutes(idle.Value), command.Clone())
            : new KeepAliveWatchdog(false, TimeSpan.FromMinutes(idle.Value));
        watchdog.RequireContextForPreparation();
        watchdog.EnableAfterPreparation();
        var number = task?.Number ?? _nextNumber + 1;
        var marker = $"准备 {number}";
        var service = new ProxyService(_logger, marker)
            .WithKeepAliveWatchdog(watchdog)
            .WithLogLabel(() => $"{(watchdog.Snapshot().Preparing || !watchdog.Enabled ? "准备" : "保活")}][{marker}")
            .WithUpstreamApiKey(upstream.ApiKey, credential.ApiKey);
        service.SetUiNotifier(_uiNotifier);
        service.RequestStart(runtime);
        if (task is null)
        {
            task = new PreparationTask(options, ++_nextNumber);
            _tasks.Add(task.Id, task);
        }
        task.Options = options;
        task.Service = service;
        task.Credential = credential;
        task.ProviderUrl = upstream.BaseUrl;
        task.ListenPort = runtime.ListenPort;
        task.Failure = null;
        task.Pending = true;
        dialog.Error = null;
        _logger.Info($"[准备][{marker}] 已提交独立准备 · {options.ClientType.Label()} · 模型 {options.SelectedModel}");
        Poll();
        _uiNotifier?.Invoke();
        return true;
    }

    public void Start(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
        {
            return;
        }
        var options = task.Options.Copy();
        if (!SubmitPrepareDialog(options))
        {
            NoticePosted?.Invoke(options.Error!);
        }
    }

    public void Stop(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task) || !task.CanStop)
        {
            return;
        }
        task.Pending = false;
        task.Service!.KeepAlive.CancelPreparation();
        task.Service.ConfigureKeepAlive(false, task.Service.KeepAlive.Idle);
        task.Service.RequestStop();
        task.Credential = null;
        _logger.Info($"[准备][准备 {task.Number}] 准备已终止，独立保活已停止");
        _uiNotifier?.Invoke();
    }

    public void Remove(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task) && task.CanStart)
        {
            task.Service?.SetUiNotifier(null);
            _tasks.Remove(taskId);
            _uiNotifier?.Invoke();
        }
    }

    public void Poll()
    {
        foreach (var task in _tasks.Values.ToList())
        {
            var service = task.Service;
            if (service is null)
            {
                continue;
            }
            if (task.Pending)
            {
                if (service.State == ServiceState.Starting)
                {
                    continue;
                }
                task.Pending = false;
                try
                {
                    if (service.State != ServiceState.Running || !service.RequestPreparationWith(task.Credential))
                    {
                        Fail(task, service.StartupError ?? "后台准备服务未能启动");
                    }
                }
                catch (InvalidOperationException)
                {
                    Fail(task, service.StartupError ?? "后台准备服务已停止");
                }
            }
            if (service.State == ServiceState.Error && task.Failure is null)
            {
                Fail(task, service.StartupError ?? "后台准备服务发生异常");
            }
            var result = service.KeepAlive.TakePreparationResult();
            if (result is null)
            {
                continue;
            }
            var message = result switch
            {
                PreparationResult.Ready => $"{task.Title}：准备完成，已开始独立保活",
                PreparationResult.Failed failed => $"{task.Title}：准备未完成，{failed.Reason}",
                _ => $"{task.Title}：准备已终止",
            };
            _logger.Info($"[准备] {message}");
            NoticePosted?.Invoke(message);
        }
    }

    private void Fail(PreparationTask task, string reason)
    {
        task.Pending = false;
        task.Failure = reason;
        task.Credential = null;
        task.Service?.RequestStop();
        var message = $"{task.Title}：{reason}";
        _logger.Warn($"[准备] {message}");
        NoticePosted?.Invoke(message);
    }

    public bool HintChangesOverTime() => _tasks.Values.Any(task => task.CanStop);

    public void Shutdown()
    {
        foreach (var task in _tasks.Values)
        {
            task.Service?.RequestStop();
        }
        foreach (var task in _tasks.Values)
        {
            task.Service?.Stop(TimeSpan.FromSeconds(15));
            task.Service?.SetUiNotifier(null);
        }
        _tasks.Clear();
    }
}
