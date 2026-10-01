using RetryProxy.Core.Client;
using RetryProxy.Core.Config;
using RetryProxy.Core.Service;
using RetryProxy.Service.Interface;
using RetryProxy.View.Drawers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RetryProxy.Service;

/// <summary>接管确认与三步向导。配置实际读写均经 Core 的事务入口，注入模式禁止进入。</summary>
public sealed class ClientTakeoverService(WorkspaceService service, IConfigService configService, IContentDialogService dialogs)
{
    private bool _showing;
    private static readonly ClientType[] Clients = [ClientType.Claude, ClientType.Codex];
    private static string T(string text) => DrawerText.T(text);
    public string BackupRoot => Global.Absolute("User/backup/client");

    public async Task TakeOverAsync(ClientType client)
    {
        if (_showing) return;
        if (ClientConfigPaths.WritesBlocked) { Notice("测试注入模式禁止修改客户端配置"); return; }
        _showing = true;
        try
        {
            var panel = new StackPanel();
            AddTakeoverPreview(panel, client);
            if (await ShowAsync("确认接管客户端", panel, "接管", "取消") != ContentDialogResult.Primary) return;
            if (await TakeOverConfirmedAsync(client) is { } error) Notice(error);
            else Notice("客户端配置已接管；首次接管后请重开已打开的客户端窗口");
        }
        catch (ClientConfigException error) { Notice(error.Message); }
        finally { _showing = false; service.Flush(); }
    }

    public async Task CancelTakeoverAsync(ClientType client)
    {
        if (_showing) return;
        _showing = true;
        try
        {
            var panel = new StackPanel();
            panel.Children.Add(Text("取消后，客户端将直连当前 Key，之后启动本软件不再自动接管。已打开的客户端窗口需要重新打开。"));
            if (await ShowAsync("取消接管", panel, "确认取消接管", "返回", danger: true) != ContentDialogResult.Primary) return;
            Notice(service.Clients.CancelTakeover(client) ?? "已取消接管，客户端已恢复直连");
        }
        finally { _showing = false; service.Flush(); }
        service.Clients.Detect();
    }

