using System;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Service;

namespace RetryProxy.Core.Workspace;

/// <summary>一项准备任务拥有自己的配置、服务和会话，与代理通道没有归属关系。</summary>
public sealed class PreparationTask
{
    internal PreparationTask(PreparationDialogState options, int number)
    {
        Options = options;
        Number = number;
    }

    internal PreparationDialogState Options { get; set; }
    internal ProxyService? Service { get; set; }
    internal CliCredential? Credential { get; set; }
    internal string? Failure { get; set; }

    public string Id => Options.TaskId;
    public int Number { get; }
    public string Title => $"准备 {Number} · {Options.ClientType.Label()}";
    public ClientType ClientType => Options.ClientType;
    public string Model => Options.SelectedModel ?? string.Empty;
    public ReasoningEffort ReasoningEffort => Options.ReasoningEffort;
    public string IdleMinutes => Options.IdleMinutes;
    public string ProviderUrl { get; internal set; } = string.Empty;
    public int? ListenPort { get; internal set; }
    public bool Pending { get; internal set; }
    public ServiceState State => Service?.State ?? ServiceState.Stopped;
    public KeepAliveSnapshot? Snapshot => Service?.KeepAlive.Snapshot();
    public bool CanStart => State is ServiceState.Stopped or ServiceState.Error;
    public bool CanStop => State is ServiceState.Starting or ServiceState.Running;
    public bool IsPreparing => CanStop && (Pending || Snapshot?.Preparing == true);
    public string? LastError => Failure ?? Service?.StartupError ?? Snapshot?.PreparationLastError;

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
            if (State == ServiceState.Stopping)
            {
                return "正在结束后台问答";
            }
            if (CanStart)
            {
                return LastError is not null ? "可修改设置后重新开始" : "可按当前设置重新开始";
            }
            if (Pending || State == ServiceState.Starting)
            {
                return "正在启动独立准备服务";
            }
            var snapshot = Snapshot;
            if (snapshot?.Preparing == true)
            {
                if (snapshot.PreparationRetryAfter is { } delay)
                {
                    return $"第 {snapshot.PreparationAttempts} 次未完成，{Math.Ceiling(delay.TotalSeconds):F0} 秒后重试";
                }
                return snapshot.PreparationAttempts == 0
                    ? "准备已排队，即将发送"
                    : $"正在进行第 {snapshot.PreparationAttempts} 次准备，失败自动重试";
            }
            if (snapshot?.Probing == true)
            {
                return "准备完成，正在进行后台保活";
            }
            var remaining = (snapshot?.Idle ?? TimeSpan.Zero) - (Service?.KeepAlive.IdleFor() ?? TimeSpan.Zero);
            return $"准备完成，距下次保活 {UiText.FormatDurationCn(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining)}";
        }
    }
}
