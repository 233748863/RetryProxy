using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using RetryProxy.Core.Metrics;

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

    /// <summary>用户手动修改的美元单价，保存时整体替换并调用 SaveChecked。</summary>
    public List<ModelPriceOverride> StatisticsModelPrices { get; set; } = new();

    /// <summary>主动更新后的公开价目表；缺失时使用随软件附带的目录。</summary>
    public PublicPriceCatalog? StatisticsPublicPrices { get; set; }
}
