using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Service;

namespace RetryProxy.Core.Workspace;

/// <summary>
/// 独立准备的事务与生命周期；界面线程访问，后台通知只请求界面刷新。
/// 管理任务每次开始重新解析目标，运行中的服务始终使用启动快照，不修改普通代理通道。
/// </summary>
public sealed class PreparationWorkspace
{
    private const string SaveError = "准备任务保存失败，请检查配置文件后重试";
    private const string TargetError = "无法读取准备目标，请检查供应商与 Key 设置";
    private const string DuplicateError = "该 Key 已有准备任务，请使用已有任务";
    private const string BlockedError = "所选 Key 正在删除，请等待操作完成";
    private readonly ProxyLogger _logger;
    private readonly Action<List<SavedPreparation>>? _save;
    private readonly Func<ClientType, PreparationKeyRef?, PreparationTarget>? _targetResolver;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, PreparationTask> _tasks = new();
    private readonly Dictionary<PreparationKeyRef, int> _blockedKeys = new();
    private Action? _uiNotifier;
    private int _nextNumber;
    private bool _resumed;
    private bool _shutdown;
    private bool _needsSave;
    private bool _saveWarningPosted;

    public PreparationWorkspace(ProxyLogger logger, IEnumerable<SavedPreparation?>? saved = null,
        Action<List<SavedPreparation>>? save = null, Func<ClientType, CliCredential>? currentProviderResolver = null,
        Func<ClientType, PreparationKeyRef?, PreparationTarget>? targetResolver = null, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _save = save;
        _targetResolver = targetResolver;
        _clock = timeProvider ?? TimeProvider.System;
        if (currentProviderResolver is not null) LocalProviderResolver = currentProviderResolver;
        Restore(saved ?? []);
    }

    internal CliCommand? TestCliCommand { get; set; }
    internal Func<ClientType, CliCredential> LocalProviderResolver { get; set; } = LocalProviderCredentials.Read;

    public IReadOnlyCollection<PreparationTask> Tasks => _tasks.Values;
    public event Action<string>? NoticePosted;

    public void SetUiNotifier(Action? notifier)
    {
        _uiNotifier = notifier;
        foreach (var task in _tasks.Values) task.Service?.SetUiNotifier(notifier);
    }

    public PreparationDialogState OpenPrepareDialog(string? taskId = null) =>
        taskId is not null && _tasks.TryGetValue(taskId, out var task) ? task.Options.Copy() : new PreparationDialogState();

    public PreparationTask? Find(string taskId) => _tasks.GetValueOrDefault(taskId);

    /// <summary>按上次持久绑定查找；跟随当前不会因通道切换而在此处改绑。</summary>
    public PreparationTask? FindForKey(PreparationKeyRef key) => key.IsEmpty ? null
        : _tasks.Values.FirstOrDefault(task => task.Mode != PrepareMode.CustomProvider && task.Binding == key);

    public CliCredential ResolveCredential(PreparationDialogState options) => ResolveTarget(options).Credential;

    private (CliCredential Credential, PreparationTarget? Target) ResolveTarget(PreparationDialogState options)
    {
        if (!Enum.IsDefined(options.ClientType) || !Enum.IsDefined(options.Mode))
            throw new WorkspaceException("请选择准备客户端和供应商来源");
        if (options.Mode == PrepareMode.CustomProvider)
        {
            try
            {
                var provider = new ProviderEndpoint("独立准备", options.ProviderUrl);
                provider.Validate();
                return (CliCredential.Create(options.ApiKey, provider.BaseUrl), null);
            }
            catch (Exception error) when (error is ConfigException or CliException)
            {
                throw new WorkspaceException(error.Message);
            }
        }

        // 解析器属于外部回调，任何异常只能转成固定文案，避免把配置中的密钥带到界面。
        try
        {
            if (_targetResolver is null)
            {
                if (options.Mode == PrepareMode.ListProvider) throw new InvalidOperationException();
                return (LocalProviderResolver(options.ClientType), null);
            }
            PreparationKeyRef? key = options.Mode == PrepareMode.LocalProvider ? null
                : new PreparationKeyRef(options.ProviderId, options.KeyId.Length > 0 ? options.KeyId : options.KeyIds.FirstOrDefault() ?? string.Empty);
            var target = _targetResolver(options.ClientType, key);
            if (target.ClientType != options.ClientType || target.Key.IsEmpty || (key is { } explicitKey && target.Key != explicitKey)
                || target.Credential.IsChannelToken || string.IsNullOrWhiteSpace(target.Credential.ApiKey))
                throw new InvalidOperationException();
            new ProviderEndpoint("独立准备", target.Credential.BaseUrl).Validate();
            return (target.Credential, target);
        }
        catch (Exception)
        {
            throw new WorkspaceException(TargetError);
        }
    }

