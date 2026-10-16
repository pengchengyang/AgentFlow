// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LogLevelConverters.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AgentFlow.Services;

/// <summary>Converts a log level string to a badge background color.</summary>
public sealed class LevelToBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value?.ToString()?.ToUpperInvariant() ?? "";
        return level switch
        {
            "TRACE" => new SolidColorBrush(Color.Parse("#F8FAFC")),
            "DEBUG" => new SolidColorBrush(Color.Parse("#DBEAFE")),
            "INFO" => new SolidColorBrush(Color.Parse("#EDE9FE")),
            "WARN" => new SolidColorBrush(Color.Parse("#FEF3C7")),
            "ERROR" or "CRITICAL" or "FATAL" => new SolidColorBrush(Color.Parse("#FEE2E2")),
            _ => new SolidColorBrush(Color.Parse("#F1F5F9")),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts a log level string to a badge foreground text color.</summary>
public sealed class LevelToForegroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value?.ToString()?.ToUpperInvariant() ?? "";
        return level switch
        {
            "TRACE" => new SolidColorBrush(Color.Parse("#94A3B8")),
            "DEBUG" => new SolidColorBrush(Color.Parse("#2563EB")),
            "INFO" => new SolidColorBrush(Color.Parse("#7C3AED")),
            "WARN" => new SolidColorBrush(Color.Parse("#D97706")),
            "ERROR" or "CRITICAL" or "FATAL" => new SolidColorBrush(Color.Parse("#DC2626")),
            _ => new SolidColorBrush(Color.Parse("#64748B")),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
