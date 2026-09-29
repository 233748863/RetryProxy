using System;
using System.Collections.Generic;
using RetryProxy.Core.Config;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;

namespace RetryProxy.Core.Client;

public enum ClientConnectionStatus { Direct, TakenOver, Modified, Unavailable }

public sealed class ClientConnectionInfo
{
    public ClientConnectionStatus Status { get; init; }
    public string ConfigPath { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// 接管的生命周期协调，不依赖界面。只有运行状态、有效模型或当前 Key 改变时才考虑写文件；
/// 例如每个请求刷新统计不会重复读取客户端配置，窗口激活则显式调用 Detect。
/// </summary>
public sealed class ClientTakeoverCoordinator
{
    private readonly ProxyWorkspace _workspace;
    private readonly Func<ClientType, ClientTakeoverState, ClientConfigStore> _storeFactory;
    private readonly Action _backupConfig;
    private readonly Dictionary<ClientType, ServiceState> _states = new();
    private readonly Dictionary<ClientType, ChannelSnapshot> _snapshots = new();
    private readonly Dictionary<ClientType, ClientConnectionInfo> _connections = new();
    private bool _busy;
    private bool _closing;

    public ClientTakeoverCoordinator(ProxyWorkspace workspace,
        Func<ClientType, ClientTakeoverState, ClientConfigStore> storeFactory, Action backupConfig)
    {
        _workspace = workspace;
        _storeFactory = storeFactory;
        _backupConfig = backupConfig;
    }

    public ClientConnectionInfo Connection(ClientType client) => _connections.GetValueOrDefault(client) ?? new();
    public ClientConfigStore Store(ClientType client) => _storeFactory(client, State(client));
    private ClientTakeoverState State(ClientType client) => _workspace.Config.ClientTakeover[client];

    public void Detect()
    {
        if (_closing || _busy) return;
        foreach (var route in _workspace.Config.Routes) Detect(route.ClientType);
    }

    private void Detect(ClientType client)
    {
        var state = State(client);
        if (ClientConfigPaths.WritesBlocked)
        {
            _connections[client] = new() { Status = ClientConnectionStatus.Unavailable, Error = "测试注入模式禁止修改客户端配置" };
            return;
        }
        try
        {
            var store = Store(client);
            var route = _workspace.Config.RouteFor(client)!;
            var takenOver = false;
            var direct = !state.Enabled;
            if (_workspace.Config.CurrentKeyOf(route) is not null)
            {
                var snapshot = _workspace.Config.SnapshotFor(route.Id);
                takenOver = store.IsTakenOver(snapshot, route.ListenPort);
                if (!takenOver) direct = IsDirect(store.ReadProfile(), snapshot);
            }
            else
                _ = store.ReadProfile();
            var running = _workspace.RouteState(route.Id) is ServiceState.Running or ServiceState.Starting;
            _connections[client] = new()
            {
                Status = takenOver ? ClientConnectionStatus.TakenOver
                    : state.Enabled && (running || !direct) ? ClientConnectionStatus.Modified : ClientConnectionStatus.Direct,
                ConfigPath = store.ConfigPath,
                BackupPath = state.BackupPath,
            };
        }
        catch (ClientConfigException error) { Failed(client, error.Message, notify: false); }
    }

    /// <summary>由已有的事件合并刷新调用；监听成功后才接管，监听失败则恢复直连。</summary>
    public void Refresh()
    {
        if (_busy || _closing) return;
        _busy = true;
        try
        {
            foreach (var route in _workspace.Config.Routes.ToArray())
            {
                try { RefreshRoute(route); }
                catch (ClientConfigException error) { Failed(route.ClientType, error.Message); }
            }
        }
        finally { _busy = false; }
    }

    private void RefreshRoute(ProxyRoute route)
    {
        var client = route.ClientType;
        var currentState = _workspace.RouteState(route.Id);
        var known = _states.TryGetValue(client, out var previousState);
        _states[client] = currentState;
        if (_workspace.Config.CurrentKeyOf(route) is null) return;
        var snapshot = _workspace.Config.SnapshotFor(route.Id);
        _snapshots.TryGetValue(client, out var previous);
        _snapshots[client] = snapshot;
        if (!State(client).Enabled || ClientConfigPaths.WritesBlocked) return;

        bool? useProxy = null;
        var modelsOnly = false;
        if (currentState == ServiceState.Running && (!known || previousState != ServiceState.Running))
            useProxy = true;
        else if (currentState is ServiceState.Stopped or ServiceState.Error && (!known || previousState != currentState))
            useProxy = false;
        else if (previous is not null && currentState == ServiceState.Running && !SameModels(previous, snapshot))
        {
            useProxy = true;
            modelsOnly = true;
        }
        else if (previous is not null && currentState is ServiceState.Stopped or ServiceState.Error
            && (!SameModels(previous, snapshot) || previous.ApiKey != snapshot.ApiKey
                || previous.UpstreamBaseUrl != snapshot.UpstreamBaseUrl || previous.AuthMode != snapshot.AuthMode))
            useProxy = false;
        if (useProxy is null) return;
        if (!CanUpdateAutomatically(client, previous ?? snapshot)
            || modelsOnly && !Store(client).IsTakenOver(snapshot, route.ListenPort))
        {
            ExternalChange(client);
            return;
        }
        if (Apply(client, useProxy.Value, modelsOnly) is { } error) Failed(client, error);
    }

    public string? TakeOver(ClientType client)
    {
        var route = _workspace.Config.RouteFor(client)!;
        if (_workspace.RouteState(route.Id) != ServiceState.Running) return "请先启动代理，再接管客户端";
        if (_workspace.Config.CurrentKeyOf(route) is null) return "请先为当前客户端添加并选择 Key";
        if (ClientConfigPaths.WritesBlocked) return "测试注入模式禁止修改客户端配置";
        try { _backupConfig(); }
        catch (Exception) { return "接管前备份失败，客户端配置未修改"; }
        return Apply(client, useProxy: true);
    }

    public string? CancelTakeover(ClientType client) => Apply(client, useProxy: false, enabled: false);

    public string? StopRoute(string routeId)
    {
        var route = _workspace.Config.Routes.Find(item => item.Id == routeId);
        if (route is null) return "找不到转发通道";
        if (State(route.ClientType).Enabled)
        {
            try
            {
                var previous = _snapshots.GetValueOrDefault(route.ClientType) ?? _workspace.Config.SnapshotFor(route.Id);
                if (CanUpdateAutomatically(route.ClientType, previous))
                {
                    if (Apply(route.ClientType, useProxy: false) is { } error) return error;
                }
                else ExternalChange(route.ClientType);
            }
            catch (ClientConfigException error) { return error.Message; }
        }
        _workspace.StopRoute(routeId);
        _states[route.ClientType] = _workspace.RouteState(routeId);
        Detect(route.ClientType);
        return null;
    }

    public void Shutdown()
    {
        _closing = true;
        foreach (var route in _workspace.Config.Routes.ToArray())
        {
            if (!State(route.ClientType).Enabled || ClientConfigPaths.WritesBlocked) continue;
            try
            {
                var previous = _snapshots.GetValueOrDefault(route.ClientType) ?? _workspace.Config.SnapshotFor(route.Id);
                if (!CanUpdateAutomatically(route.ClientType, previous)) continue;
                if (Apply(route.ClientType, useProxy: false) is { } error)
                    _workspace.Logger.Route(route.Name).Error($"退出时恢复直连失败：{error}");
            }
            catch (ClientConfigException)
            {
                _workspace.Logger.Route(route.Name).Error("退出时无法读取客户端配置，未覆盖文件");
            }
        }
    }

    private string? Apply(ClientType client, bool useProxy, bool modelsOnly = false, bool enabled = true)
    {
        var route = _workspace.Config.RouteFor(client)!;
        if (_workspace.Config.CurrentKeyOf(route) is null) return "请先为当前客户端添加并选择 Key";
        try
        {
            var request = new ClientConfigRequest
            {
                Channel = _workspace.Config.SnapshotFor(route.Id), ListenPort = route.ListenPort,
                UseProxy = useProxy, ModelsOnly = modelsOnly,
            };
            Store(client).Apply(request, State(client), enabled, updated =>
            {
                if (_workspace.SaveClientTakeover(client, updated) is not null)
                    throw new ClientConfigException("接管状态保存失败，客户端配置已回滚");
            });
            _snapshots[client] = request.Channel;
            Detect(client);
            return null;
        }
        catch (ClientConfigException error) { return error.Message; }
        catch (ConfigException) { return "当前供应商配置无效，客户端配置未修改"; }
    }

    private bool CanUpdateAutomatically(ClientType client, ChannelSnapshot previous)
    {
        var store = Store(client);
        var state = State(client);
        if (state.LastWrittenHash.Length > 0 && store.CurrentHash() == state.LastWrittenHash) return true;
        var route = _workspace.Config.RouteFor(client)!;
        if (store.IsTakenOver(previous, route.ListenPort)) return true;
        return IsDirect(store.ReadProfile(), previous);
    }

    private static bool IsDirect(ClientProfile profile, ChannelSnapshot snapshot)
    {
        var url = snapshot.ClientType == ClientType.Codex ? UrlRules.CodexApiRoot(snapshot.UpstreamBaseUrl) : snapshot.UpstreamBaseUrl;
        return profile.ApiKey == snapshot.ApiKey
            && profile.BaseUrl.TrimEnd('/') == url.TrimEnd('/')
            && profile.AuthMode == snapshot.AuthMode && !profile.HasApiKeyHelper && !profile.HasConflictingSettings;
    }

    private void ExternalChange(ClientType client)
    {
        Detect(client);
        var info = Connection(client);
        _connections[client] = new() { Status = ClientConnectionStatus.Modified, ConfigPath = info.ConfigPath,
            BackupPath = info.BackupPath, Error = "客户端配置被改动，已保留外部修改，请重新接管" };
        _workspace.Notice = "客户端配置被改动，已保留外部修改，请重新接管";
    }

    private void Failed(ClientType client, string error, bool notify = true)
    {
        _connections[client] = new()
        {
            Status = ClientConnectionStatus.Unavailable, ConfigPath = State(client).ConfigPath,
            BackupPath = State(client).BackupPath, Error = error,
        };
        if (notify) _workspace.Notice = error;
    }

    private static bool SameModels(ChannelSnapshot left, ChannelSnapshot right) =>
        new ClientConfigRequest { Channel = left }.EffectiveModels.Equals(new ClientConfigRequest { Channel = right }.EffectiveModels);
}
