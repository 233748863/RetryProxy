using RetryProxy.ViewModel.Pages;
using System.Windows.Controls;

namespace RetryProxy.View.Pages;

public partial class PreparationPage : Page
{
    public PreparationPageViewModel ViewModel { get; }

    public PreparationPage(PreparationPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
