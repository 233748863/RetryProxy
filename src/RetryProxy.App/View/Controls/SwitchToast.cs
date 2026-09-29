using RetryProxy.Service;
using RetryProxy.Service.I18n;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RetryProxy.View.Controls;

/// <summary>切换反馈独占一个提示位，5 秒内可撤销；新提示替换旧提示。</summary>
public sealed class SwitchToast : Border
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Wpf.Ui.Controls.Button _undo = new() { Margin = new Thickness(12, 0, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private Action? _action;
    public SwitchToast()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(16);
        Padding = new Thickness(14);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_undo, 1);
        grid.Children.Add(_text);
        grid.Children.Add(_undo);
        Child = grid;
        Visibility = Visibility.Collapsed;
        _timer.Tick += (_, _) => Hide();
        _undo.Click += (_, _) => { var action = _action; Hide(); action?.Invoke(); };
        Unloaded += (_, _) => Hide();
    }
    public void Show(KeySwitchNotice notice)
    {
        _timer.Stop();
        _action = notice.Undo;
        _text.Text = notice.Message;
        _undo.Content = I18nService.Instance.Translate("撤销");
        _undo.Visibility = _action is null ? Visibility.Collapsed : Visibility.Visible;
        Visibility = Visibility.Visible;
        _timer.Start();
    }
    public void Hide()
    {
        _timer.Stop();
        _action = null;
        Visibility = Visibility.Collapsed;
    }
}
