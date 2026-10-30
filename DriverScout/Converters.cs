using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DriverScout;

/// <summary>Счётчик в меню: пусто или «0» — скрыт.</summary>
public sealed class BadgeVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Length > 0 && s != "0" ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#FF5865F2" → кисть (цвет плитки программы).</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(s)) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
