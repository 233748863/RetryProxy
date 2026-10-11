using System.Windows.Controls;
using System.Windows;

namespace RetryProxy.View.Pages;

/// <summary>统计页上半部分；数据与导航生命周期由 StatisticsPage 统一管理。</summary>
public partial class OverviewPage : UserControl
{
    public OverviewPage()
    {
        InitializeComponent();
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
