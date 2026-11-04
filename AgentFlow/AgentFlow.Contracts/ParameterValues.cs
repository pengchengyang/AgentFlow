// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ParameterValues.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.Json;

namespace AgentFlow.Contracts;

/// <summary>
/// Culture-invariant parameter readers for nodes. Using <see cref="Convert.ToDouble(object)"/> or
/// <c>double.Parse</c> would interpret text with the host's current culture: on de-DE a saved
/// <c>"0.2"</c> would become 2. Every node should read parameters through these helpers so a
/// workflow behaves the same on every machine.
/// </summary>
public static class ParameterValues
{
    /// <summary>Read an int parameter (numbers, numeric strings and JSON numbers; otherwise fallback).</summary>
    public static int GetInt(object? value, int fallback = 0)
    {
        switch (value)
        {
            case null:
                return fallback;
            case int i:
                return i;
            case long l:
                return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
            case short s:
                return s;
            case byte b:
                return b;
            case ushort us:
                return us;
            case uint ui:
                return (int)Math.Min(ui, int.MaxValue);
            case float f:
                return FromDouble(f, fallback);
            case double d:
                return FromDouble(d, fallback);
            case decimal m:
                return FromDouble((double)m, fallback);
            case string text:
                return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : fallback;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Number when element.TryGetInt32(out var number) => number,
                    JsonValueKind.Number when element.TryGetInt64(out var wide) =>
                        (int)Math.Clamp(wide, int.MinValue, int.MaxValue),
                    JsonValueKind.String => GetInt(element.GetString(), fallback),
                    _ => fallback
                };
            default:
                return fallback;
        }
    }

    /// <summary>Read a long parameter (numbers, numeric strings and JSON numbers; otherwise fallback).</summary>
    public static long GetLong(object? value, long fallback = 0)
    {
        switch (value)
        {
            case null:
                return fallback;
            case long l:
                return l;
            case int i:
                return i;
            case string text:
                return long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : fallback;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Number when element.TryGetInt64(out var number) => number,
                    JsonValueKind.String => GetLong(element.GetString(), fallback),
                    _ => fallback
                };
            default:
                return value is int or short or byte or ushort or uint or float or double or decimal
                    ? GetInt(value, fallback is >= int.MinValue and <= int.MaxValue ? (int)fallback : 0)
                    : fallback;
        }
    }

    /// <summary>Read a double parameter (numbers, invariant numeric strings and JSON numbers; otherwise fallback).</summary>
    public static double GetDouble(object? value, double fallback = 0)
    {
        switch (value)
        {
            case null:
                return fallback;
            case double d:
                return d;
            case float f:
                return f;
            case decimal m:
                return (double)m;
            case int i:
                return i;
            case long l:
                return l;
            case string text:
                return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : fallback;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Number => element.GetDouble(),
                    JsonValueKind.String => GetDouble(element.GetString(), fallback),
                    _ => fallback
                };
            default:
                return fallback;
        }
    }

    /// <summary>Read a float parameter (numbers, invariant numeric strings and JSON numbers; otherwise fallback).</summary>
    public static float GetFloat(object? value, float fallback = 0)
        => (float)GetDouble(value, fallback);

    /// <summary>Read a bool parameter (bool, "true"/"false"/"1"/"0", or a nonzero number; otherwise fallback).</summary>
    public static bool GetBool(object? value, bool fallback = false)
    {
        switch (value)
        {
            case null:
                return fallback;
            case bool b:
                return b;
            case int i:
                return i != 0;
            case long l:
                return l != 0;
            case double d:
                return d != 0;
            case string text:
                return text.Trim() switch
                {
                    var t when t.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
                    var t when t.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
                    var t when t is "1" => true,
                    var t when t is "0" => false,
                    _ => fallback
                };
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => GetDouble(element, 0) != 0,
                    JsonValueKind.String => GetBool(element.GetString(), fallback),
                    _ => fallback
                };
            default:
                return fallback;
        }
    }

    /// <summary>Read a string parameter; null / missing yields <paramref name="fallback"/>.</summary>
    public static string? GetString(object? value, string? fallback = null)
    {
        switch (value)
        {
            case null:
                return fallback;
            case string text:
                return text;
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                return element.GetString() ?? fallback;
            default:
                return value.ToString() ?? fallback;
        }
    }

    private static int FromDouble(double value, int fallback)
    {
        if (!double.IsFinite(value))
            return fallback;
        if (value >= int.MaxValue)
            return int.MaxValue;
        if (value <= int.MinValue)
            return int.MinValue;
        return (int)value;
    }
}
