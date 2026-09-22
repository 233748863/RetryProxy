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
}
