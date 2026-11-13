// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ParameterConversion.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;

namespace AgentFlow.Core;

/// <summary>
/// Converts JSON-deserialized parameter values (<see cref="JsonElement"/>) into native CLR values.
/// Shared by the GUI load path and the headless engine so the same document produces the same
/// parameter types in both modes.
/// </summary>
public static class ParameterConversion
{
    public static IReadOnlyDictionary<string, object?> Normalize(
        IReadOnlyDictionary<string, object?> parameters)
        => parameters.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value));

    public static object? Normalize(object? value) =>
        value is JsonElement element
            ? element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.ToString()
            }
            : value;
}


