using RetryProxy.ViewModel.Pages;
using System.Windows.Controls;

namespace RetryProxy.View.Pages;

public partial class AboutPage : Page
{
    public AboutPageViewModel ViewModel { get; }

    public AboutPage(AboutPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
