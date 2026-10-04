using System.Windows.Controls;

namespace RetryProxy.View.Pages;

/// <summary>统计页"在途"标签；数据与导航生命周期由 StatisticsPage 统一管理。</summary>
public partial class ActiveRequestsPage : UserControl
{
    public ActiveRequestsPage()
    {
        InitializeComponent();
    }
}
