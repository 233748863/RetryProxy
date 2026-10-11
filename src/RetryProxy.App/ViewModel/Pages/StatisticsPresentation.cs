using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using RetryProxy.Core.Metrics;

namespace RetryProxy.ViewModel.Pages;

public sealed record StatisticsChoice(string Id, string Name);
public sealed record StatisticsCategory(string Id, string Name, Brush Brush);
public sealed record StatisticsChartPart(double Value, Brush Brush);
public sealed record StatisticsChartPoint(DateOnly First, DateOnly Last, string Label, string ToolTip, double Value, IReadOnlyList<StatisticsChartPart> Parts);
public sealed record StatisticsChartData(IReadOnlyList<StatisticsChartPoint> Points, UsageMetric Metric, string EmptyText);

internal static class StatisticsText
{
    // 复用冻结画刷；相同颜色不会因创建新对象触发无意义的绑定和图表更新。
    private static readonly Brush[] DarkColors = Palette(["#A5B4FC", "#7DD3FC", "#6EE7B7", "#FCD34D", "#F9A8D4", "#C4B5FD", "#94A3B8", "#71717A"]);
    private static readonly Brush[] LightColors = Palette(["#5B50DA", "#167BBD", "#128567", "#BC7C13", "#C34383", "#8854CC", "#61748C", "#92929D"]);
    private static readonly Brush[] DarkHeatColors = HeatPalette(DarkColors[0]);
    private static readonly Brush[] LightHeatColors = HeatPalette(LightColors[0]);

    public static string T(string text, params object[] args) => ClientPageText.Translate(text, args);

    public static string Count(decimal value)
    {
        var chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        if (chinese)
        {
            if (value >= 100_000_000) return (value / 100_000_000).ToString("0.##", CultureInfo.CurrentUICulture) + "亿";
            if (value >= 10_000) return (value / 10_000).ToString("0.##", CultureInfo.CurrentUICulture) + "万";
        }
        else
        {
            if (value >= 1_000_000_000) return (value / 1_000_000_000).ToString("0.##", CultureInfo.CurrentUICulture) + "B";
            if (value >= 1_000_000) return (value / 1_000_000).ToString("0.##", CultureInfo.CurrentUICulture) + "M";
            if (value >= 1000) return (value / 1000).ToString("0.##", CultureInfo.CurrentUICulture) + "K";
        }
        return value.ToString("0.##", CultureInfo.CurrentUICulture);
    }

    public static string Exact(decimal value) => value.ToString("N0", CultureInfo.CurrentUICulture);
    public static string Amount(decimal value) => "$" + value.ToString("0.############################", CultureInfo.InvariantCulture);
    public static string Money(decimal value) => value is > 0 and < 0.000001m ? Amount(value)
        : "$" + value.ToString(value == 0 || value >= 1 ? "N2" : value < 0.01m ? "0.######" : "0.####", CultureInfo.CurrentUICulture);
    public static string Rate(double? value, bool upper = false) => value is null ? "—"
        : (upper ? "≤" : string.Empty) + value.Value.ToString("0.0", CultureInfo.CurrentUICulture) + "%";
    public static string Seconds(double? value) => value is null || !double.IsFinite(value.Value) ? "—"
        : T("{0} 秒", value.Value.ToString("0.00", CultureInfo.CurrentUICulture));
    public static string Metric(UsageMetric metric, double value) => metric switch
    {
        UsageMetric.Cost => Money((decimal)value),
        UsageMetric.Speed => T("{0} /秒", value.ToString("0.#", CultureInfo.CurrentUICulture)),
        _ => Count((decimal)value),
    };

    public static Brush ThemeBrush(string name) => Application.Current?.TryFindResource(name) as Brush ?? Brushes.Gray;

    public static Brush Color(int index)
    {
        var colors = IsDark ? DarkColors : LightColors;
        return colors[Math.Clamp(index, 0, colors.Length - 1)];
    }

    public static Brush HeatColor(int level) => (IsDark ? DarkHeatColors : LightHeatColors)[Math.Clamp(level, 1, 4) - 1];

    private static bool IsDark => ThemeBrush("ApplicationBackgroundBrush") is SolidColorBrush bg && bg.Color.R + bg.Color.G + bg.Color.B < 384;

    private static Brush[] Palette(string[] colors) => colors.Select(value =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return (Brush)brush;
    }).ToArray();

    private static Brush[] HeatPalette(Brush color) => Enumerable.Range(1, 4).Select(level =>
    {
        var brush = color.Clone();
        brush.Opacity = 0.22 + 0.78 * level / 4;
        brush.Freeze();
        return brush;
    }).ToArray();

    public static string UsageHelp => T("Token = 模型计量文本的单位。总用量 = 普通输入 + 缓存读取 + 缓存写入 + 输出；思考用量已包含在输出中。缺少用量时只累计已知部分，以 ≥ 标记下限。");
    public static string CacheHelp => T("缓存命中率 = 已复用的输入 / 总输入，按用量加权。仅计算总输入与缓存读取均有效的请求；Claude 未报告缓存写入时以 ≤ 标记上限。");
    public static string CostHelp => T("美元估算费用 = 各类用量 × 对应的每百万单价。公开价来自 models.dev，可按模型和供应商修改；未知价格或不足以计价的用量不计入费用。");
    public static string SpeedHelp => T("输出速度 = 有效流式回复的输出总量 / 生成时间之和。思考用量已知时，只计回答输出及首段正文之后的时间；不足 0.1 秒、超过 10000 Token/秒的样本不计入。");
}
