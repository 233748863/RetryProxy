using System;
using RetryProxy.Core.Workspace;
using RetryProxy.Core.Logging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace RetryProxy.View.Controls;

/// <summary>
/// 单行日志：时间戳最弱，级别标签按严重度着色，标签组用强调色，
/// 正文里的 HTTP 状态码与对照不一致字段单独着色，其余正文按级别决定深浅。颜色全部走主题资源。
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
        Append($"[{parts.Source.Label()}] ", "AccentTextFillColorPrimaryBrush", emphasis: true);
        for (var index = parts.HasSourceTag ? 1 : 0; index < parts.Tags.Count; index++)
        {
            var tag = parts.Tags[index];
            Append($"[{tag}]", "AccentTextFillColorPrimaryBrush");
        }

        if (parts.Tags.Count > 0)
        {
            Append(" ", "TextFillColorTertiaryBrush");
        }

        var bodyKey = BodyBrushKey(parts.Level);
        var mismatches = LogLine.FindComparisonMismatchRanges(parts.Body);
        var status = LogLine.FindStatusCode(parts.Body);
        if (status is { } found && LogLine.StatusColor(found.Status) is { } klass)
        {
            var statusKey = klass switch
            {
                StatusColorClass.Warning => "SystemFillColorCautionBrush",
                StatusColorClass.Danger => "SystemFillColorCriticalBrush",
                _ => LevelBrushKey(parts.Level),
            };
            AppendBodySegment(0, found.Start, bodyKey);
            AppendBodySegment(found.Start, found.End, statusKey);
            AppendBodySegment(found.End, parts.Body.Length, bodyKey);
        }
        else
        {
            AppendBodySegment(0, parts.Body.Length, bodyKey);
        }

        void AppendBodySegment(int start, int end, string brushKey)
        {
            var cursor = start;
            foreach (var mismatch in mismatches)
            {
                var highlightStart = Math.Max(cursor, mismatch.Start);
                var highlightEnd = Math.Min(end, mismatch.End);
                if (highlightStart >= highlightEnd)
                {
                    continue;
                }

                Append(parts.Body.Substring(cursor, highlightStart - cursor), brushKey);
                Append(parts.Body.Substring(highlightStart, highlightEnd - highlightStart),
                    "SystemFillColorCautionBrush", emphasis: true);
                cursor = highlightEnd;
            }

            Append(parts.Body.Substring(cursor, end - cursor), brushKey);
        }
    }

    private void Append(string text, string brushKey, bool emphasis = false)
    {
        if (text.Length == 0)
        {
            return;
        }

        var run = new Run(text);
        if (emphasis)
        {
            run.FontWeight = FontWeights.SemiBold;
        }
        run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
        Inlines.Add(run);
    }
}
