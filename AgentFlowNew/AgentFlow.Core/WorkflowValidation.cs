// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowValidation.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

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
        var nodesById = graph.Nodes.ToDictionary(n => n.Id);

        // Duplicate ids / unknown types.
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            if (!seenIds.Add(node.Id))
                errors.Add($"Duplicate node id '{node.Id}'.");
            if (!registry.Contains(node.TypeId))
                errors.Add($"Node '{node.Id}' references unknown type '{node.TypeId}'.");
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
            if (!registry.Contains(fromNode.TypeId) || !registry.Contains(toNode.TypeId))
                continue;

            var fromDesc = registry.Get(fromNode.TypeId);
            var toDesc = registry.Get(toNode.TypeId);

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

            if (!toPin.DataType.IsAssignableFrom(fromPin.DataType))
                errors.Add($"Type mismatch: {conn.FromNode}.{conn.FromPin} ({fromPin.DataType.Name}) -> {conn.ToNode}.{conn.ToPin} ({toPin.DataType.Name}).");

            var targetKey = $"{conn.ToNode}.{conn.ToPin}";
            if (!connectedTargets.Add(targetKey))
                errors.Add($"Input pin '{targetKey}' has multiple incoming connections.");
        }

        // Required inputs that are not connected.
        foreach (var node in graph.Nodes)
        {
            if (!registry.Contains(node.TypeId))
                continue;
            var desc = registry.Get(node.TypeId);
            foreach (var pin in desc.InputPins.Where(p => p.Required))
            {
                if (!graph.Connections.Any(c => c.ToNode == node.Id && c.ToPin == pin.Name))
                    errors.Add($"Node '{node.Id}' is missing required input '{pin.Name}'.");
            }
        }

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
}


