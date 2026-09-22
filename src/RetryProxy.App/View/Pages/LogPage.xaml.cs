using RetryProxy.ViewModel.Pages;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RetryProxy.View.Pages;

/// <summary>
/// 运行日志页。自动滚动规则：跟随 = 勾选自动滚动 且 没有暂停；手动上滚（滚轮/拖动）且不在底部时暂停，
/// 期间任何输入活动把恢复时刻推后，连续 5 秒无操作或回到底部即恢复；取消勾选清除暂停并保持手动。
/// </summary>
public partial class LogPage : Page
{
    private readonly DispatcherTimer _resumeTimer;
    private ScrollViewer? _scroll;
    private DateTime? _resumeAt;
    private bool _programmaticScroll;

    public LogPageViewModel ViewModel { get; }

    public LogPage(LogPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
        _resumeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _resumeTimer.Tick += (_, _) => CheckResume();
        ViewModel.RowsAppended += OnRowsAppended;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogPageViewModel.AutoScroll))
            {
                OnAutoScrollToggled();
            }
        };
        Loaded += OnLoaded;
        LogList.PreviewMouseWheel += (_, _) => OnManualScroll();
        LogList.PreviewMouseDown += (_, _) => OnActivity();
        LogList.PreviewKeyDown += (_, _) => OnActivity();
    }

    private bool Follow => ViewModel.AutoScroll && _resumeAt is null;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_scroll is null)
        {
            _scroll = FindScrollViewer(LogList);
            if (_scroll is not null)
            {
                // 标记后主窗口的平滑滚动不再接管它，保持像素级虚拟化。
                _scroll.Tag = "NoSmoothScroll";
                _scroll.ScrollChanged += OnScrollChanged;
            }
        }

        if (Follow)
        {
            ScrollToEnd();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private bool AtBottom => _scroll is null || _scroll.VerticalOffset + _scroll.ViewportHeight >= _scroll.ExtentHeight - 1.0;

    private void ScrollToEnd()
    {
        if (_scroll is null)
        {
            return;
        }

        _programmaticScroll = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scroll.ScrollToEnd();
            _programmaticScroll = false;
        });
    }

    private void OnRowsAppended()
    {
        if (Follow)
        {
            ScrollToEnd();
        }
    }

    private void OnManualScroll()
    {
        if (!ViewModel.AutoScroll)
        {
            return;
        }

        // 滚轮事件先于位置变化到达，等布局后再判断是否离开了底部。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!AtBottom)
            {
                Pause();
            }
        });
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ViewModel.AutoScroll || _programmaticScroll)
        {
            return;
        }

        if (AtBottom)
        {
            _resumeAt = null;
            _resumeTimer.Stop();
        }
        else if (e.VerticalChange < 0)
        {
            Pause();
        }
    }

    private void OnActivity()
    {
        if (_resumeAt is not null)
        {
            _resumeAt = DateTime.UtcNow + LogPageViewModel.ScrollResumeDelay;
        }
    }

    private void Pause()
    {
        _resumeAt = DateTime.UtcNow + LogPageViewModel.ScrollResumeDelay;
        if (!_resumeTimer.IsEnabled)
        {
            _resumeTimer.Start();
        }
    }

    private void CheckResume()
    {
        if (_resumeAt is { } deadline && DateTime.UtcNow >= deadline)
        {
            _resumeAt = null;
            _resumeTimer.Stop();
            if (ViewModel.AutoScroll)
            {
                ScrollToEnd();
            }
        }
    }

    private void OnAutoScrollToggled()
    {
        _resumeAt = null;
        _resumeTimer.Stop();
        if (ViewModel.AutoScroll)
        {
            ScrollToEnd();
        }
    }
}
