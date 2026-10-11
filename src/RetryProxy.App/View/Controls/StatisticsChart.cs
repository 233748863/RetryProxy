using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RetryProxy.Core.Metrics;
using RetryProxy.ViewModel.Pages;

namespace RetryProxy.View.Controls;

/// <summary>随窗口尺寸重绘的 WPF 图表。鼠标悬停、方向键查看数值，单击或回车筛选该时段。</summary>
public sealed class StatisticsChart : Control
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(StatisticsChartData), typeof(StatisticsChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));
    public static readonly DependencyProperty SelectRangeCommandProperty = DependencyProperty.Register(nameof(SelectRangeCommand), typeof(ICommand), typeof(StatisticsChart));
    public StatisticsChartData? Data { get => (StatisticsChartData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public ICommand? SelectRangeCommand { get => (ICommand?)GetValue(SelectRangeCommandProperty); set => SetValue(SelectRangeCommandProperty, value); }
    private Rect _plot;
    private int _point = -1;

    public StatisticsChart()
    {
        Focusable = true;
        MinHeight = 180;
        SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        SetResourceReference(FontFamilyProperty, "TextThemeFontFamily");
        AutomationProperties.SetName(this, StatisticsText.T("用量趋势，方向键查看，回车选择时段"));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (StatisticsChart)d;
        if (chart._point >= (chart.Data?.Points.Count ?? 0)) chart._point = -1;
        chart.UpdateTooltip();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var data = Data;
        if (data is null || ActualWidth < 120 || ActualHeight < 90) return;
        var peak = data.Points.Count == 0 ? 0 : data.Points.Max(p => data.Metric == UsageMetric.Speed
            ? Math.Max(p.Value, p.Parts.Count == 0 ? 0 : p.Parts.Max(s => s.Value)) : p.Value);
        if (!(peak > 0))
        {
            var empty = Text(data.EmptyText, 12);
            dc.DrawText(empty, new Point(Math.Max(0, (ActualWidth - empty.Width) / 2), (ActualHeight - empty.Height) / 2));
            _plot = Rect.Empty;
            return;
        }
        var top = AxisTop(peak, data.Metric == UsageMetric.Requests);
        var labels = Enumerable.Range(0, 5).Select(i => Text(StatisticsText.Metric(data.Metric, top * i / 4), 10)).ToArray();
        var left = labels.Max(l => l.Width) + 12;
        _plot = new Rect(left, 10, Math.Max(1, ActualWidth - left - 10), Math.Max(1, ActualHeight - 42));
        var grid = StatisticsText.ThemeBrush("ControlStrokeColorDefaultBrush");
        for (var i = 0; i < 5; i++)
        {
            var y = _plot.Bottom - _plot.Height * i / 4;
            dc.DrawLine(new Pen(grid, 1), new Point(_plot.Left, y), new Point(_plot.Right, y));
            dc.DrawText(labels[i], new Point(_plot.Left - labels[i].Width - 10, y - labels[i].Height / 2));
        }
        var width = _plot.Width / data.Points.Count;
        var bar = Math.Max(1, Math.Min(38, width * 0.70));
        var lastLabelEnd = double.NegativeInfinity;
        for (var i = 0; i < data.Points.Count; i++)
        {
            var point = data.Points[i];
            var x = _plot.Left + width * (i + 0.5);
            if (i == _point)
            {
                dc.PushOpacity(0.07);
                dc.DrawRoundedRectangle(Foreground, null, new Rect(x - width / 2, _plot.Top, width, _plot.Height), 3, 3);
                dc.Pop();
            }
            if (data.Metric == UsageMetric.Speed)
            {
                var height = _plot.Height * point.Value / top;
                dc.PushOpacity(0.20);
                dc.DrawRectangle(StatisticsText.Color(0), null, new Rect(x - bar / 2, _plot.Bottom - height, bar, height));
                dc.Pop();
                foreach (var part in point.Parts.Where(p => p.Value > 0))
                {
                    var y = _plot.Bottom - _plot.Height * part.Value / top;
                    dc.DrawLine(new Pen(part.Brush, 3), new Point(x - bar / 2, y), new Point(x + bar / 2, y));
                }
            }
            else
            {
                var y = _plot.Bottom;
                foreach (var part in point.Parts.Where(p => p.Value > 0))
                {
                    var height = _plot.Height * part.Value / top;
                    dc.DrawRectangle(part.Brush, null, new Rect(x - bar / 2, y - height, bar, height));
                    y -= height;
                }
            }
            var label = Text(point.Label, 10);
            var labelX = Math.Clamp(x - label.Width / 2, _plot.Left, Math.Max(_plot.Left, ActualWidth - label.Width));
            // 动态测量中文 / 日期标签，确保窄窗口中不重叠。
            if (labelX >= lastLabelEnd + 16 && labelX + label.Width <= ActualWidth)
            {
                dc.DrawText(label, new Point(labelX, _plot.Bottom + 10));
                lastLabelEnd = labelX + label.Width;
            }
        }
        if (IsKeyboardFocused) dc.DrawRoundedRectangle(null, new Pen(StatisticsText.Color(0), 1), new Rect(1, 1, ActualWidth - 2, ActualHeight - 2), 5, 5);
    }

    internal static double AxisTop(double peak, bool integral)
    {
        if (!double.IsFinite(peak) || peak <= 0) return 4;
        var e = Math.Pow(10, Math.Floor(Math.Log10(peak / 4)));
        foreach (var step in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
            if (step * e * 4 >= peak) return integral ? Math.Max(4, Math.Ceiling(step * e) * 4) : step * e * 4;
        return Math.Max(integral ? 4 : 0, 40 * e);
    }

    private FormattedText Text(string value, double size) => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this);
        var index = !_plot.IsEmpty && _plot.Contains(at) && Data is { Points.Count: > 0 } data
            ? Math.Clamp((int)((at.X - _plot.Left) * data.Points.Count / _plot.Width), 0, data.Points.Count - 1) : -1;
        if (_point == index) return;
        _point = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Arrow;
        UpdateTooltip();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsKeyboardFocused) _point = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        SelectPoint();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var count = Data?.Points.Count ?? 0;
        if (count == 0) return;
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
        {
            _point = e.Key switch { Key.Home => 0, Key.End => count - 1, Key.Left => Math.Max(0, _point - 1), _ => Math.Min(count - 1, _point + 1) };
            UpdateTooltip();
            InvalidateVisual();
            if (ToolTip is ToolTip tip) tip.IsOpen = true;
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space) { SelectPoint(); e.Handled = true; }
        else if (e.Key == Key.Escape && ToolTip is ToolTip tip) { tip.IsOpen = false; e.Handled = true; }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (ToolTip is ToolTip tip) tip.IsOpen = false;
        InvalidateVisual();
    }

    private void UpdateTooltip()
    {
        if (ToolTip is ToolTip previous) previous.IsOpen = false;
        var text = Data is { } data && _point >= 0 && _point < data.Points.Count ? data.Points[_point].ToolTip : null;
        ToolTip = text is null ? null : new ToolTip { Content = text, PlacementTarget = this, MaxWidth = 420 };
        AutomationProperties.SetHelpText(this, text ?? string.Empty);
    }

    private void SelectPoint()
    {
        if (Data is { } data && _point >= 0 && _point < data.Points.Count && SelectRangeCommand is { } command
            && command.CanExecute(data.Points[_point])) command.Execute(data.Points[_point]);
    }
}