    private async Task<string?> TakeOverConfirmedAsync(ClientType client)
    {
        var route = service.Workspace.Config.RouteFor(client)!;
        if (service.Workspace.Config.CurrentKeyOf(route) is null) return "请先为当前客户端添加并选择 Key";
        if (service.Workspace.RouteState(route.Id) is ServiceState.Starting or ServiceState.Stopping)
            return "代理正在启动或停止，请稍后再试";
        if (service.Workspace.RouteState(route.Id) != ServiceState.Running)
        {
            service.Workspace.StartRoute(route.Id);
            var timer = Stopwatch.StartNew();
            while (service.Workspace.RouteState(route.Id) == ServiceState.Starting && timer.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(50);
            if (service.Workspace.RouteState(route.Id) != ServiceState.Running) return "代理启动失败，客户端配置未修改";
        }
        return service.Clients.TakeOver(client);
    }

    public async Task ShowSetupAsync(bool onlyIfNeeded = false)
    {
        if (_showing || ClientConfigPaths.WritesBlocked || onlyIfNeeded && configService.Get().CommonConfig.ClientSetupCompleted) return;
        if (onlyIfNeeded && Application.Current.MainWindow?.IsVisible != true) return;
        _showing = true;
        try
        {
            var entries = ReadEntries();
            var step = 0;
            string? error = null;
            while (step < 3)
            {
                var panel = new StackPanel();
                if (error is not null) panel.Children.Add(Text(DrawerText.Error(error), literal: true));
                if (step == 0)
                {
                    panel.Children.Add(Text("先把客户端原来的地址、Key 和模型保存为供应商，再决定是否接管。密钥不会在这里完整显示。"));
                    foreach (var entry in entries)
                    {
                        panel.Children.Add(Text(entry.Client.Label(), literal: true, heading: true));
                        panel.Children.Add(Text(entry.Message));
                        if (entry.CanImport && !entry.Imported)
                        {
                            panel.Children.Add(Text(entry.Profile!.BaseUrl, literal: true));
                            var masked = new ProviderKey { ApiKey = entry.Profile.ApiKey }.MaskedKey;
                            panel.Children.Add(Text(string.Format(T("密钥：{0} · 模型：{1}"), masked, entry.Profile.Models.Model), literal: true));
                            entry.Selected = new CheckBox { Content = T("保存现有配置"), IsChecked = true, Margin = new(0,4,0,6) };
                            AutomationProperties.SetAutomationId(entry.Selected, "ImportClient_" + entry.Client.AsStr());
                            entry.Name = new TextBox { Text = entry.SuggestedName, IsEnabled = entry.Rename, Margin = new(0,0,0,8) };
                            AutomationProperties.SetAutomationId(entry.Name, "ImportName_" + entry.Client.AsStr());
                            panel.Children.Add(entry.Selected);
                            panel.Children.Add(entry.Name);
                        }
                    }
                }
                else if (step == 1)
                {
                    panel.Children.Add(Text("接管会先备份并修改以下配置；首次接管后请重开旧窗口，之后在本机代理内换 Key 无需重开。"));
                    foreach (var entry in entries)
                    {
                        var readable = AddTakeoverPreview(panel, entry.Client);
                        var route = service.Workspace.Config.RouteFor(entry.Client)!;
                        var available = readable && service.Workspace.Config.CurrentKeyOf(route) is not null;
                        entry.Selected = new CheckBox
                        {
                            Content = T("接管此客户端"), IsChecked = available,
                            IsEnabled = available, Margin = new(0,4,0,8),
                        };
                        AutomationProperties.SetAutomationId(entry.Selected, "TakeoverClient_" + entry.Client.AsStr());
                        panel.Children.Add(entry.Selected);
                    }
                }
                else
                {
                    panel.Children.Add(Text("设置已完成；跳过的事项可以在供应商页或软件设置中继续。"));
                    foreach (var client in Clients)
                    {
                        var route = service.Workspace.Config.RouteFor(client)!;
                        panel.Children.Add(Text(client.Label(), literal: true, heading: true));
                        panel.Children.Add(Text(CurrentLabel(client), literal: true));
                        panel.Children.Add(Text((route.ClientType == ClientType.Codex ? route.LocalUrl + "/v1" : route.LocalUrl), literal: true));
                        panel.Children.Add(Text(StatusText(service.Clients.Connection(client).Status)));
                    }
                    panel.Children.Add(Text("首次接管后请重开 Claude Code 和 Codex 的旧窗口；已连接本机代理的窗口会一起切换供应商。"));
                }
                var title = step switch { 0 => "首次设置 · 1/3 保存现有配置", 1 => "首次设置 · 2/3 接管", _ => "首次设置 · 3/3 完成" };
                var result = await ShowAsync(title, panel, step == 2 ? "完成" : "确认并继续", "跳过此步");
                error = null;
                if (result == ContentDialogResult.Primary && step == 0)
                {
                    foreach (var entry in entries)
                    {
                        if (!entry.CanImport || entry.Imported || entry.Selected?.IsChecked != true) continue;
                        if (service.Clients.Store(entry.Client).CurrentHash() != entry.Hash)
                        { error = T("客户端配置已变化，请重新打开向导后确认"); break; }
                        error = service.Workspace.ImportClientProvider(entry.Profile!, entry.Name!.Text);
                        if (error is not null) break;
                        entry.Imported = true;
                        entry.Message = "原配置已保存为供应商";
                    }
                }
                else if (result == ContentDialogResult.Primary && step == 1)
                {
                    foreach (var entry in entries)
                    {
                        if (entry.Selected?.IsChecked != true) continue;
                        error = await TakeOverConfirmedAsync(entry.Client);
                        if (error is not null) break;
                    }
                }
                if (error is null) step++;
                service.Flush();
            }
            var common = configService.Get().CommonConfig;
            var previous = common.ClientSetupCompleted;
            common.ClientSetupCompleted = true;
            try { configService.SaveChecked(); }
            catch (Exception) { common.ClientSetupCompleted = previous; Notice("向导进度保存失败，下次启动会再次显示"); }
        }
        catch (ClientConfigException error) { Notice(error.Message); }
        finally { _showing = false; service.Clients.Detect(); service.Flush(); }
    }

    private List<SetupEntry> ReadEntries()
    {
        var entries = new List<SetupEntry>();
        foreach (var client in Clients)
        {
            try
            {
                var store = service.Clients.Store(client);
                var hash = store.CurrentHash();
                var profile = store.ReadProfile();
                var preview = ClientImport.Preview(profile, service.Workspace.Config, client);
                entries.Add(new() { Client = client, Profile = profile, CanImport = preview.CanImport, Hash = hash,
                    Message = preview.Message, SuggestedName = preview.SuggestedName, Rename = preview.Kind == ClientImportKind.External });
            }
            catch (ClientConfigException error) { entries.Add(new() { Client = client, Message = error.Message }); }
        }
        return entries;
    }

    private bool AddTakeoverPreview(StackPanel panel, ClientType client)
    {
        panel.Children.Add(Text(client.Label(), literal: true, heading: true));
        panel.Children.Add(Text(CurrentLabel(client), literal: true));
        try
        {
            var store = service.Clients.Store(client);
            panel.Children.Add(Text(string.Format(T("将修改：{0}"), store.ConfigPath), literal: true));
            panel.Children.Add(Text(string.Format(T("原文件备份至：{0}"), Path.Combine(BackupRoot, client.AsStr())), literal: true));
            var profile = store.ReadProfile();
            if (client == ClientType.Codex && (profile.ProviderName.Length == 0 || profile.ProviderName == "openai"))
                panel.Children.Add(Text("将使用 retry_proxy 供应商名称，Codex 历史会话列表会按新名称分组。"));
            panel.Children.Add(Text("配置和备份含明文 Key，请妥善保管。"));
            panel.Children.Add(Text("本机代理统一使用当前 Key；本机其他程序也能使用该 Key。"));
            return true;
        }
        catch (ClientConfigException error)
        {
            panel.Children.Add(Text(error.Message));
            return false;
        }
    }

    private string CurrentLabel(ClientType client)
    {
        var route = service.Workspace.Config.RouteFor(client)!;
        var provider = service.Workspace.Config.ProviderById(route.CurrentProviderId);
        var key = service.Workspace.Config.CurrentKeyOf(route);
        return provider is null || key is null ? T("请先为当前客户端添加并选择 Key") : provider.Name + " · " + key.Name;
    }

    public static string StatusText(ClientConnectionStatus status) => status switch
    {
        ClientConnectionStatus.TakenOver => "已接管", ClientConnectionStatus.Modified => "配置被改动",
        ClientConnectionStatus.Unavailable => "无法读取或同步配置", _ => "直连",
    };

    private async Task<ContentDialogResult> ShowAsync(string title, StackPanel panel, string primary, string close, bool danger = false)
    {
        var dialog = new ContentDialog(dialogs.GetDialogHostEx())
        {
            Title = T(title), Content = panel, PrimaryButtonText = T(primary), CloseButtonText = T(close),
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            DefaultButton = ContentDialogButton.Close, DialogMaxWidth = 520,
        };
        return await dialog.ShowAsync();
    }

    private static TextBlock Text(string text, bool literal = false, bool heading = false) => new()
    {
        Text = literal ? text : T(text), TextWrapping = TextWrapping.Wrap, MaxWidth = 450,
        Margin = new(0, heading ? 14 : 3, 0, 3), FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
    };
    private void Notice(string message) => service.Workspace.Notice = message;

    private sealed class SetupEntry
    {
        public ClientType Client { get; init; }
        public ClientProfile? Profile { get; init; }
        public bool CanImport { get; init; }
        public bool Rename { get; init; }
        public string Hash { get; init; } = string.Empty;
        public string SuggestedName { get; init; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool Imported { get; set; }
        public CheckBox? Selected { get; set; }
        public TextBox? Name { get; set; }
    }
}
