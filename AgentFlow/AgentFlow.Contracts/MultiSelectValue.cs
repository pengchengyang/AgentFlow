// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="MultiSelectValue.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Canonical text form for a multi-select parameter. A value is a semicolon-separated
/// list of option values (for example <c>A;B</c>); one selected option has no separator.
/// The codec is shared by the dialog and by nodes reading the parameter.
/// </summary>
public static class MultiSelectValue
{
    /// <summary>Separator used between selected option values.</summary>
    public const string Separator = ";";

    /// <summary>Join values as a canonical, trimmed, non-empty semicolon list.</summary>
    public static string Format(IEnumerable<string> values, string separator = Separator)
    {
        if (values is null)
            return "";

        var normalized = string.IsNullOrEmpty(separator) ? Separator : separator;
        return string.Join(
            normalized,
            values.Where(v => v is not null)
                  .Select(v => v.Trim())
                  .Where(v => v.Length > 0));
    }

    /// <summary>Split text into trimmed, non-empty values; order is preserved.</summary>
    public static IReadOnlyList<string> Parse(string? text, string separator = Separator)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var normalized = string.IsNullOrEmpty(separator) ? Separator : separator;
        return text.Split(normalized, StringSplitOptions.None)
                   .Select(v => v.Trim())
                   .Where(v => v.Length > 0)
                   .ToArray();
    }
}
