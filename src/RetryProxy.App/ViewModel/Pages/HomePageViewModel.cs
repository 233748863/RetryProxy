using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RetryProxy.Core.Config;
using RetryProxy.Service.Interface;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    private ObservableCollection<string> _providerNames = ["示例服务商 · https://api.example.com · 1 通道"];

    [ObservableProperty]
    private string? _selectedProvider;

    [ObservableProperty]
    private ObservableCollection<string> _channelNames = ["默认通道 · 8080 · 已停止"];

    [ObservableProperty]
    private string? _selectedChannel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenAddress))]
    private int _listenPort = 8080;

    [ObservableProperty]
    private bool _keepAliveEnabled;

    public string ListenAddress => $"http://127.0.0.1:{ListenPort}";

    public HomePageViewModel(ILogger<HomePageViewModel> logger, IConfigService configService)
    {
        _logger = logger;
        Config = configService.Get();
        ListenPort = Config.Proxy.BasePort;
        SelectedProvider = ProviderNames.Count > 0 ? ProviderNames[0] : null;
        SelectedChannel = ChannelNames.Count > 0 ? ChannelNames[0] : null;
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
