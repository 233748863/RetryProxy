using System;
using System.Collections.Generic;
using System.Linq;
using RetryProxy.Core.Config;
using RetryProxy.Core.Service;

namespace RetryProxy.Core.Workspace;

public sealed partial class ProxyWorkspace
{
    /// <summary>删除供应商或 Key 前备份配置。回调抛异常时返回错误，不保存、不改内存。</summary>
    public Action? BeforeDestructiveChange { get; set; }

    /// <summary>
    /// 管理操作共用的提交边界：先校验整份候选配置，再备份和落盘，最后发布到内存。
    /// 例如保存新 Key 失败时，当前 Key、界面选择和运行中快照都保持原样。
    /// </summary>
    private string? SaveCandidate(ProxyConfig candidate, bool destructive = false)
    {
        try
        {
            candidate.Normalize().Validate(false);
        }
        catch (ConfigException error)
        {
            return error.Message;
        }

        if (destructive)
        {
            try
            {
                BeforeDestructiveChange?.Invoke();
            }
            catch (Exception error)
            {
                return $"备份失败，未删除：{error.Message}";
            }
        }

        try
        {
            // 存储回调不能通过修改参数影响即将发布的内存配置。
            _save?.Invoke(candidate.Clone());
        }
        catch (Exception error)
        {
            return $"保存失败：{error.Message}";
        }

        Config = candidate;
        return null;
    }

    /// <summary>
    /// 新增或按 ID 完整替换供应商。新增至少一把 Key；已迁移的零 Key 供应商允许先只改基本信息。
    /// 空的新 ID 自动生成，第一个 Key 的空名称补成“默认”；草稿本身不会被修改。
    /// </summary>
    public string? SaveProvider(ProviderEndpoint draft, bool isNew) => SaveProviderCore(draft, isNew);

    private string? SaveProviderCore(ProviderEndpoint draft, bool isNew, bool allowEmptyNew = false, bool selectProvider = true)
    {
        var provider = draft.Clone();
        provider.NormalizeInPlace();
        var existing = Config.ProviderById(provider.Id);
        if (isNew)
        {
            if (existing is not null)
            {
                return "服务商 ID 重复";
            }

            if (provider.Id.Length == 0)
            {
                provider.Id = ProxyConfig.NewId();
            }

            if (!allowEmptyNew && provider.Keys.Count == 0)
            {
                return "新增供应商至少需要一个 Key";
            }
        }
        else
        {
            if (existing is null)
            {
                return "找不到该供应商";
            }

            if (existing.ClientType != provider.ClientType)
            {
                return "不能更改供应商所属客户端";
            }
        }

        // 草稿打开后后台可能识别出了接口。未改查询来源时保留最新识别；改地址/模式则重新识别。
        if (existing is not null)
            provider.BalanceQuery.Detected = provider.BaseUrl == existing.BaseUrl && provider.BalanceQuery.Mode == existing.BalanceQuery.Mode
                ? existing.BalanceQuery.Detected : null;
        if (provider.BalanceQuery.Mode != BalanceQueryMode.Auto) provider.BalanceQuery.Detected = null;

        foreach (var key in provider.Keys)
        {
            if (key.Id.Length == 0)
            {
                key.Id = ProxyConfig.NewId();
            }
        }

        if ((existing is null || existing.Keys.Count == 0) && provider.Keys.FirstOrDefault() is { Name.Length: 0 } first)
        {
            first.Name = "默认";
        }

        var removedKeys = existing?.Keys.Where(key => provider.KeyById(key.Id) is null).ToList() ?? new List<ProviderKey>();
        foreach (var removed in removedKeys)
        {
            if (IsCurrentKey(provider.Id, removed.Id))
            {
                return "当前 Key 不能删除，请先切换到其他 Key";
            }
        }

        if (removedKeys.Count > 0 && provider.Keys.Count == 0)
        {
            return "每个供应商至少保留一个 Key，请改为删除供应商";
        }

        var previous = Config;
        var candidate = Config.Clone();
        if (isNew)
        {
            candidate.Providers.Add(provider);
        }
        else
        {
            candidate.Providers[candidate.Providers.FindIndex(item => item.Id == provider.Id)] = provider;
        }

        var route = candidate.RouteFor(provider.ClientType);
        if (route is not null)
        {
            // 首供应商成为当前供应商；给旧迁移供应商补第一把 Key 后直接开始使用它。
            if (route.CurrentProviderId.Length == 0)
            {
                route.CurrentProviderId = provider.Id;
            }

            if (route.CurrentProviderId == provider.Id && route.CurrentKeyId.Length == 0)
            {
                route.CurrentKeyId = provider.Keys.FirstOrDefault()?.Id ?? string.Empty;
            }

            if (selectProvider)
            {
                candidate.SelectedRouteId = route.Id;
            }
        }

        if (SaveCandidate(candidate, removedKeys.Count > 0) is { } error)
        {
            return error;
        }

        if (selectProvider)
        {
            SelectedProvider = provider.Id;
        }

        RefreshServices();
        ApplyProviderUpdate(previous, provider.Id);
        if (!allowEmptyNew && _startupRequested && route is not null && !_manuallyStoppedRoutes.Contains(route.Id)
            && previous.RouteFor(provider.ClientType)?.CurrentProviderId.Length == 0)
        {
            // 程序已完成自动启动，但当时该客户端为空：补上首供应商后立即启动；手动停止仍优先。
            StartRoute(route.Id);
        }

        _uiNotifier?.Invoke();
        return null;
    }

