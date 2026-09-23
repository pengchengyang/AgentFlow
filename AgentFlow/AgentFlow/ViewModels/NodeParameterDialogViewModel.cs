// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeParameterDialogViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// The "parameter configuration" dialog ViewModel shown when double-clicking a node.
/// Owns the parameter collection, confirm / cancel commands, and writes edited values
/// back to the node on confirm. Only handles business / state; window presentation and
/// lifecycle are handled by the View layer.
/// </summary>
public partial class NodeParameterDialogViewModel : ViewModelBase
{
    /// <summary>The target node being edited.</summary>
    public NodeViewModel Node { get; }

    /// <summary>Dialog title.</summary>
    public string Title => Node.Title;

    /// <summary>The editable parameter row collection (edits a copy; values are written back only on confirm).</summary>
    public ObservableCollection<ParameterEditRow> Parameters { get; } = new();

    /// <summary>Whether there are no parameters (used to show an empty-state hint).</summary>
    public bool HasNoParameters => Parameters.Count == 0;

    /// <summary>Request close: parameter true = confirm, false = cancel. The View layer listens and calls Close.</summary>
    public event EventHandler<bool>? RequestClose;

    public NodeParameterDialogViewModel(NodeViewModel node)
    {
        Node = node;
        foreach (var p in node.Parameters)
            Parameters.Add(new ParameterEditRow(p.Key, p.Label, p.Value, p.TypeName));
    }

    /// <summary>Confirm: write the edited values back to the node parameters, then request close.</summary>
    [RelayCommand]
    private void Confirm()
    {
        foreach (var row in Parameters)
        {
            var target = Node.Parameters.FirstOrDefault(p => p.Key == row.Key);
            if (target is not null)
                target.Value = row.Value;
        }
        RequestClose?.Invoke(this, true);
    }

    /// <summary>Cancel: do not write back any values; request close directly.</summary>
    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}

/// <summary>A single parameter edit row in the dialog (Key / Label read-only; Value bound for editing).</summary>
public partial class ParameterEditRow : ViewModelBase
{
    public string Key { get; }
    public string Label { get; }
    public string TypeName { get; }

    [ObservableProperty]
    private string _value;

    public ParameterEditRow(string key, string label, string value, string typeName)
    {
        Key = key;
        Label = label;
        TypeName = typeName;
        _value = value;
    }
}
