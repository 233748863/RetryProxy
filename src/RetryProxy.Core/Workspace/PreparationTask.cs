using System;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Service;

namespace RetryProxy.Core.Workspace;

/// <summary>一项准备任务拥有自己的配置、服务和会话，与代理通道没有归属关系。</summary>
public sealed class PreparationTask
{
    private readonly TimeProvider _clock;
    private DateTimeOffset? _startedAt;
    private TimeSpan _elapsed;

    internal PreparationTask(PreparationDialogState options, int number, TimeProvider? timeProvider = null)
    {
        Options = options;
        Number = number;
        _clock = timeProvider ?? TimeProvider.System;
    }

    internal PreparationDialogState Options { get; set; }
    internal ProxyService? Service { get; set; }
    internal CliCredential? Credential { get; set; }
    internal string? Failure { get; set; }
    internal string? RuntimeModel { get; set; }
    internal string UpstreamApiKey { get; set; } = string.Empty;
    internal string LocalAccessKey { get; set; } = string.Empty;
    internal KeepAliveTotals CollectedTotals { get; set; }
    internal PreparationKeyRef Binding => new(ProviderId, KeyId);

    public string Id => Options.TaskId;
    public int Number { get; }
    public string Title => $"准备 {Number} · {Options.ClientType.Label()}";
    public ClientType ClientType => Options.ClientType;
    public PrepareMode Mode => Options.Mode;
    public string ProviderId => Options.ProviderId;
    public string KeyId => Options.KeyId;
    public string ProviderName { get; internal set; } = string.Empty;
    public string KeyName { get; internal set; } = string.Empty;
    public string Model => RuntimeModel ?? Options.SelectedModel ?? string.Empty;
    public ReasoningEffort ReasoningEffort => Options.ReasoningEffort;
    public string IdleMinutes => Options.IdleMinutes;
    public string ProviderUrl { get; internal set; } = string.Empty;
    public int? ListenPort { get; internal set; }
    public bool Pending { get; internal set; }
    public bool WasRunning { get; internal set; }
    public string DailyDate { get; internal set; } = string.Empty;
    public ulong DailyRounds { get; internal set; }
    public ulong DailySuccesses { get; internal set; }
    public ServiceState State => Service?.State ?? ServiceState.Stopped;
    public KeepAliveSnapshot? Snapshot => Service?.KeepAlive.Snapshot();
    public bool CanStart => State is ServiceState.Stopped or ServiceState.Error;
    public bool CanStop => State is ServiceState.Starting or ServiceState.Running;
    public bool IsPreparing => CanStop && (Pending || Snapshot?.Preparing == true);
    public bool IsReady => State == ServiceState.Running && !Pending && Snapshot is { Preparing: false, Enabled: true, LastSuccess: not null };
    public TimeSpan PreparationElapsed => _startedAt is { } start && IsPreparing
        ? NonNegative(_clock.GetUtcNow() - start) : _elapsed;
    public TimeSpan NextKeepAlive => IsReady
        ? NonNegative((Snapshot?.Idle ?? TimeSpan.Zero) - (Service?.KeepAlive.IdleFor() ?? TimeSpan.Zero))
        : TimeSpan.Zero;
    public string? LastError
    {
        get
        {
            var snapshot = Snapshot;
            return Redact(Failure ?? Service?.StartupError
                ?? (snapshot is { Preparing: true, PreparationLastErrorIsTimeout: true } ? null : snapshot?.PreparationLastError));
        }
    }

    internal void BeginTiming()
    {
        _startedAt = _clock.GetUtcNow();
        _elapsed = TimeSpan.Zero;
    }

    internal void EndTiming()
    {
        if (_startedAt is not { } start) return;
        _elapsed = NonNegative(_clock.GetUtcNow() - start);
        _startedAt = null;
    }

    // 保留可操作的错误原因，但精确遮盖本项上游密钥、手填密钥与临时本地口令。
    internal string? Redact(string? message)
    {
        if (message is null) return null;
        foreach (var secret in new[] { UpstreamApiKey, Options.ApiKey, LocalAccessKey, Credential?.ApiKey })
        {
            if (!string.IsNullOrEmpty(secret)) message = message.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
        }
        return message;
    }

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    public string Status => State switch
    {
        ServiceState.Stopped => Failure is null ? "已停止" : "运行异常",
        ServiceState.Stopping => "正在停止",
        ServiceState.Error => "运行异常",
        ServiceState.Starting => "正在启动",
        _ => IsPreparing ? "准备中" : "保活中",
    };

    public string Hint
    {
        get
        {
            if (State == ServiceState.Stopping) return "正在结束后台问答";
            if (CanStart) return LastError is not null ? "可修改设置后重新开始" : "可按当前设置重新开始";
            if (Pending || State == ServiceState.Starting) return "正在启动独立准备服务";
            var snapshot = Snapshot;
            if (snapshot?.Preparing == true)
            {
                if (snapshot.PreparationRetryAfter is { } delay)
                    return $"第 {snapshot.PreparationAttempts} 次未完成，{Math.Ceiling(delay.TotalSeconds):F0} 秒后重试";
                return snapshot.PreparationAttempts == 0
                    ? "准备已排队，即将发送"
                    : $"正在进行第 {snapshot.PreparationAttempts} 次准备，失败自动重试";
            }
            if (snapshot?.Probing == true) return "准备完成，正在进行后台保活";
            return $"准备完成，距下次保活 {UiText.FormatDurationCn(NextKeepAlive)}";
        }
    }
}
