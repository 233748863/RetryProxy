using RetryProxy.Core.Config;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.View.Controls;
using RetryProxy.View.Drawers;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RetryProxy.Service;

/// <summary>
/// 右侧抽屉入口。MainWindow 在内容区覆盖层放置 DrawerHost 后调用 SetHost；所有方法仅在 UI 线程调用。
/// 此服务只维护代理配置，不读写 Claude Code / Codex 的真实客户端配置。
/// </summary>
public sealed class DrawerService
{
    private readonly WorkspaceService _workspaceService;
    private readonly IContentDialogService _dialogs;
    private readonly ISnackbarService _snackbar;
    private readonly Dialogs _legacyDialogs;
    private DrawerHost? _host;
    private bool _editing;
    private ProxyWorkspace Workspace => _workspaceService.Workspace;

    public DrawerService(WorkspaceService workspaceService, IContentDialogService dialogs, ISnackbarService snackbar, Dialogs legacyDialogs)
    {
        _workspaceService = workspaceService;
        _dialogs = dialogs;
        _snackbar = snackbar;
        _legacyDialogs = legacyDialogs;
    }

    public void SetHost(DrawerHost host)
    {
        host.Dispatcher.VerifyAccess();
        if (_host?.IsOpen == true) throw new InvalidOperationException(DrawerText.T("请先关闭当前抽屉"));
        _host = host;
        host.ConfirmDiscardAsync = () => ConfirmAsync("放弃未保存的修改？", "关闭后，本次未保存的修改将丢弃。", "放弃修改");
    }

    private DrawerHost Host
    {
        get
        {
            var host = _host ?? throw new InvalidOperationException(DrawerText.T("请先通过 SetHost 注册抽屉宿主"));
            host.Dispatcher.VerifyAccess();
            return host;
        }
    }

    public async Task EditProviderAsync(ProviderEndpoint? provider, ClientType client)
    {
        var host = Host;
        if (_editing || host.IsOpen) return;
        _editing = true;
        try
        {
            var isNew = provider is null;
            if (!isNew)
            {
                provider = Workspace.Config.ProviderById(provider!.Id);
                if (provider is null) { ShowMessage("找不到供应商"); return; }
            }
            var draft = provider?.Clone() ?? new ProviderEndpoint
            {
                Id = ProxyConfig.NewId(), ClientType = client,
                Models = new ProviderModels { Context1M = client == ClientType.Claude },
                Keys = [new ProviderKey { Id = ProxyConfig.NewId(), Name = DrawerText.T("默认") }],
            };
            using var drawer = new ProviderDrawer(draft, isNew);
            drawer.CannotDeleteKey = keyId => Workspace.Config.Routes.Any(route => route.CurrentProviderId == draft.Id && route.CurrentKeyId == keyId)
                ? "当前 Key 不能删除，请先切换到其他 Key" : null;
            drawer.ConfirmDeleteAsync = async (title, body) =>
            {
                using var focus = host.SuspendFocusConstraint();
                return await _legacyDialogs.ConfirmDeleteAsync(title, body);
            };
            drawer.EditKeyAsync = key => EditDraftKeyAsync(drawer.Draft, key);
            drawer.CommitAsync = () => Task.FromResult(drawer.ReadDraft() ?? Workspace.SaveProvider(drawer.Draft, isNew));
            drawer.RefreshKeys();
            var previousSelection = string.Empty;
            void RefreshSelection()
            {
                var route = Workspace.Config.RouteFor(draft.ClientType);
                var selection = $"{route?.CurrentProviderId}/{route?.CurrentKeyId}";
                if (previousSelection == selection) return;
                previousSelection = selection;
                drawer.RefreshDeleteRestrictions();
            }
            _workspaceService.Refreshed += RefreshSelection;
            try
            {
                RefreshSelection();
                if (await host.ShowAsync(drawer)) Saved();
            }
            finally { _workspaceService.Refreshed -= RefreshSelection; }
        }
        finally { _editing = false; }
    }

