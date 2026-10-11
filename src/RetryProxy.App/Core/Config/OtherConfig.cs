using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace RetryProxy.Core.Config;

[Serializable]
public partial class OtherConfig : ObservableObject
{
    /// <summary>
    /// 界面语言
    /// </summary>
    [ObservableProperty]
    private string _uiCultureInfoName = "zh-Hans";
    /// <summary>运行日志保留的本机日期数，含今天；旧配置默认 7 天。</summary>
    [ObservableProperty]
    private int _runtimeLogRetentionDays = 7;
}
