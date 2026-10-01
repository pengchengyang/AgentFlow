// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeParameter.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// A parameter attached to a node: its name, data type, current value, whether the
/// user may edit it in the UI, and an optional <see cref="Group"/> that groups
/// related parameters together. The default (parameterless) constructor marks the
/// parameter as editable; the full constructor lets you supply name / type / value
/// and an explicit editable flag (e.g. <c>new NodeParameter("Gain", typeof(double), 4.0)</c>).
/// </summary>
public sealed class NodeParameter
{
    /// <summary>Parameter name (used as the JSON key inside its group).</summary>
    public string Name { get; set; } = "";

    /// <summary>Parameter data type.</summary>
    public Type Type { get; set; }

    /// <summary>Current value.</summary>
    public object? Value { get; set; }

    /// <summary>Whether the user may edit this parameter in the UI.</summary>
    public bool IsEditable { get; set; }

    /// <summary>
    /// Zone identifier: the top-level section this parameter belongs to. Zones form the
    /// left-hand navigation list of the parameter dialog; each zone owns one or more
    /// <see cref="Group"/> sections. Null or empty means the default zone.
    /// </summary>
    public string? Zone { get; set; }

    /// <summary>
    /// Group identifier used to group related parameters together inside a <see cref="Zone"/>.
    /// Parameters sharing the same group are shown / handled as one collapsible section.
    /// Null or empty means the parameter belongs to no particular group.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>Default constructor: marks the parameter as editable (type defaults to object).</summary>
    public NodeParameter()
    {
        Type = typeof(object);
        IsEditable = true;
    }

    /// <param name="group">Collapsible section inside the zone; null/empty = default group.</param>
    /// <param name="zone">Top-level section (dialog left-hand list); null/empty = default zone.</param>
    public NodeParameter(string name, Type type, object? value, bool isEditable = true, string? group = null, string? zone = null)
    {
        Name = name;
        Type = type;
        Value = value;
        IsEditable = isEditable;
        Zone = zone;
        Group = group;
    }

    public NodeParameter(Type type, object? value, bool isEditable = true, string? group = null, string? zone = null)
    {
        Type = type;
        Value = value;
        IsEditable = isEditable;
        Zone = zone;
        Group = group;
    }
}
