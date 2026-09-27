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
    /// Group identifier used to group related parameters together.
    /// Parameters sharing the same group are shown / handled as one section.
    /// Null or empty means the parameter belongs to no particular group.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>Default constructor: marks the parameter as editable (type defaults to object).</summary>
    public NodeParameter()
    {
        Type = typeof(object);
        IsEditable = true;
    }

    public NodeParameter(string name, Type type, object? value, bool isEditable = true, string? group = null)
    {
        Name = name;
        Type = type;
        Value = value;
        IsEditable = isEditable;
        Group = group;
    }

    public NodeParameter(Type type, object? value, bool isEditable = true, string? group = null)
    {
        Type = type;
        Value = value;
        IsEditable = isEditable;
        Group = group;
    }
}
