// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeAttribute.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Marks a class as an AgentFlow node so the PluginLoader can discover it via reflection.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class NodeAttribute : Attribute
{
    /// <summary>Globally unique node type id, e.g. "basic.add". Serialized into JSON.</summary>
    public new string TypeId { get; }

    /// <summary>Display name in the UI.</summary>
    public string DisplayName { get; }

    /// <summary>Node category (used for palette grouping and accent color).</summary>
    public string Category { get; }

    public NodeAttribute(string typeId, string displayName, string category = "General")
    {
        TypeId = typeId;
        DisplayName = displayName;
        Category = category;
    }
}
