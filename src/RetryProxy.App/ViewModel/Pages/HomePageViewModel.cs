using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RetryProxy.Core.Config;
using RetryProxy.Service.Interface;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetryProxy.ViewModel.Pages;

public partial class HomePageViewModel : ViewModel
{
    private const string DefaultBannerImagePath = "pack://application:,,,/Resources/Images/banner.jpg";

    private readonly ILogger<HomePageViewModel> _logger;

    public AllConfig Config { get; }

    [ObservableProperty]
    private ImageSource? _bannerImageSource;

    [ObservableProperty]
    private bool _isProxyRunning;

    [ObservableProperty]
    private ObservableCollection<string> _providerNames = [];

    [ObservableProperty]
    private string? _selectedProvider;

    [ObservableProperty]
    private ObservableCollection<string> _channelNames = [];

    [ObservableProperty]
    private string? _selectedChannel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenAddress))]
    private int _listenPort = ConfigDefaults.ListenPort;

    [ObservableProperty]
    private bool _keepAliveEnabled;

    public string ListenAddress => $"http://127.0.0.1:{ListenPort}";

    public HomePageViewModel(ILogger<HomePageViewModel> logger, IConfigService configService)
    {
        _logger = logger;
        Config = configService.Get();
        LoadFromProxyConfig();
    }

    /// <summary>
    /// 用当前代理配置填充下拉框与端口。M4/M5 接入编辑与启停后再改为双向。
    /// </summary>
    private void LoadFromProxyConfig()
    {
        var proxy = Config.Proxy ?? ProxyConfig.Builtin();
        ProviderNames = new ObservableCollection<string>(proxy.Providers.Select(provider =>
        {
            var routeCount = proxy.Routes.Count(route =>
                string.Equals(route.ProviderName, provider.Name, StringComparison.OrdinalIgnoreCase));
            return $"{provider.Name} · {provider.BaseUrl} · {routeCount} 通道";
        }));
        ChannelNames = new ObservableCollection<string>(proxy.Routes.Select(route =>
            $"{route.Name} · {route.ListenPort} · 已停止"));

        var selectedRoute = proxy.SelectedRoute;
        var selectedProvider = selectedRoute is null ? null : proxy.ProviderByName(selectedRoute.ProviderName);
        SelectedProvider = selectedProvider is null
            ? ProviderNames.FirstOrDefault()
            : ProviderNames.FirstOrDefault(name => name.StartsWith($"{selectedProvider.Name} · ", StringComparison.Ordinal));
        SelectedChannel = selectedRoute is null
            ? ChannelNames.FirstOrDefault()
            : ChannelNames.FirstOrDefault(name => name.StartsWith($"{selectedRoute.Name} · ", StringComparison.Ordinal));
        ListenPort = proxy.ListenPort;
        KeepAliveEnabled = proxy.KeepaliveEnabled;
    }

    [RelayCommand]
    private void OnLoaded()
    {
        if (BannerImageSource is not null)
        {
            return;
        }

        try
        {
            BannerImageSource = new BitmapImage(new Uri(DefaultBannerImagePath, UriKind.Absolute));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "加载首页横幅图失败");
        }
    }

    [RelayCommand]
    private void OnStartProxy()
    {
        // M2 接入 Core 代理服务
        IsProxyRunning = true;
        _logger.LogInformation("代理服务启动（占位）");
    }

    [RelayCommand]
    private void OnStopProxy()
    {
        IsProxyRunning = false;
        _logger.LogInformation("代理服务停止（占位）");
    }

    [RelayCommand]
    private void OnPrepareKeepAlive()
    {
        _logger.LogInformation("一键准备（占位）");
    }

    [RelayCommand]
    private void OnCopyListenAddress()
    {
        Clipboard.SetText(ListenAddress);
    }

    [RelayCommand]
    private void OnGoToWikiUrl()
    {
        var docs = Global.Absolute("docs");
        if (Directory.Exists(docs))
        {
            Process.Start(new ProcessStartInfo(docs) { UseShellExecute = true });
        }
    }
}
