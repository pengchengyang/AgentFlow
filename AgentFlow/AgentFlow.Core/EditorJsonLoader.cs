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
/// the editor resolves them via <c>FindPinById</c>). It never allocates or reserves pin ids, so
/// headless runs do not consume the editor's persistence ids at all.
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
        if (doc.Version != "1.0")
            throw new InvalidDataException(
                $"Unsupported editor document version '{doc.Version}'; this build supports 1.0.");
        if (doc.Nodes is null || doc.Connections is null)
            throw new InvalidDataException("Editor document has null nodes/connections.");

        var graph = new WorkflowGraph();

        // pin id -> (node id, pin name, is output). Resolved purely from the JSON logic.pins
        // tables (direction from the instantiated node by name), mirroring the editor's
        // FindPinById without allocating any pin id.
        var pinMap = new Dictionary<int, (string NodeId, string PinName, bool IsOutput)>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var spec in doc.Nodes)
        {
            if (string.IsNullOrEmpty(spec.Id) || !nodeIds.Add(spec.Id))
                throw new InvalidDataException($"Duplicate or empty node id: '{spec.Id}'.");

            var logic = spec.Logic;
            var name = JsonValueReader.GetString(logic, "name") ?? "";
            if (string.IsNullOrEmpty(name))
                throw new InvalidDataException($"Node '{spec.Id}' has no logic.name.");

            var instance = registry.CreateInstance(name);
            LogicDeserializer.Deserialize(instance, logic);

            // Surface restored parameters so WorkflowEngine can re-apply them via Configure.
            var parameters = new Dictionary<string, object?>();
            foreach (var np in instance.NodeParameters)
                if (!string.IsNullOrEmpty(np.Name))
                    parameters[np.Name] = np.Value;

            graph.Nodes.Add(new NodeSpec
            {
                Id = spec.Id,
                Name = name,
                DisplayName = string.IsNullOrEmpty(instance.DisplayName) ? null : instance.DisplayName,
                Parameters = parameters,
                X = spec.X,
                Y = spec.Y
            });

            // Map every saved pin id to (node, pinName, direction) from the logic.pins table.
            if (logic?["pins"] is JsonArray pins)
            {
                foreach (var item in pins)
                {
                    if (item is not JsonObject pinObj)
                        throw new InvalidDataException($"Node '{spec.Id}' has a non-object entry in logic.pins.");

                    var pinName = JsonValueReader.GetString(pinObj, "name");
                    var id = JsonValueReader.GetInt(pinObj, "id");
                    if (string.IsNullOrEmpty(pinName) || id <= 0)
                        throw new InvalidDataException(
                            $"Node '{spec.Id}' has a pin with a missing name or non-positive id ({id}).");

                    var isInput = instance.InputPins.Any(p => p.Name == pinName);
                    var isOutput = instance.OutputPins.Any(p => p.Name == pinName);
                    if (!isInput && !isOutput)
                        throw new InvalidDataException(
                            $"Node '{spec.Id}' declares unknown pin '{pinName}'.");
                    if (isInput && isOutput)
                        throw new InvalidDataException(
                            $"Node '{spec.Id}' pin '{pinName}' exists as both input and output.");

                    if (!pinMap.TryAdd(id, (spec.Id, pinName, isOutput)))
                        throw new InvalidDataException(
                            $"Duplicate pin id {id} on node '{spec.Id}' (already used by '{pinMap[id].NodeId}').");
                }
            }
        }

        // Resolve saved pin-id connections to (node, pinName) connections. Source must be an
        // output pin and target an input pin (the editor guarantees this).
        foreach (var conn in doc.Connections)
        {
            if (!pinMap.TryGetValue(conn.FromPinId, out var from))
                throw new InvalidDataException(
                    $"Connection references missing source pin id {conn.FromPinId}.");
            if (!pinMap.TryGetValue(conn.ToPinId, out var to))
                throw new InvalidDataException(
                    $"Connection references missing target pin id {conn.ToPinId}.");
            if (!from.IsOutput || to.IsOutput)
                throw new InvalidDataException(
                    $"Connection {conn.FromPinId} -> {conn.ToPinId} is not output -> input.");
            if (string.Equals(from.NodeId, to.NodeId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Connection {conn.FromPinId} -> {conn.ToPinId} connects a node to itself.");

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
