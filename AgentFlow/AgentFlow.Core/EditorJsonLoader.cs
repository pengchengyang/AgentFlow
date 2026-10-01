// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="EditorJsonLoader.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentFlow.Core;

/// <summary>
/// A node as persisted by the AgentFlow editor (the top-level <c>logic</c> blob carries
/// every logical parameter; <c>x</c>/<c>y</c> are GUI-only and ignored by Core).
/// </summary>
public sealed class EditorNodeSpec
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public JsonObject? Logic { get; set; }
}

/// <summary>A pin-to-pin connection persisted by the editor, keyed by pin instance ids.</summary>
public sealed class EditorConnectionSpec
{
    public int FromPinId { get; set; }
    public int ToPinId { get; set; }
}

/// <summary>
/// The full document persisted by the editor. Core only consumes the logical parts
/// (<see cref="Nodes"/> + <see cref="Connections"/>); GUI-only fields are ignored.
/// </summary>
public sealed class EditorDocument
{
    public string Version { get; set; } = "1.0";
    public List<EditorNodeSpec> Nodes { get; set; } = new();
    public List<EditorConnectionSpec> Connections { get; set; } = new();
}

/// <summary>
/// Loads an editor-persisted workflow document (the same JSON the GUI writes) into a
/// runnable <see cref="WorkflowGraph"/>, so the headless CLI can run the exact same logic
/// without any GUI dependency. Only the <c>logic</c> parts are parsed: node type/params are
/// taken from each node's <c>logic</c> blob, and pin-id based connections are resolved back
/// to <c>(node, pinName)</c> using each node's <c>logic.pins</c> id table (identical to how
/// the editor resolves them via <c>FindPinById</c>). This resolver never touches the shared
/// <see cref="PinIdPool"/>, so it is safe even after plugin probing has reserved pin ids.
/// </summary>
public static class EditorJsonLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Load an editor-persisted JSON file into a runnable <see cref="WorkflowGraph"/>.</summary>
    public static WorkflowGraph Load(string path, NodeRegistry registry)
    {
        var doc = JsonSerializer.Deserialize<EditorDocument>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Editor JSON is empty or malformed");
        return Build(doc, registry);
    }

    /// <summary>Build a runnable <see cref="WorkflowGraph"/> from an editor document.</summary>
    public static WorkflowGraph FromDocument(EditorDocument doc, NodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(registry);
        return Build(doc, registry);
    }

    private static WorkflowGraph Build(EditorDocument doc, NodeRegistry registry)
    {
        var graph = new WorkflowGraph();

        // pin id -> (node id, pin name, is output). Resolved purely from the JSON logic.pins
        // tables (direction from the instantiated node by name), mirroring the editor's
        // FindPinById without touching the shared PinIdPool.
        var pinMap = new Dictionary<int, (string NodeId, string PinName, bool IsOutput)>();

        foreach (var spec in doc.Nodes)
        {
            var logic = spec.Logic;
            var functionName = logic?["functionName"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(functionName))
                throw new InvalidDataException($"Node '{spec.Id}' has no logic.functionName.");

            var instance = registry.CreateInstance(functionName);
            LogicDeserializer.Deserialize(instance, logic);

            // Surface restored parameters so WorkflowEngine can re-apply them via Configure.
            var parameters = new Dictionary<string, object?>();
            foreach (var np in instance.NodeParameters)
                if (!string.IsNullOrEmpty(np.Name))
                    parameters[np.Name] = np.Value;

            graph.Nodes.Add(new NodeSpec
            {
                Id = spec.Id,
                FunctionName = functionName,
                Name = string.IsNullOrEmpty(instance.Name) ? null : instance.Name,
                Parameters = parameters,
                X = spec.X,
                Y = spec.Y
            });

            // Map every saved pin id to (node, pinName, direction) from the logic.pins table.
            if (logic?["pins"] is JsonArray pins)
            {
                foreach (var item in pins)
                {
                    if (item is not JsonObject pinObj) continue;
                    var name = pinObj["name"]?.GetValue<string>();
                    var id = pinObj["id"]?.GetValue<int>();
                    if (string.IsNullOrEmpty(name) || id is null) continue;

                    var isOutput = instance.OutputPins.Any(p => p.Name == name);
                    pinMap[id.Value] = (spec.Id, name, isOutput);
                }
            }
        }

        // Resolve saved pin-id connections to (node, pinName) connections. Source must be an
        // output pin and target an input pin (the editor guarantees this).
        foreach (var conn in doc.Connections)
        {
            if (!pinMap.TryGetValue(conn.FromPinId, out var from)) continue;
            if (!pinMap.TryGetValue(conn.ToPinId, out var to)) continue;
            if (!from.IsOutput || to.IsOutput) continue;

            graph.Connections.Add(new ConnectionSpec
            {
                FromNode = from.NodeId,
                FromPin = from.PinName,
                ToNode = to.NodeId,
                ToPin = to.PinName
            });
        }

        return graph;
    }
}
