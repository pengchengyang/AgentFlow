// -----------------------------------------------------------------------
// <copyright company="Your Company" file="JsonKeyValueParser.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;

namespace Node.AutoStart;

/// <summary>
/// Parses a JSON object whose values are string arrays into a key/value collection.
/// Key lookup is case-insensitive, while values preserve their original casing and order.
/// </summary>
public static class JsonKeyValueParser
{
    /// <summary>
    /// Attempts to parse a JSON key/value object.
    /// </summary>
    public static bool TryParse(
        string? json,
        out IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        settings = EmptySettings();

        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var parsed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                if (!parsed.TryGetValue(property.Name, out var values))
                {
                    values = new List<string>();
                    parsed.Add(property.Name, values);
                }

                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        return false;

                    values.Add(item.GetString() ?? string.Empty);
                }
            }

            settings = parsed.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to parse a JSON object whose property values are scalar strings.
    /// Key lookup is case-insensitive, while values are preserved exactly.
    /// </summary>
    public static bool TryParseStringValues(
        string? json,
        out IReadOnlyDictionary<string, string> values)
    {
        values = EmptyStringValues();

        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                if (!parsed.TryAdd(property.Name, property.Value.GetString() ?? string.Empty))
                    return false;
            }

            values = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a JSON object whose property values are scalar strings.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseStringValues(string? json)
    {
        if (!TryParseStringValues(json, out var values))
            throw new FormatException("The JSON payload must be an object whose values are strings.");

        return values;
    }
    /// <summary>
    /// Parses a JSON key/value object or throws when the payload is invalid.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string? json)
    {
        if (!TryParse(json, out var settings))
            throw new FormatException("The JSON key/value payload must be an object whose values are string arrays.");

        return settings;
    }

    /// <summary>
    /// Gets all values for a key. Key lookup is case-insensitive.
    /// </summary>
    public static bool TryGetValues(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        string key,
        out IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (settings.TryGetValue(key, out var found))
        {
            values = found;
            return true;
        }

        values = Array.Empty<string>();
        return false;
    }

    /// <summary>
    /// Gets the first value for a key, or null when the key does not exist.
    /// </summary>
    public static string? GetSingleValue(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        string key)
    {
        return TryGetValues(settings, key, out var values) && values.Count > 0
            ? values[0]
            : null;
    }

    private static IReadOnlyDictionary<string, string> EmptyStringValues()
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> EmptySettings()
        => new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}