    private async Task<ProviderKey?> EditDraftKeyAsync(ProviderEndpoint provider, ProviderKey? key)
    {
        var isNew = key is null;
        var draft = key?.Clone() ?? new ProviderKey { Id = ProxyConfig.NewId(), Name = provider.Keys.Count == 0 ? DrawerText.T("默认") : string.Empty };
        using var drawer = new KeyDrawer(provider, draft, isNew, nested: true);
        drawer.CommitAsync = () => Task.FromResult(drawer.ReadDraft());
        return await Host.ShowAsync(drawer) ? drawer.Draft.Clone() : null;
    }

    public async Task EditKeyAsync(string providerId, string? keyId)
    {
        var host = Host;
        if (_editing || host.IsOpen) return;
        _editing = true;
        try
        {
            var provider = Workspace.Config.ProviderById(providerId);
            if (provider is null) { ShowMessage("找不到供应商"); return; }
            var isNew = keyId is null;
            var key = isNew ? new ProviderKey { Id = ProxyConfig.NewId(), Name = provider.Keys.Count == 0 ? DrawerText.T("默认") : string.Empty }
                : provider.KeyById(keyId!);
            if (key is null) { ShowMessage("找不到 Key"); return; }
            using var drawer = new KeyDrawer(provider.Clone(), key, isNew, nested: false);
            drawer.CommitAsync = () => Task.FromResult(drawer.ReadDraft() ?? Workspace.SaveKey(providerId, drawer.Draft, isNew));
            if (await host.ShowAsync(drawer)) Saved();
        }
        finally { _editing = false; }
    }

    public async Task EditProxyAsync(string routeId)
    {
        var host = Host;
        if (_editing || host.IsOpen) return;
        _editing = true;
        try
        {
            var route = Workspace.Config.Routes.FirstOrDefault(item => item.Id == routeId);
            if (route is null) { ShowMessage("找不到转发通道"); return; }
            using var drawer = new ProxyDrawer(route.Clone());
            void Refresh() => drawer.RefreshRuntime(Workspace.RouteState(routeId));
            drawer.CommitAsync = () => SaveProxyAsync(drawer);
            drawer.ToggleAsync = () => ToggleProxyAsync(drawer);
            _workspaceService.Refreshed += Refresh;
            try
            {
                Refresh();
                if (await host.ShowAsync(drawer)) Saved();
            }
            finally { _workspaceService.Refreshed -= Refresh; }
        }
        finally { _editing = false; }
    }

    private async Task<string?> SaveProxyAsync(ProxyDrawer drawer)
    {
        if (drawer.TryRead(Workspace, out var editor, out var keepAlive) is { } error) return error;
        var original = Workspace.Config.Routes[editor!.Index];
        var portChanged = Workspace.RouteFromEditor(editor).ListenPort != original.ListenPort;
        var state = Workspace.RouteState(drawer.RouteId);
        if (state is ServiceState.Starting or ServiceState.Stopping) return "代理正在启动或停止，请稍后再试";
        var restart = portChanged && state == ServiceState.Running;
        if (portChanged)
        {
            var body = "修改端口会先停止运行中的代理，当前连接将中断；保存后仅恢复原来运行的代理。客户端地址需要自行调整。";
            if (!await ConfirmAsync("确认修改端口", body, "确认修改")) return "端口修改已取消，设置尚未保存";
            if (drawer.Lifetime.IsCancellationRequested) return "操作已取消";
            // 确认框打开期间托盘可能改变通道状态或当前 Key，再校验一次，避免保存旧引用。
            if (drawer.TryRead(Workspace, out editor, out keepAlive) is { } beforeStopError) return beforeStopError;
            state = Workspace.RouteState(drawer.RouteId);
            if (state is ServiceState.Starting or ServiceState.Stopping) return "代理正在启动或停止，请稍后再试";
            restart = state == ServiceState.Running;
            if (restart)
            {
                Workspace.StopRoute(drawer.RouteId);
                _workspaceService.Flush();
                if (!await WaitForStateAsync(drawer.RouteId, running: false, drawer.Lifetime))
                    return "代理未能在 15 秒内停止，设置尚未保存，请检查运行日志";
            }
        }

        // 保活与重试先一起验证，再通过 CommitRoute 一次保存，避免保存失败时只改了一半。
        if (drawer.TryRead(Workspace, out editor, out keepAlive) is { } latestError)
        {
            if (restart) await RestartAsync(drawer.RouteId, drawer.Lifetime);
            return latestError;
        }
        if (Workspace.CommitRoute(editor!) is { } commitError)
        {
            if (restart) await RestartAsync(drawer.RouteId, drawer.Lifetime);
            return commitError;
        }
        _workspaceService.Flush();
        if (restart && !await RestartAsync(drawer.RouteId, drawer.Lifetime))
            return "设置已保存，但代理未能重新启动，请检查运行日志";
        return null;
    }

