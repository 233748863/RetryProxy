using CommunityToolkit.Mvvm.Input;
using System;
using Wpf.Ui;

namespace RetryProxy.ViewModel.Pages;

public partial class HomePageViewModel(INavigationService navigationService) : ViewModel
{
    [RelayCommand]
    private void OnNavigate(Type pageType)
    {
        navigationService.Navigate(pageType);
    }
}
