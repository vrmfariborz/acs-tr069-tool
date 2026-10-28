using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AcsTool.Core.Sessions;
using AcsTool.Presentation.Abstractions;

namespace AcsTool.App.Converters;

/// <summary>null/empty → Collapsed, otherwise Visible. ConverterParameter "invert" flips it.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is string s ? s.Length > 0 : value is not null;
        if (parameter as string == "invert")
        {
            has = !has;
        }
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Subtle row background per session outcome (plan §4.2).</summary>
public sealed class OutcomeToBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Frozen(Color.FromRgb(0xEE, 0xF8, 0xEE));
    private static readonly Brush Busy = Frozen(Color.FromRgb(0xF4, 0xF7, 0xFC));
    private static readonly Brush Bad = Frozen(Color.FromRgb(0xFD, 0xEC, 0xEC));
    private static readonly Brush Warn = Frozen(Color.FromRgb(0xFF, 0xF6, 0xE0));
    private static readonly Brush Neutral = Frozen(Colors.White);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SessionOutcome.Completed => Ok,
        SessionOutcome.InProgress => Busy,
        SessionOutcome.UpstreamError or SessionOutcome.AuthFailed => Bad,
        SessionOutcome.AcsFault or SessionOutcome.CpeFault or SessionOutcome.HttpError or SessionOutcome.ProtocolError => Warn,
        _ => Neutral,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public sealed class BannerKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        BannerKind.Error => new SolidColorBrush(Color.FromRgb(0xF8, 0xD7, 0xDA)),
        BannerKind.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xCD)),
        BannerKind.Success => new SolidColorBrush(Color.FromRgb(0xD4, 0xED, 0xDA)),
        _ => new SolidColorBrush(Color.FromRgb(0xD1, 0xEC, 0xF1)),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
