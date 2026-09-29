using RetryProxy.ViewModel.Pages;
using System.Windows.Controls;
using System.Windows;

namespace RetryProxy.View.Pages;

public partial class PreparationPage : Page
{
    public PreparationPageViewModel ViewModel { get; }

    public PreparationPage(PreparationPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
    // 显式打开 ContextMenu，使鼠标、键盘和 UI Automation 的 Invoke 使用同一入口。
    private void OnTaskMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
