using RetryProxy.ViewModel.Pages;
using System.Windows.Controls;

namespace RetryProxy.View.Pages;

public partial class ChannelPage : Page
{
    public ChannelPageViewModel ViewModel { get; }

    public ChannelPage(ChannelPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
