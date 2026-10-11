using System.Windows.Controls;
using System.Windows;

namespace RetryProxy.View.Pages;

/// <summary>统计页上半部分；数据与导航生命周期由 StatisticsPage 统一管理。</summary>
public partial class OverviewPage : UserControl
{
    private bool _followRecentWeeks = true;

    public OverviewPage()
    {
        InitializeComponent();
    }

    private bool IsAtRecentWeeks => HeatmapScroll.ScrollableWidth - HeatmapScroll.HorizontalOffset < 0.5;

    private void OnHeatmapLoaded(object sender, RoutedEventArgs e)
    {
        if (_followRecentWeeks) HeatmapScroll.ScrollToRightEnd();
    }

    private void OnHeatmapScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, HeatmapScroll)) return;

        // 布局变化时仅跟随原本正在查看的最近日期；刷新不得拉走手动查看历史的位置。
        if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0)
        {
            if (_followRecentWeeks) HeatmapScroll.ScrollToRightEnd();
        }
        else if (e.HorizontalChange != 0)
        {
            _followRecentWeeks = IsAtRecentWeeks;
        }
        HeatmapRecentButton.IsEnabled = !IsAtRecentWeeks;
    }

    private void OnHeatmapRecentClick(object sender, RoutedEventArgs e)
    {
        _followRecentWeeks = true;
        HeatmapScroll.ScrollToRightEnd();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = ActualWidth;
        KpiGrid.Columns = width >= 780 ? 4 : width >= 390 ? 2 : 1;
        FilterGrid.Columns = width >= 1060 ? 6 : width >= 570 ? 3 : 2;
        var wide = width >= 920;
        TrendGrid.ColumnDefinitions[0].Width = new GridLength(wide ? 2.2 : 1, GridUnitType.Star);
        TrendGrid.ColumnDefinitions[1].Width = new GridLength(wide ? 1 : 0, GridUnitType.Star);
        Grid.SetColumn(RankCard, wide ? 1 : 0);
        Grid.SetRow(RankCard, wide ? 0 : 1);
        TrendCard.Margin = wide ? new Thickness(0, 0, 12, 0) : new Thickness(0, 0, 0, 12);
    }
}
