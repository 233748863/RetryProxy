using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Workspace;

/// <summary>
/// 供应商与准备任务的交界：统一解析目标、联动删除和一次保存。
/// 例如删除 A 的 Key 时，先禁止新准备并等待旧任务停止，再把 Key 与任务从同一份配置移除。
/// 所有调用都在工作区线程，异步等待不阻塞界面。
/// </summary>
public sealed class PreparationCatalog(
    Func<ProxyConfig> currentConfig,
    PreparationWorkspace preparations,
    Action<ProxyConfig, List<SavedPreparation>> save)
{
    public static PreparationTarget Resolve(ProxyConfig config, ClientType client, PreparationKeyRef? requested)
    {
        var route = config.RouteFor(client);
        var reference = requested ?? new PreparationKeyRef(route?.CurrentProviderId ?? string.Empty, route?.CurrentKeyId ?? string.Empty);
        var provider = config.ProviderById(reference.ProviderId);
        var key = provider?.KeyById(reference.KeyId);
        if (provider is null || provider.ClientType != client || key is null)
            throw new WorkspaceException("请先为当前客户端添加并选择 Key");
        var overridden = key.ModelOverride is { Model.Length: > 0 };
        var model = (overridden ? key.ModelOverride!.Model : provider.Models.Model).Trim();
        var context1M = overridden ? key.ModelOverride!.Context1M : provider.Models.Context1M;
        if (client == ClientType.Claude && context1M && model.Length > 0 && !model.EndsWith("[1M]", StringComparison.OrdinalIgnoreCase))
            model += "[1M]";
        return new PreparationTarget
        {
            ClientType = client,
            Key = reference,
            ProviderName = provider.Name,
            KeyName = key.Name,
            Credential = CliCredential.Create(key.ApiKey, provider.BaseUrl, authMode: provider.AuthMode),
            DefaultModel = model,
        };
    }

    /// <summary>用作 ProxyWorkspace 的保存回调；存储成功前不删除内存任务，失败仅返回固定文案。</summary>
    public void SaveProxy(ProxyConfig candidate)
    {
        var removed = RemovedKeys(currentConfig(), candidate);
        if (Affected(removed).Any(task => !task.CanStart))
            throw new WorkspaceException("请先停止相关准备任务再删除 Key");
        try { save(candidate, preparations.ExportSaved(removed)); }
        catch (Exception) { throw new WorkspaceException("配置保存失败，请检查配置文件后重试"); }
        preparations.RemoveForKeysAfterCommit(removed);
    }

    /// <summary>
    /// 通道退出仍会保存代理配置，必须先完成它，再由准备工作区保存运行意图并清空内存。
    /// 顺序相反会把已清空的任务列表再次落盘，导致下一次启动丢失全部准备任务。
    /// </summary>
    public void Shutdown(ProxyWorkspace workspace)
    {
        try { workspace.Shutdown(); }
        finally { preparations.Shutdown(); }
    }

    public async Task<string?> ChangeAsync(IEnumerable<PreparationKeyRef> keys, Func<string?> commit,
        CancellationToken cancellation = default)
    {
        var removed = keys.Where(key => !key.IsEmpty).ToHashSet();
        using var blocked = preparations.BlockKeys(removed);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var task in Affected(removed).ToList())
                if (preparations.StopChecked(task.Id) is { } error) return error;
            var timer = Stopwatch.StartNew();
            while (Affected(removed).Any(task => !task.CanStart))
            {
                cancellation.ThrowIfCancellationRequested();
                if (timer.Elapsed >= TimeSpan.FromSeconds(15))
                    return "准备任务未能在 15 秒内停止，尚未删除，请检查运行日志";
                await Task.Delay(50, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            // 等待期间可能切换当前 Key；最终由原管理入口重新校验，禁止删除新选中的当前 Key。
            return commit();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return "操作已取消"; }
    }

    private IEnumerable<PreparationTask> Affected(IReadOnlySet<PreparationKeyRef> keys) => preparations.Tasks
        .Where(task => task.Mode != PrepareMode.CustomProvider && keys.Contains(new PreparationKeyRef(task.ProviderId, task.KeyId)));

    public static HashSet<PreparationKeyRef> RemovedKeys(ProxyConfig previous, ProxyConfig candidate) => previous.Providers
        .SelectMany(provider => provider.Keys.Where(key => candidate.ProviderById(provider.Id)?.KeyById(key.Id) is null)
            .Select(key => new PreparationKeyRef(provider.Id, key.Id))).ToHashSet();
}
