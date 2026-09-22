using RetryProxy.ViewModel.Pages;
using System.Windows.Controls;

namespace RetryProxy.View.Pages;

public partial class CachePage : Page
{
    public CachePageViewModel ViewModel { get; }

    public CachePage(CachePageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
