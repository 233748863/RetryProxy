using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RetryProxy.View.Behavior;

/// <summary>让托盘子菜单紧贴所属条目，鼠标可直接横移进入 Key 列表。</summary>
public static class TraySubmenuPlacement
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TraySubmenuPlacement), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        if ((bool)e.NewValue)
        {
            item.Loaded += OnLoaded;
            item.SubmenuOpened += OnSubmenuOpened;
            if (item.IsLoaded) Configure(item);
        }
        else
        {
            item.Loaded -= OnLoaded;
            item.SubmenuOpened -= OnSubmenuOpened;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Configure((MenuItem)sender);

    private static void OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        // 打开时再次取得模板，兼容菜单重开和主题切换后的模板重建。
        if (ReferenceEquals(sender, e.OriginalSource)) Configure((MenuItem)sender);
    }

    private static void Configure(MenuItem item)
    {
        item.ApplyTemplate();
        if (item.Template?.FindName("Popup", item) is not Popup popup ||
            item.Template.FindName("SubmenuBorder", item) is not Border border) return;

        // WPF-UI 的阴影留白为 12,10,12,30，且向上偏移 20；靠屏幕底部时会把
        // Key 列表推到父条目上方。去掉透明间隙，例如从 Codex 横移时不必经过上一行。
        border.Margin = new Thickness(0);
        border.Effect = null;
        // 父条目的高亮边框还有 4px 外边距；透明背景使这段可命中，避免横移时
        // 鼠标先落到 ContextMenu 上，导致 WPF 清除当前子菜单选择。
        if (VisualTreeHelper.GetChildrenCount(item) > 0 && VisualTreeHelper.GetChild(item, 0) is Panel panel)
            panel.Background = Brushes.Transparent;
        popup.PlacementTarget = item;
        popup.HorizontalOffset = 0;
        popup.VerticalOffset = 0;
        popup.CustomPopupPlacementCallback = Place;
        popup.Placement = PlacementMode.Custom;
    }

    private static CustomPopupPlacement[] Place(Size popupSize, Size targetSize, Point offset)
    {
        // 优先右侧齐顶；右侧或底部放不下时，选择左侧或齐底位置。
        // 四个候选均与父条目接壤，交给 WPF 按显示器可见面积选择并处理 DPI。
        return
        [
            new(new Point(targetSize.Width, 0), PopupPrimaryAxis.Vertical),
            new(new Point(-popupSize.Width, 0), PopupPrimaryAxis.Vertical),
            new(new Point(targetSize.Width, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical),
            new(new Point(-popupSize.Width, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical),
        ];
    }
}
