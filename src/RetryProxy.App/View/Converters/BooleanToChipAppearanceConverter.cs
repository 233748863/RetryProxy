using System;
using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Converters;

/// <summary>筛选 chip：选中用主色，未选中用次色。</summary>
public class BooleanToChipAppearanceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
