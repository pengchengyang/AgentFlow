// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowValidation.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;

namespace AgentFlow.Core;

/// <summary>
/// Pre-flight graph validation and topological ordering.
/// Mirrors ALC's pin-compatibility and startup-priority ideas while staying
/// JSON-first and managed-code friendly.
/// </summary>
public static class WorkflowValidation
{
    /// <summary>Returns a list of human-readable problems (empty when the graph is valid).</summary>
    public static IReadOnlyList<string> Validate(NodeRegistry registry, WorkflowGraph graph)
    {
        var errors = new List<string>();
        var nodesById = new Dictionary<string, NodeSpec>(StringComparer.OrdinalIgnoreCase);

        // Build the id index first with TryAdd: ToDictionary would throw ArgumentException on the
        // first duplicate id, so the "Duplicate node id" error below would never be collected.
        foreach (var node in graph.Nodes)
        {
            if (!nodesById.TryAdd(node.Id, node))
                errors.Add($"Duplicate node id '{node.Id}'.");
            if (!registry.Contains(node.Name))
                errors.Add($"Node '{node.Id}' references unknown type '{node.Name}'.");
        }

        // Per-input connection count and pin/type checks.
        var connectedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conn in graph.Connections)
        {
            if (!nodesById.TryGetValue(conn.FromNode, out var fromNode))
            {
                errors.Add($"Connection references missing source node '{conn.FromNode}'.");
                continue;
            }
            if (!nodesById.TryGetValue(conn.ToNode, out var toNode))
            {
                errors.Add($"Connection references missing target node '{conn.ToNode}'.");
                continue;
            }

            // Unknown types are reported above; skip detailed pin checks for them.
            if (!registry.Contains(fromNode.Name) || !registry.Contains(toNode.Name))
                continue;

            var fromDesc = registry.Get(fromNode.Name);
            var toDesc = registry.Get(toNode.Name);

            var fromPin = fromDesc.OutputPins.FirstOrDefault(p => p.Name == conn.FromPin);
            var toPin = toDesc.InputPins.FirstOrDefault(p => p.Name == conn.ToPin);

            if (fromPin is null)
            {
                errors.Add($"Node '{conn.FromNode}' has no output pin '{conn.FromPin}'.");
                continue;
            }
            if (toPin is null)
            {
                errors.Add($"Node '{conn.ToNode}' has no input pin '{conn.ToPin}'.");
                continue;
            }

            if (toPin.DataType != fromPin.DataType)
                errors.Add($"Type mismatch: {conn.FromNode}.{conn.FromPin} ({fromPin.DataType}) -> {conn.ToNode}.{conn.ToPin} ({toPin.DataType}).");

            var targetKey = $"{conn.ToNode}.{conn.ToPin}";
            if (!connectedTargets.Add(targetKey))
                errors.Add($"Input pin '{targetKey}' has multiple incoming connections.");
        }

        // Required inputs that are not connected.
        foreach (var node in graph.Nodes)
        {
            if (!registry.Contains(node.Name))
                continue;
            var desc = registry.Get(node.Name);
            foreach (var pin in desc.InputPins.Where(p => p.Required))
            {
                if (!graph.Connections.Any(c => c.ToNode == node.Id && c.ToPin == pin.Name))
                    errors.Add($"Node '{node.Id}' is missing required input '{pin.Name}'.");
            }
        }

        // DependsOn (startup dependency) checks: the referenced instance id must exist in the graph,
        // must not be the node itself, and the dependency graph must not contain a cycle.
        var byInstanceId = new Dictionary<int, NodeSpec>();
        foreach (var node in graph.Nodes)
        {
            var instanceId = ToInt(GetParameter(node, "InstanceId"));
            if (instanceId == 0) continue;
            if (!byInstanceId.TryAdd(instanceId, node))
                errors.Add($"Duplicate node instance id #{instanceId} ('{byInstanceId[instanceId].Id}' and '{node.Id}').");
        }

        foreach (var node in graph.Nodes)
        {
            var instanceId = ToInt(GetParameter(node, "InstanceId"));
            var dependsOn = ToInt(GetParameter(node, "DependsOn"));
            if (dependsOn == 0) continue;
            if (dependsOn == instanceId)
                errors.Add($"Node '{node.Id}' depends on itself (DependsOn = {dependsOn}).");
            else if (!byInstanceId.ContainsKey(dependsOn))
                errors.Add($"Node '{node.Id}' depends on missing node instance #{dependsOn}.");
        }

        foreach (var cycle in FindDependencyCycles(graph, byInstanceId))
            errors.Add(cycle);

        // Cycle detection.
        try
        {
            TopologicalSort(graph);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
        }

        return errors;
    }

    /// <summary>
    /// Kahn's algorithm with ALC-style startup priority used as a tie-breaker:
    /// when several nodes are ready at the same time, smaller Priority runs first.
    /// </summary>
    public static List<string> TopologicalSort(WorkflowGraph graph)
    {
        if (graph.Nodes.Select(n => n.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != graph.Nodes.Count)
            throw new InvalidOperationException("Duplicate node ids; cannot topologically sort.");

        var indegree = graph.Nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var c in graph.Connections)
            indegree[c.ToNode]++;

        var order = new List<string>(graph.Nodes.Count);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (order.Count < graph.Nodes.Count)
        {
            var ready = graph.Nodes
                .Where(n => indegree[n.Id] == 0 && !added.Contains(n.Id))
                .OrderBy(n => n.Priority)
                .ThenBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ready.Count == 0)
                throw new InvalidOperationException("The workflow contains a cycle; cannot topologically sort.");

            var next = ready[0];
            added.Add(next.Id);
            order.Add(next.Id);

            foreach (var c in graph.Connections.Where(c => c.FromNode == next.Id))
                indegree[c.ToNode]--;
        }

        return order;
    }

    /// <summary>Read a built-in parameter value (exact key first, then case-insensitive).</summary>
    private static object? GetParameter(NodeSpec node, string name)
        => node.Parameters.TryGetValue(name, out var value)
            ? value
            : node.Parameters.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Convert a built-in parameter value (native, JSON element or numeric string) to an int.</summary>
    internal static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
        string s when int.TryParse(s, out var parsed) => parsed,
        JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n) => n,
        JsonElement je when je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var s2) => s2,
        _ => 0,
    };

    /// <summary>Report one message per DependsOn cycle, each cycle only once.</summary>
    private static IEnumerable<string> FindDependencyCycles(
        WorkflowGraph graph,
        IReadOnlyDictionary<int, NodeSpec> byInstanceId)
    {
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in graph.Nodes)
        {
            var path = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var node = start;
            while (true)
            {
                if (index.TryGetValue(node.Id, out var begin))
                {
                    var cycle = path.Skip(begin).ToList();
                    var key = string.Join(" -> ", cycle.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                    if (reported.Add(key))
                        yield return $"DependsOn cycle: {string.Join(" -> ", cycle)} -> {node.Id}.";
                    break;
                }

                index[node.Id] = path.Count;
                path.Add(node.Id);

                var dependsOn = ToInt(GetParameter(node, "DependsOn"));
                if (dependsOn == 0) break;
                if (!byInstanceId.TryGetValue(dependsOn, out var target)) break;
                if (string.Equals(target.Id, node.Id, StringComparison.OrdinalIgnoreCase)) break;
                node = target;
            }
        }
    }
}




