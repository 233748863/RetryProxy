using RetryProxy.View.Drawers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace RetryProxy.View.Controls;

/// <summary>仅覆盖宿主内容区的右侧抽屉；每层保留自己的草稿、滚动位置和返回焦点。</summary>
public partial class DrawerHost : UserControl
{
    private sealed class Entry(DrawerPage page, IInputElement? previousFocus, double previousScroll)
    {
        public DrawerPage Page { get; } = page;
        public IInputElement? PreviousFocus { get; } = previousFocus;
        public double PreviousScroll { get; } = previousScroll;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Stack<Entry> _entries = new();
    private Window? _window;
    private bool _transitioning;
    private bool _saving;
    private bool _closing;
    private int _focusSuspensions;
    private bool _redirectingFocus;

    public DrawerHost()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateWidth();
        Unloaded += OnUnloaded;
    }

    public bool IsOpen => _entries.Count > 0;
    public Func<DependencyObject?, bool>? AllowsOverlayFocus { get; set; }
    public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

    /// <summary>完成保存返回 true，取消返回 false。嵌套显示时，父抽屉仍留在栈中。</summary>
    public async Task<bool> ShowAsync(DrawerPage page)
    {
        Dispatcher.VerifyAccess();
        if (_transitioning || _saving || _closing)
            throw new InvalidOperationException(DrawerText.T("抽屉正在切换，请稍后再试"));
        var entry = new Entry(page, Keyboard.FocusedElement, BodyScroll.VerticalOffset);
        _entries.Push(entry);
        page.StateChanged += RefreshChrome;
        if (_entries.Count == 1)
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.PreviewKeyDown += OnWindowKeyDown;
                _window.PreviewGotKeyboardFocus += OnWindowGotKeyboardFocus;
            }
        }
        Body.Content = page;
        Visibility = Visibility.Visible;
        // 首次打开时宿主此前为 Collapsed，先取得内容区实际宽度，避免从 0 像素开始动画。
        UpdateLayout();
        UpdateWidth();
        BodyScroll.ScrollToTop();
        RefreshChrome();
        await AnimateAsync(true);
        FocusFirst();
        return await entry.Completion.Task;
    }

    /// <summary>ContentDialog 显示期间让出焦点约束，确认关闭后重新约束抽屉。</summary>
    public IDisposable SuspendFocusConstraint()
    {
        Dispatcher.VerifyAccess();
        _focusSuspensions++;
        return new FocusLease(this);
    }

    private sealed class FocusLease(DrawerHost owner) : IDisposable
    {
        private DrawerHost? _owner = owner;
        public void Dispose()
        {
            if (_owner is not { } host) return;
            _owner = null;
            host._focusSuspensions--;
            if (host.IsOpen && host._focusSuspensions == 0)
                host.Dispatcher.BeginInvoke(DispatcherPriority.Input, host.EnsureFocus);
        }
    }

    public async Task RequestCloseAsync() => await TryCloseAsync(closeAll: false);

    /// <summary>
    /// 切页前一次确认并关闭全部层级。例如在供应商里编辑 Key 时，父子草稿一起检查；
    /// 用户取消确认则不弹出任何一层，原输入、滚动位置与后台操作全部保留。
    /// </summary>
    public Task<bool> TryCloseAllAsync() => TryCloseAsync(closeAll: true);

    private async Task<bool> TryCloseAsync(bool closeAll)
    {
        Dispatcher.VerifyAccess();
        if (!IsOpen) return true;
        if (_transitioning || _saving || _closing || _focusSuspensions > 0) return false;
        var entries = closeAll ? _entries.ToArray() : [_entries.Peek()];
        if (entries.Any(entry => entry.Page.IsBusy && !entry.Page.IsReadOnly)) return false;
        _closing = true;
        RefreshChrome();
        try
        {
            if (entries.Any(entry => !entry.Page.IsReadOnly && entry.Page.HasChanges))
            {
                try
                {
                    if (ConfirmDiscardAsync is null || !await ConfirmDiscardAsync()) return false;
                }
                catch (Exception)
                {
                    entries[0].Page.SetError("无法显示确认，请稍后重试");
                    return false;
                }
                if (!IsOpen || !ReferenceEquals(_entries.Peek(), entries[0])) return false;
            }
            await PopAsync(false, closeAll);
            return true;
        }
        finally
        {
            _closing = false;
            RefreshChrome();
        }
    }

    private async void CloseClicked(object sender, RoutedEventArgs e) => await RequestCloseAsync();

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (!IsOpen || _transitioning || _saving || _closing || _focusSuspensions > 0 || _entries.Peek().Page.IsBusy) return;
        var page = _entries.Peek().Page;
        if (page.IsReadOnly) return;
        _saving = true;
        RefreshChrome();
        var saved = false;
        try
        {
            saved = await page.SaveAsync();
        }
        catch (OperationCanceledException) when (page.Lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            // 异常可能含密钥或网络响应，界面只显示固定文案。
            page.SetError("保存失败，请检查输入后重试");
        }
        finally
        {
            _saving = false;
            RefreshChrome();
        }
        if (saved && IsOpen && ReferenceEquals(_entries.Peek().Page, page)) await PopAsync(true);
    }

    private async Task PopAsync(bool saved, bool closeAll = false)
    {
        var entries = closeAll ? _entries.ToArray() : [_entries.Peek()];
        foreach (var entry in entries) entry.Page.CancelPendingOperations();
        await AnimateAsync(false);
        if (_entries.Count == 0 || !ReferenceEquals(_entries.Peek(), entries[0])) return;
        foreach (var entry in entries)
        {
            _entries.Pop();
            entry.Page.StateChanged -= RefreshChrome;
            entry.Page.Dispose();
        }
        Body.Content = null;
        if (IsOpen)
        {
            Body.Content = _entries.Peek().Page;
            BodyScroll.ScrollToVerticalOffset(entries[0].PreviousScroll);
            RefreshChrome();
            await AnimateAsync(true);
        }
        else
        {
            Visibility = Visibility.Collapsed;
            DetachWindow();
        }
        // 整体关闭只恢复最外层的入口焦点，不重新展示即将被丢弃的父草稿。
        RestoreFocus(entries[^1].PreviousFocus);
        foreach (var entry in entries) entry.Completion.TrySetResult(saved);
    }

    private void RefreshChrome()
    {
        if (!IsOpen) return;
        var page = _entries.Peek().Page;
        TitleText.Text = DrawerText.T(page.TitleKey);
        SaveButton.Content = DrawerText.T(_entries.Count > 1 ? "保存并返回" : page.SaveButtonKey);
        SaveButton.Visibility = page.IsReadOnly ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = DrawerText.T(page.IsReadOnly ? "关闭" : "取消");
        CancelButton.Margin = page.IsReadOnly ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        BackButton.Visibility = _entries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var enabled = !_saving && !_transitioning && !_closing;
        SaveButton.IsEnabled = enabled && !page.IsBusy && !page.IsReadOnly;
        // 只读查询始终可关闭：PopAsync 会取消正在读取的任务。
        var canClose = enabled && (page.IsReadOnly || !page.IsBusy);
        CancelButton.IsEnabled = canClose;
        CloseButton.IsEnabled = canClose;
        BackButton.IsEnabled = canClose;
        // 确认框自行遮挡正文；保留正文可用，确保返回父层时能恢复原输入焦点。
        Body.IsEnabled = !_saving && !_transitioning;
        ErrorText.Text = page.ErrorText;
        ErrorText.Visibility = string.IsNullOrEmpty(page.ErrorText) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateWidth() => Panel.Width = ActualWidth < 640 ? Math.Max(0, ActualWidth) : 400;

    private async Task AnimateAsync(bool opening)
    {
        _transitioning = true;
        RefreshChrome();
        var animation = new DoubleAnimation(opening ? Panel.Width : 0, opening ? 0 : Panel.Width,
            TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        animation.Completed += (_, _) => completion.TrySetResult(true);
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, animation);
        // 卸载时动画时钟可能不再送达 Completed，延时仅为保证调用方一定能收尾。
        await Task.WhenAny(completion.Task, Task.Delay(240));
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.X = opening ? 0 : Panel.Width;
        _transitioning = false;
        RefreshChrome();
    }

    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsOpen || _focusSuspensions > 0 || e.Key != Key.Escape) return;
        // 模型下拉菜单先接收自己的 Esc；再次按 Esc 才关闭抽屉。
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = ParentOf(node))
            if (node is ComboBox { IsDropDownOpen: true }) return;
        e.Handled = true;
        await RequestCloseAsync();
    }

    private void OnWindowGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsOpen || _focusSuspensions > 0 || _redirectingFocus || (IsWithinPanel(e.NewFocus as DependencyObject) || AllowsOverlayFocus?.Invoke(e.NewFocus as DependencyObject) == true)) return;
        e.Handled = true;
        EnsureFocus();
    }

    private void EnsureFocus()
    {
        if (!IsOpen || _focusSuspensions > 0 || (IsWithinPanel(Keyboard.FocusedElement as DependencyObject) || AllowsOverlayFocus?.Invoke(Keyboard.FocusedElement as DependencyObject) == true)) return;
        FocusFirst();
    }

    private void FocusFirst()
    {
        if (!IsOpen || Body.Content is not FrameworkElement content) return;
        _redirectingFocus = true;
        try
        {
            if (!content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
                CloseButton.Focus();
        }
        finally { _redirectingFocus = false; }
    }

    private void RestoreFocus(IInputElement? element)
    {
        if (element is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } target
            && (!IsOpen || IsWithinPanel(target))) Keyboard.Focus(target);
        else if (IsOpen) FocusFirst();
    }

    private bool IsWithinPanel(DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, Panel)) return true;
            node = ParentOf(node);
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
    {
        if (node is System.Windows.Controls.Primitives.Popup popup) return popup.PlacementTarget;
        if (node is ContextMenu menu) return menu.PlacementTarget;
        // 下拉选项和右键菜单位于独立 PopupRoot，按所属控件回到抽屉，允许正常键盘选择。
        if (node is ComboBoxItem or MenuItem && ItemsControl.ItemsControlFromItemContainer(node) is { } owner) return owner;
        if (node is Visual or Visual3D)
            return VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
        return LogicalTreeHelper.GetParent(node);
    }

    private void DetachWindow()
    {
        if (_window is null) return;
        _window.PreviewKeyDown -= OnWindowKeyDown;
        _window.PreviewGotKeyboardFocus -= OnWindowGotKeyboardFocus;
        _window = null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachWindow();
        while (_entries.TryPop(out var entry))
        {
            entry.Page.StateChanged -= RefreshChrome;
            entry.Page.Dispose();
            entry.Completion.TrySetResult(false);
        }
        Body.Content = null;
        Visibility = Visibility.Collapsed;
    }
}
