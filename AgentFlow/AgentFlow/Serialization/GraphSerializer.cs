// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="GraphSerializer.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentFlow.Core;

namespace AgentFlow.Serialization;

/// <summary>
/// Layered document produced when persisting an editor graph. The <see cref="Nodes"/>
/// and <see cref="Connections"/> lists carry the structure; per-node <see cref="SerializedNode.Logic"/>
/// is the logical-parameter blob produced by <see cref="AgentFlow.Core.LogicSerializer"/>.
/// All JSON keys use lowerCamelCase (parameter values are the only exception).
/// </summary>
public sealed class GraphDocument
{
    public string Version { get; set; } = "1.0";
    public List<SerializedNode> Nodes { get; set; } = new();
    public List<SerializedConnection> Connections { get; set; } = new();
}

/// <summary>A persisted node instance: UI state + the logical parameter blob from Core.</summary>
public sealed class SerializedNode
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>
    /// Logical parameters produced by <see cref="AgentFlow.Core.LogicSerializer"/>
    /// (uuid, functionName, instanceId + subclass data), plus the node's display <c>name</c> as
    /// the first key. Serialized as the node's top-level <c>logic</c> field and restored
    /// on load via <see cref="AgentFlow.Core.LogicDeserializer"/>.
    /// </summary>
    public JsonObject? Logic { get; set; }
}

/// <summary>A persisted pin-to-pin connection.</summary>
public sealed class SerializedConnection
{
    /// <summary>Instance id of the source (output) pin.</summary>
    public int FromPinId { get; set; }

    /// <summary>Instance id of the target (input) pin.</summary>
    public int ToPinId { get; set; }
}

/// <summary>
/// Serializes an <see cref="EditorGraph"/> into a layered JSON document.
/// UI-related state (node id, position, name, connections) is handled here;
/// logical / runtime node parameters are delegated to the Core-layer
/// <see cref="AgentFlow.Core.LogicSerializer"/> on each node instance. This class
/// therefore lives in the UI layer and is not part of Core.
/// </summary>
public static class GraphSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Serialize the whole graph to a layered JSON string.</summary>
    public static string Serialize(EditorGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var doc = BuildDocument(graph);
        return JsonSerializer.Serialize(doc, Options);
    }

    /// <summary>Serialize the whole graph and write it to <paramref name="path"/>.</summary>
    public static void Save(EditorGraph graph, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, Serialize(graph));
    }

    private static GraphDocument BuildDocument(EditorGraph graph)
    {
        var doc = new GraphDocument();

        foreach (var node in graph.Nodes)
        {
            // Logical parameters come from the Core layer; the node's editable display name
            // is written by BaseNode as the first key inside the logic blob.
            var parameters = LogicSerializer.Serialize(node.RuntimeNode);
            var logic = new JsonObject();
            // Deep-clone each value: they still belong to the `parameters` object returned by
            // LogicSerializer, and a JsonNode can only have one parent.
            foreach (var kv in parameters)
                logic[kv.Key] = kv.Value?.DeepClone();

            doc.Nodes.Add(new SerializedNode
            {
                Id = node.Id,
                X = node.X,
                Y = node.Y,
                Logic = logic
            });
        }

        foreach (var c in graph.Connections)
        {
            doc.Connections.Add(new SerializedConnection
            {
                FromPinId = c.SourcePin.Id,
                ToPinId = c.TargetPin.Id
            });
        }

        return doc;
    }
}
