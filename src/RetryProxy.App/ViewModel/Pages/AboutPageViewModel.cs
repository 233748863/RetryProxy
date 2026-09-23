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
    private readonly WorkspaceService _workspaceService;

    [ObservableProperty]
    private string _versionText = $"v{Global.Version}";

    [ObservableProperty]
    private string _summary = "本地 LLM 反向代理：自动重试、等待生成、暂存回复、保活与当日统计。";

    public IReadOnlyList<DependencyItem> Dependencies { get; } =
    [
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
