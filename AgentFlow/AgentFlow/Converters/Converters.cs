// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Converters.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

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

/// <summary>Returns true when the bound string is non-null and non-empty (used to show optional text).</summary>
public sealed class NotEmptyConverter : IValueConverter
{
    public static readonly NotEmptyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Initial shown inside the component-list icon square. Takes the first letter of the node
/// name ("Socket Client" -&gt; S, "ReAct Agent" -&gt; R). Presentation only.
/// </summary>
public sealed class LastWordInitialConverter : IValueConverter
{
    public static readonly LastWordInitialConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return string.Empty;
        foreach (var ch in s)
            if (char.IsLetter(ch))
                return char.ToUpperInvariant(ch).ToString();
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Turns the raw pin summary ("in:Input:String, out:Output:String") into the design's short
/// subtitle: "Input · String" / "Output · String" / "In / Out". Presentation only — the
/// underlying palette data is untouched.
/// </summary>
public sealed class PinSummaryConverter : IValueConverter
{
    public static readonly PinSummaryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return string.Empty;

        bool hasIn = false, hasOut = false;
        string? inType = null, outType = null;

        foreach (var chunk in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = chunk.Trim().Split(':');
            if (seg.Length < 3) continue;
            if (seg[0].Equals("in", StringComparison.OrdinalIgnoreCase)) { hasIn = true; inType = seg[2]; }
            else if (seg[0].Equals("out", StringComparison.OrdinalIgnoreCase)) { hasOut = true; outType = seg[2]; }
        }

        if (hasIn && hasOut) return "In / Out";
        if (hasOut) return $"Output · {outType}";
        if (hasIn) return $"Input · {inType}";
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
