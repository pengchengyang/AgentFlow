// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LogicDeserializer.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json.Nodes;
using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Deserializes <b>only</b> the logical parameters of a node instance from a JSON
/// object. It restores <see cref="BaseNode.Uuid"/>, <see cref="BaseNode.TypeId"/>,
/// <see cref="BaseNode.InstanceId"/> and any subclass-specific data while ignoring
/// all GUI fields such as coordinate positions, node id, name and priority.
/// The upper-layer <c>GraphDeserializer</c> (in the AgentFlow project) extracts the
/// logical blob from the full graph document and passes it here.
/// </summary>
public static class LogicDeserializer
{
    /// <summary>
    /// Restore a node instance's logical parameters from its JSON blob.
    /// GUI fields in the JSON are ignored.
    /// </summary>
    /// <param name="node">The contract-layer node instance to restore into.</param>
    /// <param name="json">The logical-parameter JSON blob (may be null).</param>
    public static void Deserialize(BaseNode node, JsonObject? json)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (json is null) return;
        node.DeserializeParameters(json);
    }
}
