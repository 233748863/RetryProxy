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
/// 任务设置经保存回调写入配置文件，服务与会话只在本次运行有效。
/// 和 ProxyWorkspace 一样只在界面线程访问，后台变化通过通知请求刷新。
/// </summary>
public sealed class PreparationWorkspace
{
    private readonly ProxyLogger _logger;
    private readonly Action<List<SavedPreparation>>? _save;
    private readonly Dictionary<string, PreparationTask> _tasks = new();
    private Action? _uiNotifier;
    private int _nextNumber;

    public PreparationWorkspace(ProxyLogger logger, IEnumerable<SavedPreparation?>? saved = null, Action<List<SavedPreparation>>? save = null)
    {
        _logger = logger;
        _save = save;
        Restore(saved ?? []);
    }

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
        : $"使用 {options.ClientType.Label()} 为填写的供应商准备，地址与密钥随任务保存。";

    /// <summary>后台临时代理的运行时配置；供应商地址原样保存，按客户端的地址规则拼接（PRD-供应商管理 §6.1）。</summary>
    internal static ProxyConfig CreateRuntimeConfig(string baseUrl, int port, double idleMinutes, ClientType clientType,
        ReasoningEffort reasoningEffort = ReasoningEffort.Default) => new()
    {
        ClientType = clientType,
        ListenPort = port,
        MaxRetries = 0,
        KeepaliveEnabled = false,
        KeepaliveIdleMinutes = idleMinutes,
        KeepaliveContextLimit = (long)KeepAliveWatchdog.DefaultContextLimit,
        KeepaliveReasoningEffort = reasoningEffort,
        UpstreamBaseUrl = baseUrl,
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
        if (!options.ReasoningEffort.IsSupportedBy(options.ClientType))
        {
            dialog.Error = "该客户端不支持所选思考强度，请重新选择";
            return false;
        }

        ProxyConfig runtime;
        CliCredential upstream;
        CliCredential credential;
        try
        {
            upstream = ResolveCredential(options);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            runtime = CreateRuntimeConfig(upstream.BaseUrl, port, idle.Value, options.ClientType, options.ReasoningEffort);
            var address = new Uri(upstream.BaseUrl);
            if (address.IsLoopback && (address.Port == port || _tasks.Values.Any(item => !item.CanStart && item.ListenPort == address.Port)))
            {
                throw new WorkspaceException("供应商地址指向独立准备服务，请填写实际供应商地址");
            }
            runtime.Validate(true);
            credential = CliCredential.Create(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), runtime.LocalUrl, options.SelectedModel, upstream.AuthMode);
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
        var logger = _logger.Preparation($"{marker} · {options.ClientType.Label()}");
        var service = new ProxyService(_logger, marker)
            .WithKeepAliveWatchdog(watchdog)
            .WithRouteLogger(logger)
            .AsPreparationProxy(upstream.ApiKey, credential.ApiKey, upstream.AuthMode);
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
        logger.WithActivity(LogActivity.Preparation).Info($"已提交准备 · 模型 {options.SelectedModel} · 思考强度 {options.ReasoningEffort.Label()}");
        Save();
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
        _logger.Preparation(task.Title).WithActivity(LogActivity.Service).Info("准备已终止，独立保活已停止");
        _uiNotifier?.Invoke();
    }

