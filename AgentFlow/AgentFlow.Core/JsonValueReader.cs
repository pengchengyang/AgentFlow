// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="JsonValueReader.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json.Nodes;

namespace AgentFlow.Core;

/// <summary>
/// Tolerant readers for editor JSON fields. A hand-edited or older document may store a number as
/// a string, a value as null, or the wrong JSON kind; the previous code called
/// <c>GetValue&lt;int&gt;()</c> directly, which throws instead of falling back.
/// </summary>
public static class JsonValueReader
{
    /// <summary>String value, or <paramref name="fallback"/> when missing / not a string.</summary>
    public static string? GetString(JsonObject? obj, string key, string? fallback = null)
    {
        var node = obj?[key];
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    }

    /// <summary>Integer value (number or numeric string), or <paramref name="fallback"/>.</summary>
    public static int GetInt(JsonObject? obj, string key, int fallback = 0)
    {
        var node = obj?[key];
        if (node is not JsonValue value)
            return fallback;

        if (value.TryGetValue<int>(out var number))
            return number;
        if (value.TryGetValue<long>(out var longNumber))
            return (int)Math.Clamp(longNumber, int.MinValue, int.MaxValue);
        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed))
            return parsed;
        return fallback;
    }

    /// <summary>Boolean value (bool or "true"/"false"), or <paramref name="fallback"/>.</summary>
    public static bool GetBool(JsonObject? obj, string key, bool fallback = false)
    {
        var node = obj?[key];
        if (node is not JsonValue value)
            return fallback;

        if (value.TryGetValue<bool>(out var flag))
            return flag;
        if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed))
            return parsed;
        return fallback;
    }
}
