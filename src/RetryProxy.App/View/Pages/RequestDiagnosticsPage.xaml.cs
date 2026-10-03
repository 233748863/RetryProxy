using RetryProxy.ViewModel.Pages;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RetryProxy.View.Pages;

public partial class RequestDiagnosticsPage : Page
{
    public RequestDiagnosticsPageViewModel ViewModel { get; }
    private RequestDiagnosticRow? _anchor;
    private double _offset;
    private int _anchorIndex;

    public RequestDiagnosticsPage(RequestDiagnosticsPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.RowsUpdating += BeforeRowsUpdate;
        ViewModel.RowsUpdated += AfterRowsUpdate;
        ViewModel.Activate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ViewModel.RowsUpdating -= BeforeRowsUpdate;
        ViewModel.RowsUpdated -= AfterRowsUpdate;
        ViewModel.Deactivate();
    }

    private void BeforeRowsUpdate(bool reset)
    {
        if (FindScrollViewer(RequestList) is not { } scroll) return;
        _offset = scroll.VerticalOffset;
        _anchorIndex = Math.Min((int)_offset, ViewModel.Rows.Count - 1);
        _anchor = _anchorIndex >= 0 ? ViewModel.Rows[_anchorIndex] : null;
    }

    private void AfterRowsUpdate(bool reset)
    {
        if (FindScrollViewer(RequestList) is not { } scroll) return;
        if (reset) { scroll.ScrollToTop(); return; }
        // 集合增量更新后锚定原来的首个可见请求；不调用 Focus/ScrollIntoView。
        var newIndex = _anchor is null ? -1 : ViewModel.Rows.IndexOf(_anchor);
        scroll.ScrollToVerticalOffset(newIndex >= 0 ? Math.Max(0, _offset + newIndex - _anchorIndex) : _offset);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } result) return result;
        return null;
    }
}
