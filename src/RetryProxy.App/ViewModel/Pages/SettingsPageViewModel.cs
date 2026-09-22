using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Config;
using RetryProxy.Service.I18n;
using RetryProxy.Service.Interface;
using RetryProxy.View.Converters;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace RetryProxy.ViewModel.Pages;

public partial class SettingsPageViewModel : ViewModel
{
    private readonly IConfigService _configService;

    public AllConfig Config { get; }

    /// <summary>
    /// 可选界面语言。zh-Hans 为源文案，其余需存在 User/I18n/{lang}.json。
    /// </summary>
    [ObservableProperty]
    private FrozenDictionary<string, string> _languageDict =
        new[] { "zh-Hans", "en" }.ToFrozenDictionary(c => c, CultureInfoNameToKVPConverter.GetDisplayName);

    /// <summary>
    /// 开机自动启动。M6 接入注册表 Run 项。
    /// </summary>
    [ObservableProperty]
    private bool _autoStartEnabled;

    /// <summary>
    /// 启动时最小化到托盘。M6 接入配置。
    /// </summary>
    [ObservableProperty]
    private bool _startMinimized;

    public SettingsPageViewModel(IConfigService configService)
    {
        _configService = configService;
        Config = configService.Get();
    }

    [RelayCommand]
    private void OnUiLanguageSelectionChanged(object? value)
    {
        if (value is not KeyValuePair<string, string> language)
        {
            return;
        }

        Config.OtherConfig.UiCultureInfoName = language.Key;
        I18nService.Instance.ChangeLanguage(language.Key);
        _configService.Save();
    }
}
