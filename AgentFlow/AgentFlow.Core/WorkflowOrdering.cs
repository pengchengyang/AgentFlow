// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowOrdering.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Single source of truth for the node start order: a node appears after the node its
/// <see cref="BaseNode.DependsOn"/> points at. Both the GUI (through <see cref="PluginLoader"/>)
/// and the headless engine (<see cref="WorkflowEngine"/>) call this, so the same workflow starts
/// in the same order in both modes (previously only the GUI honoured DependsOn).
/// </summary>
public static class WorkflowOrdering
{
    /// <summary>
    /// Return <paramref name="nodes"/> in dependency order (Kahn). Every dependency edge contributes
    /// to a node's indegree, so a node with <c>DependsOn = [1001, 1002, 1003]</c> is ordered after all
    /// three nodes. Missing dependencies are treated as independent here; validation reports them.
    /// A dependency cycle falls back to the input order for nodes that could not be placed.
    /// </summary>
    public static List<BaseNode> SortByDependencies(IReadOnlyList<BaseNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var byId = new Dictionary<int, BaseNode>();
        foreach (var node in nodes)
            byId.TryAdd(node.InstanceId, node);

        var dependents = new Dictionary<int, List<BaseNode>>();
        var indegree = new Dictionary<BaseNode, int>();
        foreach (var node in nodes)
            indegree[node] = 0;

        foreach (var node in nodes)
        {
            foreach (var dependencyId in BaseNode.NormalizeDependsOn(node.DependsOn))
            {
                if (!byId.TryGetValue(dependencyId, out var dependency) ||
                    ReferenceEquals(dependency, node))
                    continue;

                if (!dependents.TryGetValue(dependency.InstanceId, out var list))
                {
                    list = new List<BaseNode>();
                    dependents[dependency.InstanceId] = list;
                }

                list.Add(node);
                indegree[node]++;
            }
        }

        var queue = new Queue<BaseNode>(nodes.Where(n => indegree[n] == 0));
        var sorted = new List<BaseNode>(nodes.Count);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            sorted.Add(node);

            if (!dependents.TryGetValue(node.InstanceId, out var next))
                continue;

            foreach (var dependent in next)
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (sorted.Count < nodes.Count)
            sorted.AddRange(nodes.Where(n => !sorted.Contains(n)));

        return sorted;
    }
}


