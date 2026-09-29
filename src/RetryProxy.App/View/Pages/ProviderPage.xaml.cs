using RetryProxy.ViewModel.Pages;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RetryProxy.View.Pages;

public partial class ProviderPage : Page
{
    public ProviderPageViewModel ViewModel { get; }
    private Point _dragStart;
    private sealed record ProviderDrag(string Id);
    private sealed record KeyDrag(string ProviderId, string Id);

    public ProviderPage(ProviderPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
        ViewModel.LocateRequested += id =>
        {
            UpdateLayout();
            foreach (var item in ProviderList.Items)
                if (item is ProviderCardViewModel { Id: var providerId } && providerId == id)
                    (ProviderList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement)?.BringIntoView();
        };
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);
    private bool CanDrag(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        return e.LeftButton == MouseButtonState.Pressed &&
            (Math.Abs(point.X - _dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(point.Y - _dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance);
    }
    private void OnProviderDrag(object sender, MouseEventArgs e)
    {
        if (CanDrag(e) && sender is FrameworkElement { DataContext: ProviderCardViewModel card } handle)
            DragDrop.DoDragDrop(handle, new ProviderDrag(card.Id), DragDropEffects.Move);
    }
    private void OnKeyDrag(object sender, MouseEventArgs e)
    {
        if (CanDrag(e) && sender is FrameworkElement { DataContext: ProviderKeyRowViewModel key } handle)
            DragDrop.DoDragDrop(handle, new KeyDrag(key.ProviderId, key.Id), DragDropEffects.Move);
    }
    private void OnProviderDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ProviderDrag)) is ProviderDrag dragged && sender is FrameworkElement { DataContext: ProviderCardViewModel target })
            ViewModel.MoveProvider(dragged.Id, target.Id);
        e.Handled = true;
    }
    private void OnKeyDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(KeyDrag)) is KeyDrag dragged && sender is FrameworkElement { DataContext: ProviderKeyRowViewModel target }
            && target.ProviderId == dragged.ProviderId)
        {
            ViewModel.MoveKey(target.ProviderId, dragged.Id, target.Id);
            e.Handled = true;
        }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        var y = e.GetPosition(PageScroll).Y;
        if (y < 36) PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - 12);
        else if (y > PageScroll.ActualHeight - 36) PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset + 12);
        e.Effects = e.Data.GetDataPresent(typeof(ProviderDrag)) || e.Data.GetDataPresent(typeof(KeyDrag)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
}
