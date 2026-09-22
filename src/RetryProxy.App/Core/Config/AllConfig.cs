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
    /// 代理配置（schema 6，snake_case 固定键序）。文件里没有该节点时为 null，
    /// 由 ConfigService 按"注入 → 文件 → 注册表 → 内置"顺序补齐。
    /// 它不是 ObservableObject；改动后需显式调用 IConfigService.Save()。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProxyConfig? Proxy { get; set; }

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