    /// <summary>保存成功后复用 M2 热更新语义：同一个 Key 的参数变更不打断等待；换 Key 才立即改投。</summary>
    private void ApplyProviderUpdate(ProxyConfig previous, string providerId)
    {
        var provider = Config.ProviderById(providerId)!;
        foreach (var route in Config.Routes.Where(item => item.CurrentProviderId == providerId))
        {
            var oldRoute = previous.Routes.First(item => item.Id == route.Id);
            var oldProvider = previous.ProviderById(oldRoute.CurrentProviderId);
            if (route.CurrentProviderId != oldRoute.CurrentProviderId || route.CurrentKeyId != oldRoute.CurrentKeyId)
            {
                var oldKey = oldProvider?.KeyById(oldRoute.CurrentKeyId);
                var label = oldProvider is null ? "未选择供应商" : oldKey is null ? oldProvider.Name : $"{oldProvider.Name} · {oldKey.Name}";
                ApplySwitch(route.Id, route.Name, label, KeyLabel(providerId, route.CurrentKeyId));
            }
            else
            {
                if (oldProvider?.BaseUrl != provider.BaseUrl && RouteKeepAlives.TryGetValue(route.Id, out var watchdog))
                {
                    watchdog.ResetSession();
                }

                if (RouteState(route.Id) is ServiceState.Starting or ServiceState.Running)
                {
                    PushSnapshot(route.Id);
                    Logger.Route(route.Name).Info($"服务商“{provider.Name}”已更新，之后的尝试立即使用新设置");
                }
            }
        }
    }

    /// <summary>新增或按 ID 替换卡片内的一把 Key；完整验证并持久化后更新正在使用它的通道。</summary>
    public string? SaveKey(string providerId, ProviderKey draft, bool isNew)
    {
        if (Config.ProviderById(providerId) is not { } existing)
        {
            return "找不到该供应商";
        }

        var provider = existing.Clone();
        var key = draft.Clone();
        key.NormalizeInPlace();
        var index = provider.Keys.FindIndex(item => item.Id == key.Id);
        if (isNew)
        {
            if (index >= 0)
            {
                return "Key ID 重复";
            }

            if (key.Id.Length == 0)
            {
                key.Id = ProxyConfig.NewId();
            }

            provider.Keys.Add(key);
        }
        else
        {
            if (index < 0)
            {
                return "找不到该 Key";
            }

            provider.Keys[index] = key;
        }

        return SaveProviderCore(provider, false, selectProvider: false);
    }

    private bool IsCurrentKey(string providerId, string keyId) =>
        Config.Routes.Any(route => route.CurrentProviderId == providerId && route.CurrentKeyId == keyId);

    public string? DeleteKey(string providerId, string keyId)
    {
        if (Config.ProviderById(providerId) is not { } existing)
        {
            return "找不到该供应商";
        }

        if (existing.KeyById(keyId) is null)
        {
            return "找不到该 Key";
        }

        var provider = existing.Clone();
        provider.Keys.RemoveAll(key => key.Id == keyId);
        return SaveProviderCore(provider, false, selectProvider: false);
    }

    public string? RemoveProvider(string providerId) => RemoveProviderCore(providerId);

