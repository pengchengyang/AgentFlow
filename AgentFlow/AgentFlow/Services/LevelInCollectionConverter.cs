// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LevelInCollectionConverter.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AgentFlow.Services;

/// <summary>
/// Returns true when the bound collection contains the string passed via <c>ConverterParameter</c>.
/// Used to drive the checked (selected) state of the level filter toggle buttons.
/// </summary>
public sealed class LevelInCollectionConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ICollection col && parameter is string p)
        {
            foreach (object? item in col)
            {
                if (string.Equals(item?.ToString(), p, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
