using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Service;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RetryProxy.ViewModel.Pages;

/// <summary>关于页的一条依赖致谢。</summary>
public sealed record DependencyItem(string Name, string Note, string Url);

public partial class AboutPageViewModel : ViewModel
{
    public const string RepositoryUrl = "https://github.com/233748863/RetryProxy";
    public const string LicenseUrl = "https://www.gnu.org/licenses/gpl-3.0.html";

    private readonly WorkspaceService _workspaceService;

    [ObservableProperty]
    private string _versionText = $"v{Global.Version}";

    [ObservableProperty]
    private string _summary = "本地 LLM 反向代理：自动重试、等待生成、暂存回复、保活与当日统计。";

    [ObservableProperty]
    private string _licenseText = "本程序以 GNU General Public License v3.0 发布：可自由使用、修改与再分发，再分发时须保留源代码与同一许可证。界面框架与视觉资源来自 BetterGI（GPL-3.0）。";

    public IReadOnlyList<DependencyItem> Dependencies { get; } =
    [
        new("BetterGI", "界面框架、页面样板与视觉资源 · GPL-3.0", "https://github.com/babalae/better-genshin-impact"),
        new("WPF-UI / WPF-UI.Tray / WPF-UI.Violeta", "Fluent 控件、导航、托盘与对话框 · MIT", "https://github.com/lepoco/wpfui"),
        new("CommunityToolkit.Mvvm", "ObservableProperty / RelayCommand 源生成 · MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("ASP.NET Core Kestrel", "本地 HTTP 监听与流式转发 · MIT", "https://github.com/dotnet/aspnetcore"),
        new("Microsoft.Xaml.Behaviors.Wpf", "XAML 事件到命令的绑定 · MIT", "https://github.com/microsoft/XamlBehaviorsWpf"),
        new("Vanara.PInvoke", "Win32 互操作封装 · MIT", "https://github.com/dahall/Vanara"),
        new("Semver", "版本号比较 · MIT", "https://github.com/WalkerCodeRanger/semver"),
        new("LLM Retry Proxy（Rust 版）", "本程序的原型实现，功能与文案逐条对照移植 · GPL-3.0", RepositoryUrl),
    ];

    public AboutPageViewModel(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    [RelayCommand]
    private void OnOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            _workspaceService.Workspace.Notice = $"无法打开浏览器：{url}";
        }
    }
}
