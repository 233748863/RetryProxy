using RetryProxy.ViewModel.Pages;
using System.Windows.Controls;

namespace RetryProxy.View.Pages;

public partial class OverviewPage : Page
{
    public OverviewPageViewModel ViewModel { get; }

    public OverviewPage(OverviewPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
