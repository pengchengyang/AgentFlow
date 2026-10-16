// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BoolConverters.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AgentFlow.Services;

/// <summary>
/// Converts a boolean into a grid row span: true -> 2 (full height), false -> 1.
/// Used to expand the log panel over the whole canvas area.
/// </summary>
public sealed class BoolToRowSpanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 2 : 1;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Log panel height: true -> NaN (auto/fill), false -> 180 (default).</summary>
public sealed class BoolToLogPanelHeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? double.NaN : 180d;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts a boolean into a grid row: true -> 0 (top row), false -> 1 (bottom row).</summary>
public sealed class BoolToRowConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0 : 1;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts a boolean into a panel height: true -> Auto (fill), false -> 180 (default).</summary>
public sealed class BoolToPanelHeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? double.NaN : 180.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Inverts a boolean (used to hide the canvas while the log panel is expanded).</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
