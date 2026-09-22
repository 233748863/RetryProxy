using CommunityToolkit.Mvvm.ComponentModel;
using RetryProxy.Core.Config;
using RetryProxy.Service.Interface;

namespace RetryProxy.ViewModel.Pages;

public partial class CachePageViewModel : ViewModel
{
    public AllConfig Config { get; }

    [ObservableProperty]
    private string _headline = "缓存明细";

    [ObservableProperty]
    private string _description = "请求缓存命中与条目明细。M6 阶段接入真实数据。";

    public CachePageViewModel(IConfigService configService)
    {
        Config = configService.Get();
    }
}