    public void Remove(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task) && task.CanStart)
        {
            task.Service?.SetUiNotifier(null);
            _tasks.Remove(taskId);
            Save();
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
            var logger = _logger.Preparation(task.Title).WithActivity(LogActivity.Preparation);
            if (result is PreparationResult.Failed)
            {
                logger.Warn(message);
            }
            else
            {
                logger.Info(message);
            }
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
        _logger.Preparation(task.Title).WithActivity(LogActivity.Service).Warn(message);
        NoticePosted?.Invoke(message);
    }

    public bool HintChangesOverTime() => _tasks.Values.Any(task => task.CanStop);

    /// <summary>退出时结束全部服务并清空内存；不写配置，已保存的任务下次启动照常恢复。</summary>
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

    /// <summary>
    /// 按配置文件恢复任务：一律以“已停止”出现，不启动服务、不读取本机供应商，等用户点“开始准备”。
    /// 客户端或供应商来源无法识别的条目跳过；ID 缺失或重复时换新 ID；编号无效或与前面重复时顺延，
    /// 例如保存的编号依次为 2、2、1 → 恢复为 2、3、1。
    /// </summary>
    private void Restore(IEnumerable<SavedPreparation?> saved)
    {
        var skipped = 0;
        foreach (var entry in saved)
        {
            ClientType? client = entry?.ClientType switch
            {
                "codex" => ClientType.Codex,
                "claude" => ClientType.Claude,
                _ => null,
            };
            PrepareMode? mode = entry?.ProviderSource switch
            {
                // 旧版的 local 与 current 同义：都在开始时取客户端当前使用的供应商。
                SavedPreparation.CurrentSource or SavedPreparation.LegacyLocalSource => PrepareMode.LocalProvider,
                SavedPreparation.CustomSource => PrepareMode.CustomProvider,
                _ => null,
            };
            if (entry is null || client is null || mode is null)
            {
                skipped++;
                continue;
            }
            var custom = mode == PrepareMode.CustomProvider;
            // 不认识或该客户端不支持的思考强度回到默认，与对话框切换客户端时的处理一致。
            var effort = Enum.GetValues<ReasoningEffort>().FirstOrDefault(value => value.AsStr() == entry.ReasoningEffort);
            var options = new PreparationDialogState
            {
                TaskId = string.IsNullOrWhiteSpace(entry.Id) || _tasks.ContainsKey(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
                Mode = mode.Value,
                ClientType = client.Value,
                ProviderUrl = custom ? entry.ProviderUrl ?? string.Empty : string.Empty,
                ApiKey = custom ? entry.ApiKey ?? string.Empty : string.Empty,
                SelectedModel = entry.Model,
                ReasoningEffort = effort.IsSupportedBy(client.Value) ? effort : ReasoningEffort.Default,
                IdleMinutes = entry.IdleMinutes ?? string.Empty,
            };
            var number = entry.Number > 0 && _tasks.Values.All(task => task.Number != entry.Number) ? entry.Number : _nextNumber + 1;
            _nextNumber = Math.Max(_nextNumber, number);
            var task = new PreparationTask(options, number) { ProviderUrl = entry.ProviderUrl ?? string.Empty };
            _tasks.Add(task.Id, task);
        }
        if (skipped > 0)
        {
            _logger.Warn($"一键准备有 {skipped} 项已保存的设置无法识别，已跳过");
        }
    }

    /// <summary>任务新增、改设置或删除后整体写回配置；只写设置，不含服务、端口与会话。</summary>
    private void Save()
    {
        if (_save is null)
        {
            return;
        }
        try
        {
            _save(_tasks.Values.OrderBy(task => task.Number).Select(ToSaved).ToList());
        }
        catch (Exception error)
        {
            NoticePosted?.Invoke($"保存失败：{error.Message}");
        }
    }

    private static SavedPreparation ToSaved(PreparationTask task)
    {
        var custom = task.Options.Mode == PrepareMode.CustomProvider;
        return new SavedPreparation
        {
            Id = task.Id,
            Number = task.Number,
            ClientType = task.ClientType.AsStr(),
            ProviderSource = custom ? SavedPreparation.CustomSource : SavedPreparation.CurrentSource,
            ProviderUrl = task.ProviderUrl,
            ApiKey = custom ? task.Options.ApiKey : string.Empty,
            Model = task.Model,
            ReasoningEffort = task.ReasoningEffort.AsStr(),
            IdleMinutes = task.IdleMinutes,
        };
    }
}
