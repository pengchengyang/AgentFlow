// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BoolConverters.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace AgentFlow.Services;

/// <summary>
/// Height of the log panel: <c>true</c> (expanded) -> NaN, which is the layout system's "Auto"
/// value, so the panel is stretched to the full height of its cell; <c>false</c> -> the docked
/// height, so the panel sits at the bottom of the canvas area.
/// </summary>
/// <remarks>
/// NaN must be returned explicitly. Returning <c>AvaloniaProperty.UnsetValue</c> here goes through
/// the binding's "no value" path, which is not a height at all, and left the panel at its docked
/// height while expanded: a strip of the canvas background then showed above it.
/// </remarks>
public sealed class BoolToPanelHeightConverter : IValueConverter
{
    /// <summary>Height of the log panel while it is docked at the bottom of the canvas area.</summary>
    public const double DockedHeight = 180d;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? double.NaN : DockedHeight;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Vertical alignment of the log panel: <c>true</c> (expanded) -> <see cref="VerticalAlignment.Stretch"/>
/// so it fills its cell from the very top, <c>false</c> -> <see cref="VerticalAlignment.Bottom"/>
/// so the docked panel hugs the status bar instead of being centred.
/// </summary>
public sealed class BoolToPanelAlignmentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;

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
