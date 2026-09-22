using RetryProxy.ViewModel.Pages;
using System.Windows.Controls;

namespace RetryProxy.View.Pages;

public partial class StatusPage : Page
{
    public StatusPageViewModel ViewModel { get; }

    public StatusPage(StatusPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
