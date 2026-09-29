using RetryProxy.Core.Workspace;
using RetryProxy.Service.I18n;
using System.Globalization;

namespace RetryProxy.ViewModel.Pages;

/// <summary>统一转发导航通知，避免合并后丢失概况倒计时与缓存筛选复位。</summary>
public sealed class StatisticsPageViewModel : ViewModel
{
    public OverviewPageViewModel Overview { get; }
    public CachePageViewModel Cache { get; }

    public StatisticsPageViewModel(OverviewPageViewModel overview, CachePageViewModel cache)
    {
        Overview = overview;
        Cache = cache;
    }

    public override void OnNavigatedTo()
    {
        Overview.OnNavigatedTo();
        Cache.OnNavigatedTo();
    }

    public override void OnNavigatedFrom()
    {
        Overview.OnNavigatedFrom();
        Cache.OnNavigatedFrom();
    }
}

/// <summary>客户端页面的动态文案；先翻译模板，再填入名称或数字，密钥内容不进入界面文案。</summary>
internal static class ClientPageText
{
    public static string Translate(string key, params object[] arguments)
    {
        var template = I18nService.Instance.Translate(key);
        return arguments.Length == 0 ? template : string.Format(CultureInfo.CurrentUICulture, template, arguments);
    }

    public static string CurrentProviderKey(ProxyWorkspace workspace)
    {
        var route = workspace.SelectedRouteRef();
        var provider = route is null ? null : workspace.Config.ProviderById(route.CurrentProviderId);
        if (provider is null)
        {
            return Translate("未选择供应商");
        }

        var key = provider.KeyById(route!.CurrentKeyId);
        return Translate("{0} · {1}", provider.Name, key?.Name ?? Translate("未选择 Key"));
    }
}