    private async Task<string?> ToggleProxyAsync(ProxyDrawer drawer)
    {
        var state = Workspace.RouteState(drawer.RouteId);
        if (state is ServiceState.Starting or ServiceState.Stopping) return "代理正在启动或停止，请稍后再试";
        if (state == ServiceState.Running)
        {
            if (!await ConfirmAsync("停止代理", "停止代理后，当前连接将中断。客户端配置不会自动改动。", "确认停止")) return null;
            drawer.Lifetime.ThrowIfCancellationRequested();
            Workspace.StopRoute(drawer.RouteId);
            _workspaceService.Flush();
            if (!await WaitForStateAsync(drawer.RouteId, running: false, drawer.Lifetime))
                return "代理未能在 15 秒内停止，请检查运行日志";
        }
        else if (!await RestartAsync(drawer.RouteId, drawer.Lifetime))
            return "代理启动失败，请检查供应商设置和运行日志";
        _workspaceService.Flush();
        drawer.RefreshRuntime(Workspace.RouteState(drawer.RouteId));
        return null;
    }

    private async Task<bool> RestartAsync(string routeId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Workspace.StartRoute(routeId);
        _workspaceService.Flush();
        var started = await WaitForStateAsync(routeId, running: true, cancellation);
        _workspaceService.Flush();
        return started;
    }

    private async Task<bool> WaitForStateAsync(string routeId, bool running, CancellationToken cancellation)
    {
        // RequestStop 是非阻塞请求；等待状态变化用异步延时，绝不在 UI 线程调用 Stop/Task.Wait。
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            cancellation.ThrowIfCancellationRequested();
            var state = Workspace.RouteState(routeId);
            if (running && state == ServiceState.Running) return true;
            if (!running && state is ServiceState.Stopped or ServiceState.Error) return true;
            if (running && state is ServiceState.Stopped or ServiceState.Error) return false;
            await Task.Delay(50, cancellation);
        }
        return false;
    }

    private async Task<bool> ConfirmAsync(string title, string body, string primary)
    {
        using var focus = Host.SuspendFocusConstraint();
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        DrawerText.Bind(text, TextBlock.TextProperty, body);
        var dialog = new ContentDialog(_dialogs.GetDialogHostEx())
        {
            Content = text,
            PrimaryButtonAppearance = ControlAppearance.Danger,
            DefaultButton = ContentDialogButton.Close,
            DialogMaxWidth = 440,
        };
        DrawerText.Bind(dialog, ContentDialog.TitleProperty, title);
        DrawerText.Bind(dialog, ContentDialog.PrimaryButtonTextProperty, primary);
        DrawerText.Bind(dialog, ContentDialog.CloseButtonTextProperty, "取消");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void Saved()
    {
        _workspaceService.Flush();
        ShowMessage("保存成功");
    }

    private void ShowMessage(string key) => _snackbar.Show(DrawerText.T("提示"), DrawerText.T(key),
        ControlAppearance.Secondary, new SymbolIcon(SymbolRegular.Info24), TimeSpan.FromSeconds(5));
}
