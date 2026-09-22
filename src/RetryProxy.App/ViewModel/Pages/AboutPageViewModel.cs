using CommunityToolkit.Mvvm.ComponentModel;
using RetryProxy.Core.Config;
using RetryProxy.Service.Interface;

namespace RetryProxy.ViewModel.Pages;

public partial class AboutPageViewModel : ViewModel
{
    public AllConfig Config { get; }

    [ObservableProperty]
    private string _headline = "关于";

    [ObservableProperty]
    private string _description = "LLM Retry Proxy · 基于 BetterGI 界面框架重构 · GPL-3.0";

    public AboutPageViewModel(IConfigService configService)
    {
        Config = configService.Get();
    }
}
