// -----------------------------------------------------------------------
// <copyright company="Your Company" file="IniFileParser.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Node.ReadIniFile;

/// <summary>
/// Parses INI text into a flat dictionary. Section-content lines are stored under the section
/// name, while standard key/value lines are stored under <c>SECTION.KEY</c>. Every value is
/// stored as a list so duplicate entries are not lost.
/// </summary>
public static class IniFileParser
{
    private static readonly Regex ValidKeyPattern = new(
        "^[A-Za-z0-9_.-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempts to parse complete INI text.
    /// </summary>
    public static bool TryParse(
        string? content,
        out IniFileParseResult result,
        out string? error)
    {
        result = IniFileParseResult.Empty;
        error = null;

        if (content is null)
        {
            error = "INI content is null.";
            return false;
        }

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var currentSection = string.Empty;
        var valueCount = 0;
        var lineNumber = 0;

        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;

            if (trimmed.StartsWith('['))
            {
                if (!trimmed.EndsWith(']'))
                {
                    error = $"Line {lineNumber}: section header must end with ']'.";
                    return false;
                }

                var sectionName = trimmed[1..^1].Trim();
                if (sectionName.Length == 0)
                {
                    error = $"Line {lineNumber}: section name must not be empty.";
                    return false;
                }

                currentSection = sectionName;
                sections.Add(sectionName);
                continue;
            }

            if (TrySplitKeyValue(trimmed, out var key, out var value))
            {
                var outputKey = currentSection.Length > 0
                    ? $"{currentSection}.{key}"
                    : $"GLOBAL.{key}";
                AddValue(entries, outputKey, value, ref valueCount);
                continue;
            }

            if (currentSection.Length > 0)
            {
                AddValue(entries, currentSection, trimmed, ref valueCount);
            }
            else
            {
                warnings.Add(
                    $"Line {lineNumber}: ignored a value outside any section: {trimmed}");
            }
        }

        var values = entries.ToDictionary(
            pair => pair.Value.DisplayKey,
            pair => (IReadOnlyList<string>)pair.Value.Values.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        result = new IniFileParseResult(
            values,
            sections.Count,
            valueCount,
            warnings);
        return true;
    }

    private static bool TrySplitKeyValue(
        string line,
        out string key,
        out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
            return false;

        var candidateKey = line[..separatorIndex].Trim();
        if (candidateKey.Length == 0 || !ValidKeyPattern.IsMatch(candidateKey))
            return false;

        key = candidateKey;
        value = line[(separatorIndex + 1)..].Trim();
        return true;
    }

    private static void AddValue(
        Dictionary<string, Entry> entries,
        string key,
        string value,
        ref int valueCount)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            entry = new Entry(key);
            entries.Add(key, entry);
        }

        entry.Values.Add(value);
        valueCount++;
    }

    private sealed class Entry(string displayKey)
    {
        public string DisplayKey { get; } = displayKey;
        public List<string> Values { get; } = new();
    }
}

/// <summary>
/// Parsed INI values and summary information used for diagnostics.
/// </summary>
public sealed record IniFileParseResult(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Values,
    int SectionCount,
    int ValueCount,
    IReadOnlyList<string> Warnings)
{
    public static IniFileParseResult Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        0,
        0,
        Array.Empty<string>());
}