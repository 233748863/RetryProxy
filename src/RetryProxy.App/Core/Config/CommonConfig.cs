using CommunityToolkit.Mvvm.ComponentModel;
using RetryProxy.Helpers;
using System;
using Wpf.Ui.Controls;

namespace RetryProxy.Core.Config;

/// <summary>
/// 主题类型配置
/// </summary>
public enum ThemeType
{
    DarkNone,
    DarkMica,
    DarkAcrylic,
    LightNone,
    LightMica,
    LightAcrylic,
}

/// <summary>
/// 通用界面配置
/// </summary>
[Serializable]
public partial class CommonConfig : ObservableObject
{
    /// <summary>
    /// 关闭窗口时最小化至托盘而非退出
    /// </summary>
    [ObservableProperty]
    private bool _exitToTray;

    /// <summary>
    /// 当前主题类型
    /// </summary>
    [ObservableProperty]
    private ThemeType _currentThemeType = OsVersionHelper.IsWindows11_22523_OrGreater ? ThemeType.DarkMica : ThemeType.DarkNone;

    /// <summary>
    /// 当前背景样式（旧字段，仅保留兼容）
    /// </summary>
    [ObservableProperty]
    private WindowBackdropType _currentBackdropType = WindowBackdropType.Mica;

    /// <summary>
    /// 是否首次运行
    /// </summary>
    [ObservableProperty]
    private bool _isFirstRun = true;

    /// <summary>
    /// 上次运行的版本号
    /// </summary>
    [ObservableProperty]
    private string _runForVersion = "";
}
