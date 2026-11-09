// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowValidation.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
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

        // Ports can be parameter-driven: pin DataType is mutable, so validation must inspect
        // the configured node instance instead of the plugin descriptor's default pin snapshot.
        var configuredInstances = new Dictionary<string, BaseNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            if (!registry.Contains(node.Name))
                continue;
            try
            {
                var instance = registry.CreateInstance(node.Name);
                instance.Configure(ParameterConversion.Normalize(node.Parameters));
                configuredInstances.TryAdd(node.Id, instance);
            }
            catch (Exception ex)
            {
                errors.Add($"Node '{node.Id}' failed to configure: {ex.Message}");
            }
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

            var fromInstance = configuredInstances.GetValueOrDefault(conn.FromNode);
            var toInstance = configuredInstances.GetValueOrDefault(conn.ToNode);
            if (fromInstance is null || toInstance is null)
                continue;

            var fromPin = fromInstance.OutputPins.FirstOrDefault(p => p.Name == conn.FromPin);
            var toPin = toInstance.InputPins.FirstOrDefault(p => p.Name == conn.ToPin);

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
            var instance = configuredInstances.GetValueOrDefault(node.Id);
            if (instance is null)
                continue;
            foreach (var pin in instance.InputPins.Where(p => p.Required))
            {
                if (!graph.Connections.Any(c => c.ToNode == node.Id && c.ToPin == pin.Name))
                    errors.Add($"Node '{node.Id}' is missing required input '{pin.Name}'.");
            }
        }

        // DependsOn startup dependency checks: every referenced instance must exist, self-dependencies
        // are invalid, and the dependency graph must be acyclic. DependsOn accepts the new array/string
        // forms and the legacy single integer.
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
            foreach (var dependsOn in ToIntList(GetParameter(node, "DependsOn")))
            {
                if (dependsOn == instanceId)
                    errors.Add($"Node '{node.Id}' depends on itself (DependsOn = {dependsOn}).");
                else if (!byInstanceId.ContainsKey(dependsOn))
                    errors.Add($"Node '{node.Id}' depends on missing node instance #{dependsOn}.");
            }
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
    /// <summary>
    /// Convert a DependsOn value to a normalized list. Accepts a typed list, a legacy integer,
    /// a comma/semicolon-separated string, or a JSON array/string/number.
    /// </summary>
    internal static IReadOnlyList<int> ToIntList(object? value)
    {
        var ids = new List<int>();

        switch (value)
        {
            case null:
                return Array.Empty<int>();
            case IEnumerable<int> values:
                ids.AddRange(values);
                break;
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (var item in array.EnumerateArray())
                    AddJsonInt(item, ids);
                break;
            case JsonElement element:
                AddJsonInt(element, ids);
                break;
            case string text:
                foreach (var part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(part, out var parsed))
                        ids.Add(parsed);
                }
                break;
            case int or long or short or byte:
                ids.Add(Convert.ToInt32(value));
                break;
        }

        return BaseNode.NormalizeDependsOn(ids);
    }

    private static void AddJsonInt(JsonElement element, ICollection<int> ids)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            ids.Add(number);
            return;
        }

        if (element.ValueKind != JsonValueKind.String)
            return;

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (int.TryParse(text, out var single))
        {
            ids.Add(single);
            return;
        }

        foreach (var part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var parsed))
                ids.Add(parsed);
        }
    }
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
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<NodeSpec>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var start in graph.Nodes)
        {
            foreach (var cycle in Visit(start))
                yield return cycle;
        }

        IEnumerable<string> Visit(NodeSpec node)
        {
            state[node.Id] = 1;
            stack.Add(node);

            foreach (var dependencyId in ToIntList(GetParameter(node, "DependsOn")))
            {
                if (!byInstanceId.TryGetValue(dependencyId, out var dependency) ||
                    string.Equals(dependency.Id, node.Id, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!state.TryGetValue(dependency.Id, out var dependencyState))
                {
                    foreach (var nestedCycle in Visit(dependency))
                        yield return nestedCycle;
                    continue;
                }

                if (dependencyState != 1)
                    continue;

                var cycleStart = stack.FindIndex(n => string.Equals(n.Id, dependency.Id, StringComparison.OrdinalIgnoreCase));
                if (cycleStart < 0)
                    continue;

                var cycleNodes = stack.Skip(cycleStart).Select(n => n.Id).ToList();
                var key = string.Join(" -> ", cycleNodes.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
                if (reported.Add(key))
                    yield return $"DependsOn cycle: {string.Join(" -> ", cycleNodes)} -> {dependency.Id}.";
            }

            stack.RemoveAt(stack.Count - 1);
            state[node.Id] = 2;
        }
    }
}




