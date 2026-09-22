using System.Globalization;
using Avalonia.Data.Converters;

namespace AgentFlow.Converters;

/// <summary>Inverts a boolean (used to switch between connected / disconnected pin visuals).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
