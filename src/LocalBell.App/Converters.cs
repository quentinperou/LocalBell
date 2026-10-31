using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LocalBell.App;

/// <summary>True → Collapsed, false → Visible (the text of a row while it is not edited).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
