// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LogicSerializer.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json.Nodes;
using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Serializes <b>only</b> the logical parameters of a node instance
/// (<see cref="BaseNode.Uuid"/>, <see cref="BaseNode.TypeId"/>,
/// <see cref="BaseNode.InstanceId"/> and any subclass-specific data).
/// It deliberately ignores all GUI state such as coordinates, node id, name,
/// priority and connections, so the Core layer stays free of any UI dependency.
/// The resulting JSON blob is consumed by the upper-layer <c>GraphSerializer</c>
/// (in the AgentFlow project), which merges it with the GUI state.
/// </summary>
public static class LogicSerializer
{
    /// <summary>
    /// Produce the logical-parameter JSON blob for a node instance.
    /// Ignores all GUI state.
    /// </summary>
    /// <param name="node">The contract-layer node instance to serialize.</param>
    /// <returns>A JSON object containing only the node's logical parameters.</returns>
    public static JsonObject Serialize(BaseNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var json = new JsonObject();
        node.SerializeParameters(json);
        return json;
    }
}
