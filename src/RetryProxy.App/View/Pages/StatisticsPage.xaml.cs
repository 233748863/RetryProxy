using RetryProxy.ViewModel.Pages;
using System.Windows.Controls;

namespace RetryProxy.View.Pages;

public partial class StatisticsPage : Page
{
    public StatisticsPageViewModel ViewModel { get; }

    public StatisticsPage(StatisticsPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
