// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="GraphDeserializer.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentFlow.Core;

/// <summary>
/// Deserializes a layered JSON document back into a <see cref="GraphDocument"/>
/// (UI state + per-node logical parameter blobs). The caller (AgentFlow) then
/// materializes node instances and invokes
/// <see cref="Contracts.BaseNode.DeserializeParameters"/> on each one so the
/// contract layer restores its own logical / runtime state.
/// </summary>
public static class GraphDeserializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Parse a layered graph JSON string into a <see cref="GraphDocument"/>.</summary>
    public static GraphDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        return JsonSerializer.Deserialize<GraphDocument>(json, Options)
            ?? throw new InvalidDataException("Graph JSON is empty or malformed.");
    }

    /// <summary>Read a graph JSON file and deserialize it into a <see cref="GraphDocument"/>.</summary>
    public static GraphDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Graph file not found.", path);
        return Deserialize(File.ReadAllText(path));
    }
}
