using CommunityToolkit.Mvvm.Input;
using System;
using Wpf.Ui;

namespace RetryProxy.ViewModel.Pages;

public partial class HomePageViewModel(INavigationService navigationService) : ViewModel
{
    [RelayCommand]
    private void OnNavigate(Type pageType)
    {
        // 共用侧栏导航，例如点击首页“统计”时同步选中统计页，并沿用切页保护。
        navigationService.Navigate(pageType);
    }
}
