using RetryProxy.ViewModel.Pages;
using System.Windows.Controls;

namespace RetryProxy.View.Pages;

public partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; }

    public SettingsPage(SettingsPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
