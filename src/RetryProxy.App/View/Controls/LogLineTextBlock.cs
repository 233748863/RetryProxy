using RetryProxy.Core.Workspace;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace RetryProxy.View.Controls;

/// <summary>
/// 单行日志：时间戳最弱，级别标签按严重度着色，标签组用强调色，
/// 正文里的 HTTP 状态码单独着色，其余正文按级别决定深浅。颜色全部走主题资源。
/// </summary>
public class LogLineTextBlock : TextBlock
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(string), typeof(LogLineTextBlock), new PropertyMetadata(string.Empty, OnLineChanged));

    public string Line
    {
        get => (string)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    private static void OnLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LogLineTextBlock)d).Rebuild((string)e.NewValue);
    }

    private static string LevelBrushKey(LogLevelFilter level) => level switch
    {
        LogLevelFilter.Error => "SystemFillColorCriticalBrush",
        LogLevelFilter.Warning => "SystemFillColorCautionBrush",
        _ => "SystemFillColorSuccessBrush",
    };

    private static string BodyBrushKey(LogLevelFilter level) => level switch
    {
        LogLevelFilter.Error => "SystemFillColorCriticalBrush",
        LogLevelFilter.Warning => "SystemFillColorCautionBrush",
        _ => "TextFillColorSecondaryBrush",
    };

    private void Rebuild(string line)
    {
        Inlines.Clear();
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        var parts = LogLine.Split(line);
        if (parts.Timestamp.Length > 0)
        {
            Append(parts.TimeOfDay + " ", "TextFillColorTertiaryBrush");
        }

        Append(parts.Level.Badge() + " ", LevelBrushKey(parts.Level));
        foreach (var tag in parts.Tags)
        {
            Append($"[{tag}]", "AccentTextFillColorPrimaryBrush");
        }

        if (parts.Tags.Count > 0)
        {
            Append(" ", "TextFillColorTertiaryBrush");
        }

        var bodyKey = BodyBrushKey(parts.Level);
        var status = LogLine.FindStatusCode(parts.Body);
        if (status is { } found && LogLine.StatusColor(found.Status) is { } klass)
        {
            var statusKey = klass switch
            {
                StatusColorClass.Warning => "SystemFillColorCautionBrush",
                StatusColorClass.Danger => "SystemFillColorCriticalBrush",
                _ => LevelBrushKey(parts.Level),
            };
            Append(parts.Body.Substring(0, found.Start), bodyKey);
            Append(parts.Body.Substring(found.Start, found.End - found.Start), statusKey);
            Append(parts.Body.Substring(found.End), bodyKey);
        }
        else
        {
            Append(parts.Body, bodyKey);
        }
    }

    private void Append(string text, string brushKey)
    {
        if (text.Length == 0)
        {
            return;
        }

        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
        Inlines.Add(run);
    }
}
