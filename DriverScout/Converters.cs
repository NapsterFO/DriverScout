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

/// <summary>Ключ иконки программы → картинка из ресурсов Assets\apps.</summary>
public sealed class AppIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key) return null;
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/DriverScout;component/Assets/apps/{key}.png");
        image.DecodePixelWidth = 96; // плитка 46 px, запас под 200 % масштаб
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#FF5865F2" → кисть (цвет плитки программы).</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(s)) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