    private string? RemoveProviderCore(string providerId, bool selectNeighbor = false)
    {
        if (Config.ProviderById(providerId) is null)
        {
            return "找不到该供应商";
        }

        if (Config.Routes.Any(route => route.CurrentProviderId == providerId))
        {
            return "当前供应商不能删除，请先切换到其他供应商的 Key";
        }

        var candidate = Config.Clone();
        var index = candidate.Providers.FindIndex(provider => provider.Id == providerId);
        candidate.Providers.RemoveAt(index);
        var neighbor = selectNeighbor && candidate.Providers.Count > 0
            ? candidate.Providers[Math.Min(index, candidate.Providers.Count - 1)]
            : null;
        if (neighbor is not null && candidate.RouteFor(neighbor.ClientType) is { } neighborRoute)
        {
            candidate.SelectedRouteId = neighborRoute.Id;
        }

        if (SaveCandidate(candidate, destructive: true) is { } error)
        {
            return error;
        }

        if (neighbor is not null)
        {
            SelectedProvider = neighbor.Id;
        }

        SyncSelection();
        _uiNotifier?.Invoke();
        return null;
    }

    /// <summary>复制整张卡片到原卡下方，供应商和 Key 均生成新 ID；成功后选中副本。</summary>
    public string? DuplicateProvider(string providerId)
    {
        if (Config.ProviderById(providerId) is not { } source)
        {
            return "找不到该供应商";
        }

        if (source.Keys.Count == 0)
        {
            return "新增供应商至少需要一个 Key，请先为原供应商添加 Key";
        }

        var duplicate = source.Clone();
        duplicate.Id = ProxyConfig.NewId();
        foreach (var key in duplicate.Keys)
        {
            key.Id = ProxyConfig.NewId();
        }

        var names = Config.ProvidersFor(source.ClientType).Select(provider => provider.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = $"{source.Name} 副本";
        duplicate.Name = baseName;
        for (var suffix = 2; names.Contains(duplicate.Name); suffix++)
        {
            duplicate.Name = $"{baseName} {suffix}";
        }

        var candidate = Config.Clone();
        candidate.Providers.Insert(candidate.Providers.FindIndex(provider => provider.Id == providerId) + 1, duplicate);
        candidate.SelectedRouteId = candidate.RouteFor(duplicate.ClientType)?.Id ?? candidate.SelectedRouteId;
        if (SaveCandidate(candidate) is { } error)
        {
            return error;
        }

        SelectedProvider = duplicate.Id;
        SyncSelection();
        _uiNotifier?.Invoke();
        return null;
    }

    /// <summary>移动到目标卡片原来的位置（向下排在目标之后、向上排在目标之前）；其他客户端槽位不变。</summary>
    public string? MoveProvider(string id, string targetId)
    {
        var source = Config.ProviderById(id);
        var target = Config.ProviderById(targetId);
        if (source is null || target is null)
        {
            return "找不到该供应商";
        }

        if (source.ClientType != target.ClientType)
        {
            return "只能在同一客户端内排序供应商";
        }

        if (id == targetId)
        {
            return null;
        }

        var candidate = Config.Clone();
        var ordered = candidate.ProvidersFor(source.ClientType).ToList();
        MoveTo(ordered, ordered.FindIndex(provider => provider.Id == id), ordered.FindIndex(provider => provider.Id == targetId));
        var next = 0;
        for (var index = 0; index < candidate.Providers.Count; index++)
        {
            if (candidate.Providers[index].ClientType == source.ClientType)
            {
                candidate.Providers[index] = ordered[next++];
            }
        }

        return SaveOrder(candidate);
    }

    /// <summary>只移动指定卡片内的 Key；移动方向与 <see cref="MoveProvider"/> 相同。</summary>
    public string? MoveKey(string providerId, string id, string targetId)
    {
        var candidate = Config.Clone();
        if (candidate.ProviderById(providerId) is not { } provider)
        {
            return "找不到该供应商";
        }

        var from = provider.Keys.FindIndex(key => key.Id == id);
        var to = provider.Keys.FindIndex(key => key.Id == targetId);
        if (from < 0 || to < 0)
        {
            return "找不到该 Key";
        }

        if (from == to)
        {
            return null;
        }

        MoveTo(provider.Keys, from, to);
        return SaveOrder(candidate);
    }

    private static void MoveTo<T>(List<T> items, int from, int to)
    {
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
    }

    private string? SaveOrder(ProxyConfig candidate)
    {
        if (SaveCandidate(candidate) is { } error)
        {
            return error;
        }

        _uiNotifier?.Invoke();
        return null;
    }
}
