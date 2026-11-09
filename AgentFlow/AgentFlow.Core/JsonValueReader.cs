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

    /// <summary>
    /// Dependency-id list. Accepts a JSON array, a single number, a numeric string, or a
    /// comma/semicolon-separated string so new and legacy workflow files both load safely.
    /// </summary>
    public static IReadOnlyList<int> GetIntList(JsonObject? obj, string key)
    {
        var node = obj?[key];
        var ids = new List<int>();

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue arrayValue && TryReadInt(arrayValue, out var arrayId))
                    ids.Add(arrayId);
            }
        }
        else if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) &&
                (text.Contains(',') || text.Contains(';')))
            {
                foreach (var part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(part, out var parsed))
                        ids.Add(parsed);
                }
            }
            else if (TryReadInt(value, out var id))
            {
                ids.Add(id);
            }
        }

        return AgentFlow.Contracts.BaseNode.NormalizeDependsOn(ids);
    }

    private static bool TryReadInt(JsonValue value, out int result)
    {
        if (value.TryGetValue<int>(out result))
            return true;
        if (value.TryGetValue<long>(out var longNumber))
        {
            result = (int)Math.Clamp(longNumber, int.MinValue, int.MaxValue);
            return true;
        }
        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out result))
            return true;
        result = 0;
        return false;
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