    public string PlanText(PreparationDialogState options) => options.Mode switch
    {
        PrepareMode.LocalProvider => $"开始时读取 {options.ClientType.Label()} 当前 Key 的地址与密钥，运行中的任务沿用开始时的配置。",
        PrepareMode.ListProvider => "每个 Key 创建一项准备任务，开始时读取供应商设置，运行中的任务沿用开始时的配置。",
        _ => $"使用 {options.ClientType.Label()} 为填写的供应商准备，地址与密钥随任务保存。",
    };

    /// <summary>临时代理单次尝试、不改普通通道重试；准备失败仍由原看门狗反复重试。</summary>
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
        dialog.StartedCount = 0;
        try
        {
            var existing = Find(dialog.TaskId);
            var drafts = new List<PreparationDialogState>();
            if (dialog.Mode == PrepareMode.ListProvider && existing is null)
            {
                var keys = dialog.KeyIds.Count > 0 ? dialog.KeyIds : new List<string> { dialog.KeyId };
                if (keys.Count == 0 || keys.Any(string.IsNullOrWhiteSpace)) throw new WorkspaceException("请至少选择一个 Key");
                if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count) throw new WorkspaceException(DuplicateError);
                foreach (var key in keys)
                {
                    var options = dialog.Copy();
                    if (drafts.Count > 0) options = CopyWithId(options, Guid.NewGuid().ToString("N"));
                    options.KeyId = key;
                    options.KeyIds.Clear();
                    drafts.Add(options);
                }
            }
            else
            {
                // 编辑单项只认 KeyId，避免抽屉遗留的多选值改变其它任务。
                var options = dialog.Copy();
                options.KeyIds.Clear();
                drafts.Add(options);
            }
            var plans = BuildPlans(drafts);
            dialog.Error = CommitStarts(plans, out var started);
            dialog.StartedCount = started;
            return dialog.Error is null;
        }
        catch (WorkspaceException error)
        {
            dialog.Error = error.Message;
            return false;
        }
    }

    /// <summary>
    /// 供应商页快捷准备：复用仍指向所点 Key 的停止项，运行项跳过，整批落盘后才启动。
    /// 停止的跟随任务已指向其它 Key 时，保留跟随任务并新建固定任务；旧绑定在同次提交中解除。
    /// </summary>
    public string? PrepareKeys(ClientType client, IEnumerable<PreparationKeyRef> keys, out int startedCount)
    {
        startedCount = 0;
        try
        {
            var requested = keys.ToList();
            if (!Enum.IsDefined(client) || requested.Any(key => key.IsEmpty)) throw new WorkspaceException("请选择有效的客户端和 Key");
            if (requested.Distinct().Count() != requested.Count) throw new WorkspaceException(DuplicateError);
            var drafts = new List<PreparationDialogState>();
            foreach (var key in requested)
            {
                EnsureUnblocked(key);
                var task = FindForKey(key);
                if (task is not null && task.ClientType != client) throw new WorkspaceException("所选 Key 不属于该客户端");
                if (task?.CanStop == true) continue;
                var options = task?.Options.Copy() ?? new PreparationDialogState { ClientType = client, Mode = PrepareMode.ListProvider };
                options.ProviderId = key.ProviderId;
                options.KeyId = key.KeyId;
                options.KeyIds.Clear();
                drafts.Add(options);
            }
            return CommitStarts(BuildPlans(drafts, fromKeyShortcut: true), out startedCount);
        }
        catch (WorkspaceException error)
        {
            return error.Message;
        }
    }

    private sealed record StartPlan(PreparationTask? Existing, PreparationDialogState Options, int Number,
        PreparationTarget? Target, CliCredential Upstream, CliCredential Credential, ProxyConfig Runtime, string Model,
        PreparationTask? ReleasedFollow);

    private List<StartPlan> BuildPlans(IEnumerable<PreparationDialogState> drafts, bool fromKeyShortcut = false)
    {
        if (_shutdown) throw new WorkspaceException("准备服务已退出，请重新打开软件");
        var plans = new List<StartPlan>();
        var bindings = new HashSet<PreparationKeyRef>();
        var ports = new HashSet<int>();
        var number = _nextNumber;
        foreach (var draft in drafts)
        {
            var options = draft;
            var task = Find(options.TaskId);
            if (task is not null && !task.CanStart) throw new WorkspaceException("请先停止这项准备，再修改设置");
            if (task is not null) EnsureUnblocked(task.Binding);
            PreparationTask? releasedFollow = null;
            (CliCredential Credential, PreparationTarget? Target)? resolvedCurrent = null;
            if (fromKeyShortcut && task?.Mode == PrepareMode.LocalProvider)
            {
                try { resolvedCurrent = ResolveTarget(options); }
                catch (WorkspaceException)
                {
                    // 当前通道暂时无法解析，也不能妨碍用户准备明确点击的另一个有效 Key。
                }
                if (resolvedCurrent?.Target?.Key != new PreparationKeyRef(options.ProviderId, options.KeyId))
                {
                    // 例如跟随任务上次准备 A、当前已切 B：新建固定 A，原任务仍跟随当前。
                    releasedFollow = task;
                    task = null;
                    resolvedCurrent = null;
                    options = new PreparationDialogState
                    {
                        ClientType = draft.ClientType, Mode = PrepareMode.ListProvider,
                        ProviderId = draft.ProviderId, KeyId = draft.KeyId,
                    };
                }
            }
            var idle = UiText.ParseIdleMinutes(options.IdleMinutes);
            if (idle is null || idle < ConfigDefaults.MinKeepaliveIdleMinutes || idle > ConfigDefaults.MaxKeepaliveIdleMinutes)
                throw new WorkspaceException("独立保活间隔请输入 0.5～1440 分钟");
            if (!options.ReasoningEffort.IsSupportedBy(options.ClientType))
                throw new WorkspaceException("该客户端不支持所选思考强度，请重新选择");
            options.SelectedModel = options.SelectedModel?.Trim() ?? string.Empty;
            if (options.Mode == PrepareMode.CustomProvider && options.SelectedModel.Length == 0)
                throw new WorkspaceException("请输入模型名称，或获取模型后选择一个用于准备");
            if (options.Mode == PrepareMode.ListProvider && new PreparationKeyRef(options.ProviderId, options.KeyId).IsEmpty)
                throw new WorkspaceException("请选择供应商和 Key");

            var (upstream, target) = resolvedCurrent ?? ResolveTarget(options);
            if (target is not null)
            {
                EnsureUnblocked(target.Key);
                if (!bindings.Add(target.Key) || _tasks.Values.Any(other => other.Id != options.TaskId
                    && other != releasedFollow && other.Mode != PrepareMode.CustomProvider && other.Binding == target.Key))
                    throw new WorkspaceException(DuplicateError);
                options.ProviderId = target.Key.ProviderId;
                options.KeyId = target.Key.KeyId;
            }
            else
            {
                // 旧测试/独立调用没有管理 ID；不把手动任务凭据误当成供应商绑定。
                options.ProviderId = string.Empty;
                options.KeyId = string.Empty;
            }
            options.KeyIds.Clear();
            var model = options.SelectedModel.Length > 0 ? options.SelectedModel : (target?.DefaultModel ?? upstream.Model ?? string.Empty).Trim();
            if (model.Length == 0) throw new WorkspaceException("该 Key 尚未设置有效模型，请填写准备模型或设置供应商模型");
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                // 同批次的临时监听端口可能被系统复用；重抽直到本批唯一。
                while (!ports.Add(port))
                {
                    listener.Stop();
                    listener.Start();
                    port = ((IPEndPoint)listener.LocalEndpoint).Port;
                }
                var runtime = CreateRuntimeConfig(upstream.BaseUrl, port, idle.Value, options.ClientType, options.ReasoningEffort);
                var address = new Uri(upstream.BaseUrl);
                if (address.IsLoopback && (address.Port == port || _tasks.Values.Any(item => !item.CanStart && item.ListenPort == address.Port)))
                    throw new WorkspaceException("供应商地址指向独立准备服务，请填写实际供应商地址");
                runtime.Validate(true);
                var credential = CliCredential.Create(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), runtime.LocalUrl, model, upstream.AuthMode);
                options.IdleMinutes = UiText.TrimFloat(idle.Value);
                options.ProviderUrl = options.Mode == PrepareMode.CustomProvider ? upstream.BaseUrl : string.Empty;
                options.ApiKey = options.Mode == PrepareMode.CustomProvider ? upstream.ApiKey : string.Empty;
                plans.Add(new StartPlan(task, options, task?.Number ?? ++number, target, upstream, credential, runtime, model, releasedFollow));
            }
            catch (SocketException)
            {
                throw new WorkspaceException("无法分配后台准备端口，请稍后重试");
            }
            catch (Exception error) when (error is ConfigException or CliException or UriFormatException)
            {
                throw new WorkspaceException("准备参数无效，请检查供应商地址与模型设置");
            }
        }
        return plans;
    }

    private string? CommitStarts(List<StartPlan> plans, out int startedCount)
    {
        startedCount = 0;
        if (plans.Count == 0) return null;
        var candidate = ExportSaved();
        foreach (var plan in plans)
        {
            if (plan.ReleasedFollow is { } following)
                WriteOptions(candidate.Single(item => item.Id == following.Id), WithoutBinding(following.Options),
                    following.Number, string.Empty, string.Empty, string.Empty);
            var entry = candidate.FirstOrDefault(item => item.Id == plan.Options.TaskId);
            if (entry is null) { entry = new SavedPreparation(); candidate.Add(entry); }
            WriteOptions(entry, plan.Options, plan.Number, plan.Upstream.BaseUrl,
                plan.Target?.ProviderName ?? string.Empty, plan.Target?.KeyName ?? string.Empty);
            entry.WasRunning = true;
            if (entry.DailyDate.Length == 0) entry.DailyDate = Today;
        }
        var error = TrySave(candidate.OrderBy(item => item.Number).ToList());
        if (error is not null) return error;

        // 所有候选先原子落盘，再一起发布；保存失败既不改原对象，也不会分配服务/启动 CLI。
        foreach (var plan in plans)
        {
            if (plan.ReleasedFollow is { } following)
            {
                following.Options = WithoutBinding(following.Options);
                following.ProviderName = following.KeyName = following.ProviderUrl = string.Empty;
                following.RuntimeModel = null;
            }
            var task = plan.Existing ?? new PreparationTask(plan.Options, plan.Number, _clock) { DailyDate = Today };
            task.Service?.SetUiNotifier(null);
            task.Options = plan.Options;
            task.ProviderName = plan.Target?.ProviderName ?? string.Empty;
            task.KeyName = plan.Target?.KeyName ?? string.Empty;
            task.ProviderUrl = plan.Upstream.BaseUrl;
            task.RuntimeModel = plan.Model;
            task.UpstreamApiKey = plan.Upstream.ApiKey;
            task.LocalAccessKey = plan.Credential.ApiKey;
            task.Credential = plan.Credential;
            task.ListenPort = plan.Runtime.ListenPort;
            task.Failure = null;
            task.Pending = true;
            task.WasRunning = true;
            task.CollectedTotals = default;
            task.BeginTiming();
            _tasks[task.Id] = task;
            _nextNumber = Math.Max(_nextNumber, plan.Number);
        }
        foreach (var plan in plans)
        {
            var task = _tasks[plan.Options.TaskId];
            try
            {
                var watchdog = TestCliCommand is { } command
                    ? KeepAliveWatchdog.WithCliCommand(false, TimeSpan.FromMinutes(plan.Runtime.KeepaliveIdleMinutes), command.Clone())
                    : new KeepAliveWatchdog(false, TimeSpan.FromMinutes(plan.Runtime.KeepaliveIdleMinutes));
                watchdog.RequireContextForPreparation();
                watchdog.EnableAfterPreparation();
                var logger = _logger.Preparation(task.Title);
                var service = new ProxyService(_logger, $"准备 {task.Number}")
                    .WithKeepAliveWatchdog(watchdog).WithRouteLogger(logger)
                    .AsPreparationProxy(plan.Upstream.ApiKey, plan.Credential.ApiKey, plan.Upstream.AuthMode);
                task.Service = service;
                service.SetUiNotifier(_uiNotifier);
                if (!service.RequestStart(plan.Runtime)) throw new InvalidOperationException();
                startedCount++;
                logger.WithActivity(LogActivity.Preparation).Info($"已提交准备 · 模型 {task.Model} · 思考强度 {task.ReasoningEffort.Label()}");
            }
            catch (Exception)
            {
                Fail(task, "后台准备服务未能启动");
            }
        }
        Poll();
        _uiNotifier?.Invoke();
        return null;
    }

    public void Start(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;
        var options = task.Options.Copy();
        if (!SubmitPrepareDialog(options)) NoticePosted?.Invoke(options.Error!);
    }

    public void Stop(string taskId)
    {
        if (StopChecked(taskId) is { } error) NoticePosted?.Invoke(error);
    }

    public string? StopChecked(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task) || (!task.CanStop && !task.WasRunning)) return null;
        var candidate = ExportSaved();
        candidate.Single(item => item.Id == taskId).WasRunning = false;
        if (TrySave(candidate) is { } error) return error;
        task.WasRunning = false;
        StopService(task);
        _logger.Preparation(task.Title).WithActivity(LogActivity.Service).Info("准备已终止，独立保活已停止");
        _uiNotifier?.Invoke();
        return null;
    }

    private static void StopService(PreparationTask task)
    {
        task.EndTiming();
        task.Pending = false;
        if (task.Service is { } service)
        {
            service.KeepAlive.CancelPreparation();
            service.ConfigureKeepAlive(false, service.KeepAlive.Idle);
            service.RequestStop();
        }
        task.Credential = null;
    }

    public void Remove(string taskId)
    {
        if (RemoveChecked(taskId) is { } error) NoticePosted?.Invoke(error);
    }

    public string? RemoveChecked(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task) || !task.CanStart) return null;
        if (_blockedKeys.ContainsKey(task.Binding)) return BlockedError;
        var candidate = ExportSaved().Where(item => item.Id != taskId).ToList();
        if (TrySave(candidate) is { } error) return error;
        task.Service?.SetUiNotifier(null);
        _tasks.Remove(taskId);
        _uiNotifier?.Invoke();
        return null;
    }

    /// <summary>删除等待期间锁住旧绑定和新目标；嵌套屏障按引用计数，释放后才能继续操作。</summary>
    public IDisposable BlockKeys(IEnumerable<PreparationKeyRef> keys)
    {
        var blocked = keys.Where(key => !key.IsEmpty).Distinct().ToList();
        foreach (var key in blocked) _blockedKeys[key] = _blockedKeys.GetValueOrDefault(key) + 1;
        return new KeyBlock(() =>
        {
            foreach (var key in blocked)
            {
                if (_blockedKeys[key] == 1) _blockedKeys.Remove(key);
                else _blockedKeys[key]--;
            }
        });
    }

    private sealed class KeyBlock(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private void EnsureUnblocked(PreparationKeyRef key)
    {
        if (_blockedKeys.ContainsKey(key)) throw new WorkspaceException(BlockedError);
    }

    /// <summary>供跨配置事务合并使用：复制快照、按持久绑定排除；本方法不调用保存回调。</summary>
    public List<SavedPreparation> ExportSaved(IEnumerable<PreparationKeyRef>? excludingKeys = null)
    {
        var excluded = excludingKeys?.Where(key => !key.IsEmpty).ToHashSet();
        foreach (var task in _tasks.Values) _needsSave |= CollectDaily(task);
        return _tasks.Values.Where(task => excluded?.Contains(task.Binding) != true)
            .OrderBy(task => task.Number).Select(ToSaved).ToList();
    }

    /// <summary>调用方已把供应商和任务列表一起落盘；这里仅清理服务与内存，绝不二次保存。</summary>
    public void RemoveForKeysAfterCommit(IEnumerable<PreparationKeyRef> keys)
    {
        var removed = keys.Where(key => !key.IsEmpty).ToHashSet();
        foreach (var task in _tasks.Values.Where(task => removed.Contains(task.Binding)).ToList())
        {
            task.Service?.SetUiNotifier(null);
            StopService(task);
            task.Service?.Stop(TimeSpan.FromSeconds(15));
            _tasks.Remove(task.Id);
        }
        _uiNotifier?.Invoke();
    }

    /// <summary>构造只恢复设置；App 完成启动后显式调用一次，单项错误不阻断其它任务。</summary>
    public void ResumeRunning()
    {
        if (_resumed || _shutdown) return;
        _resumed = true;
        foreach (var task in _tasks.Values.Where(task => task.WasRunning).ToList())
        {
            if (task.CanStop) continue;
            var options = task.Options.Copy();
            if (!SubmitPrepareDialog(options)) Fail(task, options.Error ?? "准备任务恢复失败");
        }
        PersistObservedChanges();
    }

    public void Poll()
    {
        foreach (var task in _tasks.Values.ToList())
        {
            _needsSave |= CollectDaily(task);
            var service = task.Service;
            if (service is null) continue;
            if (task.Pending)
            {
                if (service.State == ServiceState.Starting) continue;
                task.Pending = false;
                try
                {
                    if (service.State != ServiceState.Running || !service.RequestPreparationWith(task.Credential))
                        Fail(task, "后台准备服务未能启动");
                }
                catch (InvalidOperationException)
                {
                    Fail(task, "后台准备服务已停止");
                }
            }
            if ((service.State == ServiceState.Error || (service.State == ServiceState.Stopped && task.WasRunning)) && task.Failure is null)
                Fail(task, "后台准备服务发生异常");
            var result = service.KeepAlive.TakePreparationResult();
            if (result is null) continue;
            if (result is PreparationResult.Failed)
            {
                Fail(task, "准备未完成，请检查供应商与客户端设置");
                continue;
            }
            task.EndTiming();
            var message = result is PreparationResult.Ready
                ? $"{task.Title}：准备完成，已开始独立保活"
                : $"{task.Title}：准备已终止";
            _logger.Preparation(task.Title).WithActivity(LogActivity.Preparation).Info(message);
            NoticePosted?.Invoke(message);
        }
        PersistObservedChanges();
    }

    private void Fail(PreparationTask task, string reason)
    {
        task.Failure = task.Redact(reason);
        task.WasRunning = false;
        _needsSave = true;
        StopService(task);
        var message = $"{task.Title}：{task.Failure}";
        _logger.Preparation(task.Title).WithActivity(LogActivity.Service).Warn(message);
        NoticePosted?.Invoke(message);
    }

    private string Today => _clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>只累计总计差值；替换看门狗前先采集旧值，跨本地日期归零后归入本次观察到的结果。</summary>
    private bool CollectDaily(PreparationTask task)
    {
        var changed = false;
        var today = Today;
        if (task.DailyDate != today)
        {
            task.DailyDate = today;
            task.DailyRounds = 0;
            task.DailySuccesses = 0;
            changed = true;
        }
        var totals = task.Snapshot?.Totals ?? default;
        var previous = task.CollectedTotals;
        var successes = Difference(totals.Completed, previous.Completed);
        var rounds = Add(successes, Add(Difference(totals.Failed, previous.Failed), Difference(totals.Interrupted, previous.Interrupted)));
        task.CollectedTotals = totals;
        task.DailySuccesses = Add(task.DailySuccesses, successes);
        task.DailyRounds = Add(task.DailyRounds, rounds);
        return changed || rounds > 0;
    }

    private static ulong Difference(ulong value, ulong previous) => value >= previous ? value - previous : value;
    private static ulong Add(ulong first, ulong second) => ulong.MaxValue - first < second ? ulong.MaxValue : first + second;

    private void PersistObservedChanges()
    {
        if (!_needsSave || _shutdown) return;
        if (TrySave(ExportSaved()) is { } error && !_saveWarningPosted)
        {
            _saveWarningPosted = true;
            NoticePosted?.Invoke(error);
        }
    }

    public bool HintChangesOverTime() => _tasks.Values.Any(task => task.CanStop);

    /// <summary>保留退出前的运行意图，先关闭服务再保存最终统计；保存失败也不遗留服务或写空任务列表。</summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        try
        {
            foreach (var task in _tasks.Values)
            {
                if (task.Service is not null) task.WasRunning = task.WasRunning && task.CanStop && task.Failure is null;
            }
            foreach (var task in _tasks.Values)
            {
                task.Service?.SetUiNotifier(null);
                StopService(task);
            }
            foreach (var task in _tasks.Values) task.Service?.Stop(TimeSpan.FromSeconds(15));
        }
        finally
        {
            try
            {
                // 取消正在执行的问答会增加中断轮次，必须在停止完成后采集，同时保留先前的恢复意图。
                if (TrySave(ExportSaved()) is { } error) NoticePosted?.Invoke(error);
            }
            finally { _tasks.Clear(); }
        }
    }

    private void Restore(IEnumerable<SavedPreparation?> saved)
    {
        var skipped = 0;
        foreach (var entry in saved)
        {
            ClientType? client = entry?.ClientType switch { "codex" => ClientType.Codex, "claude" => ClientType.Claude, _ => null };
            PrepareMode? mode = entry?.ProviderSource switch
            {
                SavedPreparation.CurrentSource or SavedPreparation.LegacyLocalSource => PrepareMode.LocalProvider,
                SavedPreparation.ListSource => PrepareMode.ListProvider,
                SavedPreparation.CustomSource => PrepareMode.CustomProvider,
                _ => null,
            };
            if (entry is null || client is null || mode is null) { skipped++; continue; }
            var custom = mode == PrepareMode.CustomProvider;
            var effort = Enum.GetValues<ReasoningEffort>().FirstOrDefault(value => value.AsStr() == entry.ReasoningEffort);
            var options = new PreparationDialogState
            {
                TaskId = string.IsNullOrWhiteSpace(entry.Id) || _tasks.ContainsKey(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
                Mode = mode.Value,
                ClientType = client.Value,
                ProviderId = custom ? string.Empty : entry.ProviderId ?? string.Empty,
                KeyId = custom ? string.Empty : entry.KeyId ?? string.Empty,
                ProviderUrl = custom ? entry.ProviderUrl ?? string.Empty : string.Empty,
                ApiKey = custom ? entry.ApiKey ?? string.Empty : string.Empty,
                SelectedModel = entry.Model ?? string.Empty,
                ReasoningEffort = effort.IsSupportedBy(client.Value) ? effort : ReasoningEffort.Default,
                IdleMinutes = entry.IdleMinutes ?? string.Empty,
            };
            var number = entry.Number > 0 && _tasks.Values.All(task => task.Number != entry.Number) ? entry.Number : _nextNumber + 1;
            _nextNumber = Math.Max(_nextNumber, number);
            var task = new PreparationTask(options, number, _clock)
            {
                ProviderUrl = entry.ProviderUrl ?? string.Empty,
                ProviderName = custom ? string.Empty : entry.ProviderName ?? string.Empty,
                KeyName = custom ? string.Empty : entry.KeyName ?? string.Empty,
                WasRunning = entry.WasRunning,
                DailyDate = Today,
                DailyRounds = entry.DailyDate == Today ? entry.DailyRounds : 0,
                DailySuccesses = entry.DailyDate == Today ? entry.DailySuccesses : 0,
            };
            _tasks.Add(task.Id, task);
        }
        if (skipped > 0) _logger.Warn($"一键准备有 {skipped} 项已保存的设置无法识别，已跳过");
    }

    private string? TrySave(List<SavedPreparation> candidate)
    {
        try
        {
            _save?.Invoke(candidate);
            _needsSave = false;
            _saveWarningPosted = false;
            return null;
        }
        catch (Exception)
        {
            return SaveError;
        }
    }

    private static SavedPreparation ToSaved(PreparationTask task)
    {
        var saved = new SavedPreparation
        {
            WasRunning = task.WasRunning,
            DailyDate = task.DailyDate,
            DailyRounds = task.DailyRounds,
            DailySuccesses = task.DailySuccesses,
        };
        WriteOptions(saved, task.Options, task.Number, task.ProviderUrl, task.ProviderName, task.KeyName);
        return saved;
    }

    private static void WriteOptions(SavedPreparation saved, PreparationDialogState options, int number,
        string providerUrl, string providerName, string keyName)
    {
        saved.Id = options.TaskId;
        saved.Number = number;
        saved.ClientType = options.ClientType.AsStr();
        saved.ProviderSource = options.Mode switch
        {
            PrepareMode.CustomProvider => SavedPreparation.CustomSource,
            PrepareMode.ListProvider => SavedPreparation.ListSource,
            _ => SavedPreparation.CurrentSource,
        };
        saved.ProviderUrl = providerUrl;
        saved.ApiKey = options.Mode == PrepareMode.CustomProvider ? options.ApiKey : string.Empty;
        // 留空是每次开始沿用有效默认模型；绝不能把本次解析出的模型反写成用户覆盖。
        saved.Model = options.SelectedModel ?? string.Empty;
        saved.ReasoningEffort = options.ReasoningEffort.AsStr();
        saved.IdleMinutes = options.IdleMinutes;
        saved.ProviderId = options.ProviderId;
        saved.KeyId = options.KeyId;
        saved.ProviderName = providerName;
        saved.KeyName = keyName;
    }

    private static PreparationDialogState WithoutBinding(PreparationDialogState source)
    {
        var options = source.Copy();
        options.ProviderId = options.KeyId = string.Empty;
        options.KeyIds.Clear();
        return options;
    }

    private static PreparationDialogState CopyWithId(PreparationDialogState source, string id) => new()
    {
        TaskId = id, Mode = source.Mode, ClientType = source.ClientType,
        ProviderId = source.ProviderId, KeyId = source.KeyId, KeyIds = new List<string>(source.KeyIds),
        ProviderUrl = source.ProviderUrl, ApiKey = source.ApiKey, SelectedModel = source.SelectedModel,
        ReasoningEffort = source.ReasoningEffort, IdleMinutes = source.IdleMinutes,
    };
}
