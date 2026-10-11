using RetryProxy.ViewModel.Pages;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RetryProxy.View.Pages;

/// <summary>最新在前；阅读、选择和展开均暂停跟随，只由用户主动恢复。</summary>
public partial class LogPage : Page
{
    private ScrollViewer? _requestScroll;
    private ScrollViewer? _eventScroll;
    private bool _updating;
    private double _offset;
    private object? _anchor;
    private double _anchorY;
    private int _restoreVersion;
    public LogPageViewModel ViewModel { get; }
    private ScrollViewer? CurrentScroll => ViewModel.IsRequestView ? _requestScroll : _eventScroll;
    private ListBox CurrentList => ViewModel.IsRequestView ? RequestList : LogList;

    public LogPage(LogPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
        SizeChanged += (_, _) => ViewModel.IsCompact = ActualWidth < 620;
        ViewModel.RowsUpdating += RememberPosition;
        ViewModel.RowsUpdated += RestorePosition;
        Loaded += (_, _) => AttachScrollViewers();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LogPageViewModel.IsRequestView))
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AttachScrollViewers);
        };
        foreach (var list in new[] { RequestList, LogList })
        {
            list.PreviewMouseWheel += OnMouseWheel;
            list.PreviewMouseLeftButtonDown += (_, _) => CancelPositionRestore();
            list.PreviewKeyDown += (_, args) =>
            {
                if (args.Key is Key.Down or Key.Up or Key.PageDown or Key.PageUp or Key.End or Key.Home)
                {
                    CancelPositionRestore();
                    ViewModel.PauseFollowing();
                }
            };
        }
    }

    private void AttachScrollViewers()
    {
        _requestScroll ??= Attach(RequestList);
        _eventScroll ??= Attach(LogList);
    }

    private ScrollViewer? Attach(ListBox list)
    {
        list.ApplyTemplate();
        var scroll = FindScrollViewer(list);
        if (scroll is null) return null;
        scroll.Tag = "NoSmoothScroll";
        scroll.ScrollChanged += (_, args) =>
        {
            if (!_updating && scroll == CurrentScroll && args.VerticalChange > 0
                && args.ExtentHeightChange == 0 && args.ViewportHeightChange == 0) ViewModel.PauseFollowing();
        };
        return scroll;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs args)
    {
        CancelPositionRestore();
        if (CurrentScroll is not { } scroll) return;
        var offset = Math.Clamp(scroll.VerticalOffset - args.Delta / 120.0 * SystemParameters.WheelScrollLines * 18, 0, scroll.ScrollableHeight);
        if (offset > 0) ViewModel.PauseFollowing();
        scroll.ScrollToVerticalOffset(offset);
        args.Handled = true;
    }

    private void CancelPositionRestore()
    {
        _restoreVersion++;
        _updating = false;
    }

    private void RememberPosition(bool reset)
    {
        _updating = true;
        _anchor = null;
        _offset = CurrentScroll?.VerticalOffset ?? 0;
        if (reset || ViewModel.AutoScroll || !IsLoaded) return;
        var list = CurrentList;
        list.UpdateLayout();
        _offset = CurrentScroll?.VerticalOffset ?? 0;
        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) continue;
            var y = item.TranslatePoint(new Point(), list).Y;
            if (y + item.ActualHeight <= 0 || y >= list.ActualHeight) continue;
            _anchor = list.Items[index];
            _anchorY = y;
            break;
        }
    }

    private void RestorePosition(bool reset)
    {
        var version = ++_restoreVersion;
        var follow = reset || ViewModel.AutoScroll;
        var offset = follow ? 0 : _offset;
        var list = CurrentList;
        var anchor = follow ? null : _anchor;
        var anchorY = _anchorY;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (version != _restoreVersion) return;
            try
            {
                if (!IsLoaded) return;
                AttachScrollViewers();
                if (list != CurrentList || CurrentScroll is not { } scroll) return;
                list.UpdateLayout();
                if (anchor is not null && list.ItemContainerGenerator.ContainerFromItem(anchor) is ListBoxItem item)
                    offset = scroll.VerticalOffset + item.TranslatePoint(new Point(), list).Y - anchorY;
                if (Math.Abs(scroll.VerticalOffset - offset) > 0.5) scroll.ScrollToVerticalOffset(offset);
            }
            finally { _updating = false; }
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnMoreClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.IsOpen = true; }
    }
}
