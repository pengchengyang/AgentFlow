// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeParameterDialogViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using AgentFlow.Contracts;
using AgentFlow.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// The "parameter configuration" dialog ViewModel shown when double-clicking a node.
/// Owns the grouped parameter tree, confirm / cancel commands, and writes edited values
/// back to the node on confirm. Only handles business / state; window presentation and
/// lifecycle are handled by the View layer.
/// </summary>
public partial class NodeParameterDialogViewModel : ViewModelBase
{
    private const string DefaultGroup = "General";

    /// <summary>The target node being edited.</summary>
    public NodeViewModel Node { get; }

    /// <summary>Dialog title.</summary>
    public string Title => Node.Title;

    /// <summary>Localized dialog title (node name + "Parameter Configuration").</summary>
    public string DialogTitle => $"{Title} - {Loc.Instance["ParamConfig"]}";

    /// <summary>The grouped parameter tree (group node -> parameter rows).</summary>
    public ObservableCollection<ParameterGroupNode> Groups { get; } = new();

    /// <summary>Whether there are no parameters (used to show an empty-state hint).</summary>
    public bool HasNoParameters => Groups.Count == 0;

    /// <summary>Request close: parameter true = confirm, false = cancel. The View layer listens and calls Close.</summary>
    public event EventHandler<bool>? RequestClose;

    public NodeParameterDialogViewModel(NodeViewModel node)
    {
        Node = node;
        var groups = new Dictionary<string, ParameterGroupNode>(StringComparer.Ordinal);

        // 1. Real parameters attached via AddParameter, grouped by their NodeParameter.Group
        //    (this is the data that round-trips through logic.parameters).
        foreach (var p in node.Model.Node.NodeParameters)
        {
            if (string.IsNullOrEmpty(p.Name)) continue;
            var groupName = string.IsNullOrEmpty(p.Group) ? DefaultGroup : p.Group!;
            var row = new ParameterEditRow(
                p.Name,
                p.Name,
                p.Value?.ToString() ?? "",
                p.Type.Name,
                p,
                p.IsEditable);
            AddRow(groups, groupName, row);
        }

        // 2. Declared ParameterDefinitions as a fallback, so nodes that do not yet use
        //    AddParameter still show their editable rows under a default group.
        foreach (var def in node.Parameters)
        {
            if (groups.Values.SelectMany(g => g.Children).Any(r => r.Key == def.Key)) continue;
            var row = new ParameterEditRow(
                def.Key,
                def.Label,
                def.Value,
                def.TypeName,
                isEditable: true);
            AddRow(groups, DefaultGroup, row);
        }

        foreach (var g in groups.Values)
            Groups.Add(g);
    }

    /// <summary>Confirm: write the edited values back to the node parameters, then request close.</summary>
    [RelayCommand]
    private void Confirm()
    {
        foreach (var row in Groups.SelectMany(g => g.Children))
        {
            var target = Node.Parameters.FirstOrDefault(p => p.Key == row.Key);
            if (target is not null)
                target.Value = row.Value;

            if (row.Source is not null)
                row.Source.Value = ConvertValue(row.Value, row.Source.Type);
        }
        // Push the edited built-in General parameters (DisplayName, DependsOn) back onto
        // the node instance and refresh the canvas title.
        Node.Model.Node.ApplyBuiltInParameters();
        Node.Name = Node.Model.Node.Name;
        RequestClose?.Invoke(this, true);
    }

    /// <summary>Cancel: do not write back any values; request close directly.</summary>
    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);

    private static void AddRow(
        Dictionary<string, ParameterGroupNode> groups,
        string groupName,
        ParameterEditRow row)
    {
        if (!groups.TryGetValue(groupName, out var group))
        {
            group = new ParameterGroupNode(groupName);
            groups[groupName] = group;
        }
        group.Children.Add(row);
    }

    private static object? ConvertValue(string? raw, Type type)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            if (type == typeof(string)) return raw;
            if (type == typeof(bool)) return bool.Parse(raw);
            if (type == typeof(int)) return int.Parse(raw);
            if (type == typeof(long)) return long.Parse(raw);
            if (type == typeof(double)) return double.Parse(raw);
            if (type == typeof(float)) return float.Parse(raw);
            if (type.IsEnum) return Enum.Parse(type, raw);
            return Convert.ChangeType(raw, type);
        }
        catch
        {
            return raw;   // Fall back to the raw string; the node handles it.
        }
    }
}

/// <summary>A tree node for one parameter group (its children are the parameter edit rows).</summary>
public sealed class ParameterGroupNode
{
    public string Name { get; }
    public ObservableCollection<ParameterEditRow> Children { get; } = new();

    public ParameterGroupNode(string name) => Name = name;
}

/// <summary>A single parameter edit row in the dialog (Key / Label read-only; Value bound for editing).</summary>
public partial class ParameterEditRow : ViewModelBase
{
    public string Key { get; }
    public string Label { get; }
    public string TypeName { get; }

    /// <summary>Optional contract-layer parameter this row edits (null for declaration-only rows).</summary>
    public NodeParameter? Source { get; }

    /// <summary>Whether the user is allowed to edit this parameter's value.</summary>
    public bool IsEditable { get; }

    [ObservableProperty]
    private string _value;

    public ParameterEditRow(
        string key,
        string label,
        string value,
        string typeName,
        NodeParameter? source = null,
        bool isEditable = true)
    {
        Key = key;
        Label = label;
        TypeName = typeName;
        _value = value;
        Source = source;
        IsEditable = isEditable;
    }
}
