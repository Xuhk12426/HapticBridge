using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HapticBridge.Core.Models;

namespace HapticBridge.UI.Converters;

public class HealthStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(34, 197, 94));   // #22C55E
    private static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(234, 179, 8));  // #EAB308
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(239, 68, 68));     // #EF4444
    private static readonly SolidColorBrush GreyBrush = new(Color.FromRgb(107, 114, 128));  // #6B7280

    static HealthStateToBrushConverter()
    {
        GreenBrush.Freeze();
        YellowBrush.Freeze();
        RedBrush.Freeze();
        GreyBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ComponentHealthState state)
        {
            return state switch
            {
                ComponentHealthState.Working or
                ComponentHealthState.Connected or
                ComponentHealthState.Running or
                ComponentHealthState.Ready => GreenBrush,

                ComponentHealthState.NeedsAttention => YellowBrush,

                ComponentHealthState.Malfunctioning => RedBrush,

                _ => GreyBrush
            };
        }

        if (value is bool b)
        {
            return b ? GreenBrush : RedBrush;
        }

        return GreyBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class HealthStateToBackgroundBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBg = new(Color.FromArgb(32, 34, 197, 94));
    private static readonly SolidColorBrush YellowBg = new(Color.FromArgb(32, 234, 179, 8));
    private static readonly SolidColorBrush RedBg = new(Color.FromArgb(32, 239, 68, 68));
    private static readonly SolidColorBrush GreyBg = new(Color.FromArgb(32, 107, 114, 128));

    static HealthStateToBackgroundBrushConverter()
    {
        GreenBg.Freeze();
        YellowBg.Freeze();
        RedBg.Freeze();
        GreyBg.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ComponentHealthState state)
        {
            return state switch
            {
                ComponentHealthState.Working or
                ComponentHealthState.Connected or
                ComponentHealthState.Running or
                ComponentHealthState.Ready => GreenBg,

                ComponentHealthState.NeedsAttention => YellowBg,

                ComponentHealthState.Malfunctioning => RedBg,

                _ => GreyBg
            };
        }

        return GreyBg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
