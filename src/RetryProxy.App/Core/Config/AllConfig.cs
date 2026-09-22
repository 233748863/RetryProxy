using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Text.Json.Serialization;

namespace RetryProxy.Core.Config;

/// <summary>
/// 全部配置。序列化到 User/config.json。
/// </summary>
[Serializable]
public partial class AllConfig : ObservableObject
{
    /// <summary>
    /// 任意配置变更时触发（用于自动保存）
    /// </summary>
    [JsonIgnore]
    public Action? OnAnyChangedAction { get; set; }

    /// <summary>
    /// 通用界面配置
    /// </summary>
    public CommonConfig CommonConfig { get; set; } = new();

    /// <summary>
    /// 其他配置
    /// </summary>
    public OtherConfig OtherConfig { get; set; } = new();

    /// <summary>
    /// 代理配置
    /// </summary>
    public ProxyConfig Proxy { get; set; } = new();

    public void InitEvent()
    {
        PropertyChanged += OnAnyPropertyChanged;
        CommonConfig.PropertyChanged += OnAnyPropertyChanged;
        OtherConfig.PropertyChanged += OnAnyPropertyChanged;
    }

    public void OnAnyPropertyChanged(object? sender, EventArgs args)
    {
        OnAnyChangedAction?.Invoke();
    }
}
