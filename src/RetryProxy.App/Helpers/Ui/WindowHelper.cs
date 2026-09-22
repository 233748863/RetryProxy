using RetryProxy.Core.Config;
using RetryProxy.Service.Interface;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace RetryProxy.Helpers.Ui;

public class WindowHelper
{
    private const uint DesktopCompositionDisabledHResult = 0x80263001;

    public static void TryApplySystemBackdrop(System.Windows.Window window)
    {
        var themeType = App.GetService<IConfigService>()?.Get().CommonConfig.CurrentThemeType
                        ?? ThemeType.DarkNone;
        ApplyThemeToWindow(window, themeType);
    }

    /// <summary>
    /// 根据主题类型应用主题到指定窗口
    /// </summary>
    public static void ApplyThemeToWindow(System.Windows.Window window, ThemeType themeType)
    {
        try
        {
            ApplyThemeCore(window, themeType);
        }
        catch (COMException ex) when ((uint)ex.HResult == DesktopCompositionDisabledHResult)
        {
            ApplyFallbackTheme(window, themeType);
        }
        catch
        {
            ApplyFallbackTheme(window, themeType);
        }
    }

    private static void ApplyThemeCore(System.Windows.Window window, ThemeType themeType)
    {
        switch (themeType)
        {
            case ThemeType.DarkNone:
                window.Background = new SolidColorBrush(Color.FromArgb(255, 32, 32, 32));
                WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.None);
                break;

            case ThemeType.LightNone:
                window.Background = new SolidColorBrush(Color.FromArgb(255, 243, 243, 243));
                WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.None);
                break;

            case ThemeType.DarkMica:
            case ThemeType.LightMica:
                window.Background = new SolidColorBrush(Colors.Transparent);
                WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.Mica);
                break;

            case ThemeType.DarkAcrylic:
                window.Background = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0));
                WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.Acrylic);
                break;

            case ThemeType.LightAcrylic:
                window.Background = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
                WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.Acrylic);
                break;

            default:
                window.Background = new SolidColorBrush(Colors.Transparent);
                WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.Mica);
                break;
        }
    }

    private static void ApplyFallbackTheme(System.Windows.Window window, ThemeType themeType)
    {
        window.Background = new SolidColorBrush(GetFallbackBackgroundColor(themeType));
        WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.None);
    }

    private static Color GetFallbackBackgroundColor(ThemeType themeType)
    {
        return themeType switch
        {
            ThemeType.LightNone => Color.FromArgb(255, 243, 243, 243),
            ThemeType.LightMica => Color.FromArgb(255, 243, 243, 243),
            ThemeType.LightAcrylic => Color.FromArgb(255, 243, 243, 243),
            _ => Color.FromArgb(255, 32, 32, 32)
        };
    }

    /// <summary>
    /// 按父窗口当前可见区域居中。尚未 Loaded 时会在 Loaded 后再执行。
    /// </summary>
    public static void CenterOnVisibleOwner(System.Windows.Window window)
    {
        if (!window.IsLoaded)
        {
            window.Loaded += (_, _) => CenterOnVisibleOwner(window);
            return;
        }

        var owner = window.Owner ?? System.Windows.Application.Current?.MainWindow;
        if (owner is null || !owner.IsVisible || owner.WindowState == System.Windows.WindowState.Minimized)
        {
            return;
        }

        if (System.Windows.PresentationSource.FromVisual(owner)?.CompositionTarget is not { } ct)
        {
            return;
        }

        var origin = ct.TransformFromDevice.Transform(owner.PointToScreen(default));
        window.Left = origin.X + (owner.ActualWidth - window.ActualWidth) / 2;
        window.Top = origin.Y + (owner.ActualHeight - window.ActualHeight) / 2;
    }
}
