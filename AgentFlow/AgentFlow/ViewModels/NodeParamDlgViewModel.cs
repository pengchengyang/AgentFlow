// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeParamDlgViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using AgentFlow.Contracts;
using AgentFlow.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// The "parameter configuration" dialog ViewModel shown when double-clicking a node.
/// <para>
/// Three levels, mirroring a classic property-grid dialog:
/// <list type="bullet">
/// <item><b>Zone</b> — the left-hand navigation list (<see cref="Zones"/>).</item>
/// <item><b>Group</b> — a collapsible section inside the selected zone.</item>
/// <item><b>Parameter</b> — one editable row inside a group.</item>
/// </list>
/// The right-hand side renders the selected zone as a flat list of group headers and
/// their rows (<see cref="VisibleItems"/>), so selection stays unique across all groups.
/// </para>
/// Only handles business / state; window presentation and lifecycle are handled by the View layer.
/// </summary>
public partial class NodeParamDlgViewModel : ViewModelBase
{
    /// <summary>Zone used when a parameter does not declare one.</summary>
    private const string DefaultZone = "General";

    /// <summary>Group used when a parameter does not declare one.</summary>
    private const string DefaultGroup = "General";

    /// <summary>The target node being edited.</summary>
    public NodeViewModel Node { get; }

    /// <summary>Dialog title.</summary>
    public string Title => Node.Title;

    /// <summary>Localized dialog title (node name + "Parameter Configuration").</summary>
    public string DialogTitle => $"{Title} - {Loc.Instance["ParamConfig"]}";

    /// <summary>Title-bar caption: main title.</summary>
    public string Caption => Loc.Instance["Configuration"];

    /// <summary>Title-bar subtitle: node name · instance id.</summary>
    public string Subtitle => $"{Title} · #{Node.InstanceId}";

    /// <summary>Raised after edited values have been written back, so the editor can mark itself dirty.</summary>
    public event EventHandler? NodeChanged;

    /// <summary>Zones shown in the left-hand navigation list.</summary>
    public ObservableCollection<ParameterZoneNode> Zones { get; } = new();

    /// <summary>Whether there are no parameters (used to show an empty-state hint).</summary>
    public bool HasNoParameters => Zones.Count == 0;

    /// <summary>
    /// Flat content of the right-hand pane for the selected zone: each group header
    /// (<see cref="ParameterGroupNode"/>) followed by its rows (<see cref="ParameterEditRow"/>)
    /// while the group is expanded.
    /// </summary>
    public ObservableCollection<object> VisibleItems { get; } = new();

    /// <summary>Currently selected zone; drives the right-hand pane.</summary>
    [ObservableProperty]
    private ParameterZoneNode? _selectedZone;

    /// <summary>Currently selected entry of the right-hand list (a parameter row, or a group header).</summary>
    [ObservableProperty]
    private object? _selectedItem;

    /// <summary>Currently selected parameter row; drives the description pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailTitle))]
    [NotifyPropertyChangedFor(nameof(DetailDescription))]
    private ParameterEditRow? _selectedRow;

    /// <summary>True when any value has been edited since the last Apply / open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _isDirty;

    /// <summary>Apply is available only while there are pending edits.</summary>
    public bool CanApply => IsDirty;

    /// <summary>Description pane heading: the selected property name.</summary>
    public string DetailTitle => SelectedRow?.Label ?? string.Empty;

    /// <summary>Description pane body: the property description, falling back to its data type.</summary>
    public string DetailDescription => SelectedRow?.Description ?? SelectedRow?.TypeName ?? string.Empty;

    /// <summary>Request close: parameter true = confirm, false = cancel. The View layer listens and calls Close.</summary>
    public event EventHandler<bool>? RequestClose;

    /// <summary>Raised when the user switches zones while there are unsaved edits; the View shows a confirmation dialog.</summary>
    public event EventHandler? RequestApplyConfirm;

    private ParameterZoneNode? _lastZone;
    private ParameterZoneNode? _pendingZone;
    private bool _isProgrammaticZoneChange;

    public NodeParamDlgViewModel(NodeViewModel node)
    {
        Node = node;
        var zones = new Dictionary<string, ParameterZoneNode>(StringComparer.Ordinal);
        var groupsByZone = new Dictionary<string, Dictionary<string, ParameterGroupNode>>(StringComparer.Ordinal);

        // 1. Real parameters attached via AddParameter, grouped by zone + NodeParameter.Group
        //    (this is the data that round-trips through logic.parameters).
        foreach (var p in node.Model.Node.NodeParameters)
        {
            if (string.IsNullOrEmpty(p.Name)) continue;
            var zoneName = string.IsNullOrEmpty(p.Zone) ? DefaultZone : p.Zone!;
            var groupName = string.IsNullOrEmpty(p.Group) ? DefaultGroup : p.Group!;
            var row = new ParameterEditRow(
                p.Name,
                p.Name,
                p.Value?.ToString() ?? "",
                p.Type.Name,
                p,
                p.IsEditable,
                editor: p.Editor,
                options: p.Options);
            AddRow(zones, groupsByZone, zoneName, groupName, row);
        }

        // 2. Declared ParameterDefinitions as a fallback, so nodes that do not yet use
        //    AddParameter still show their editable rows under the default zone / group.
        foreach (var def in node.Parameters)
        {
            if (zones.Values.SelectMany(z => z.Groups).SelectMany(g => g.Children).Any(r => r.Key == def.Key))
                continue;
            var row = new ParameterEditRow(
                def.Key,
                def.Label,
                def.Value,
                def.TypeName,
                source: null,
                isEditable: true,
                description: def.Hint,
                editor: def.Editor,
                options: def.Options);
            AddRow(zones, groupsByZone, DefaultZone, DefaultGroup, row);
        }

        foreach (var z in zones.Values)
            Zones.Add(z);

        // Track edits (dirty state) and group expand / collapse (right-hand list rebuild).
        foreach (var row in Zones.SelectMany(z => z.Groups).SelectMany(g => g.Children))
            row.PropertyChanged += OnRowPropertyChanged;
        foreach (var group in Zones.SelectMany(z => z.Groups))
            group.PropertyChanged += OnGroupPropertyChanged;

        SelectedZone = Zones.FirstOrDefault();
    }

    /// <summary>Switching zone rebuilds the right-hand list.</summary>
    partial void OnSelectedZoneChanged(ParameterZoneNode? value)
    {
        // Programmatic switch (revert / commit): just rebuild, keep last-zone in sync.
        if (_isProgrammaticZoneChange)
        {
            _lastZone = value;
            RebuildVisibleItems();
            return;
        }

        // User-initiated switch while there are pending edits: ask before leaving the zone.
        if (IsDirty && value is not null && !ReferenceEquals(value, _lastZone))
        {
            _pendingZone = value;
            // Snap the selection back to the current zone until the user decides.
            _isProgrammaticZoneChange = true;
            SelectedZone = _lastZone;
            _isProgrammaticZoneChange = false;
            RequestApplyConfirm?.Invoke(this, EventArgs.Empty);
            return;
        }

        _lastZone = value;
        RebuildVisibleItems();
    }

    /// <summary>Only parameter rows update the description pane; selecting a group header leaves it unchanged.</summary>
    partial void OnSelectedItemChanged(object? value)
    {
        if (value is ParameterEditRow row)
            SelectedRow = row;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParameterEditRow.Value))
            IsDirty = true;
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParameterGroupNode.IsExpanded))
            RebuildVisibleItems();
    }

    /// <summary>Flatten the selected zone into: group header, then its rows while expanded.</summary>
    private void RebuildVisibleItems()
    {
        VisibleItems.Clear();
        if (SelectedZone is null) return;

        foreach (var group in SelectedZone.Groups)
        {
            VisibleItems.Add(group);
            if (!group.IsExpanded) continue;
            foreach (var row in group.Children)
                VisibleItems.Add(row);
        }
    }

    /// <summary>Confirm: write the edited values back to the node, then close.</summary>
    [RelayCommand]
    private void Confirm()
    {
        WriteBack();
        IsDirty = false;
        RequestClose?.Invoke(this, true);
    }

    /// <summary>Apply: write the edited values back but keep the dialog open.</summary>
    [RelayCommand]
    private void Apply()
    {
        WriteBack();
        IsDirty = false;
    }

    /// <summary>Cancel: do not write back any values; request close directly.</summary>
    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);

    /// <summary>Apply pending edits and switch to the zone the user was heading to (Confirm).</summary>
    public void ApplyPendingZoneChanges()
    {
        WriteBack();
        IsDirty = false;
        CompletePendingSwitch();
    }

    /// <summary>Discard pending edits and switch to the target zone (Cancel).</summary>
    public void DiscardPendingZoneChanges()
    {
        IsDirty = false;
        CompletePendingSwitch();
    }

    private void CompletePendingSwitch()
    {
        if (_pendingZone is null) return;
        var target = _pendingZone;
        _pendingZone = null;
        _isProgrammaticZoneChange = true;
        SelectedZone = target;
        _isProgrammaticZoneChange = false;
    }

    /// <summary>Push every edited row back onto the node parameters and the built-in fields.</summary>
    private void WriteBack()
    {
        foreach (var row in Zones.SelectMany(z => z.Groups).SelectMany(g => g.Children))
        {
            var target = Node.Parameters.FirstOrDefault(p => p.Key == row.Key);
            if (target is not null)
                target.Value = row.Value;

            if (row.Source is not null)
                row.Source.Value = ConvertValue(row.Value, row.Source.Type);
        }
        // Push the edited built-in General parameters (Name, DependsOn) back onto
        // the node instance and refresh the canvas title.
        Node.Model.Node.ApplyBuiltInParameters();
        Node.DisplayName = Node.Model.Node.DisplayName ?? "";
        Node.NotifyDependsOnChanged();
        // The node's Title / DisplayName may have changed, so refresh the dialog's own title-bar text.
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(DialogTitle));
        NodeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void AddRow(
        Dictionary<string, ParameterZoneNode> zones,
        Dictionary<string, Dictionary<string, ParameterGroupNode>> groupsByZone,
        string zoneName,
        string groupName,
        ParameterEditRow row)
    {
        if (!zones.TryGetValue(zoneName, out var zone))
        {
            zone = new ParameterZoneNode(zoneName);
            zones[zoneName] = zone;
            groupsByZone[zoneName] = new Dictionary<string, ParameterGroupNode>(StringComparer.Ordinal);
        }

        var groups = groupsByZone[zoneName];
        if (!groups.TryGetValue(groupName, out var group))
        {
            group = new ParameterGroupNode(groupName);
            groups[groupName] = group;
            zone.Groups.Add(group);
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
            if (type == typeof(int)) return ParseInt(raw);
            if (type == typeof(long)) return ParseLong(raw);
            if (type == typeof(double)) return ParseDouble(raw);
            if (type == typeof(float)) return (float)ParseDouble(raw);
            if (type.IsEnum) return Enum.Parse(type, raw);
            return Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
        }
        catch
        {
            return raw;   // Fall back to the raw string; the node handles it.
        }
    }

    // Parameter text is persisted culture-invariantly ("0.2"); also accept a local decimal comma
    // while editing, then store the typed invariant value.
    private static int ParseInt(string raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var invariant)
            ? invariant
            : int.Parse(raw, NumberStyles.Integer, CultureInfo.CurrentCulture);

    private static long ParseLong(string raw) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var invariant)
            ? invariant
            : long.Parse(raw, NumberStyles.Integer, CultureInfo.CurrentCulture);

    private static double ParseDouble(string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant)
            ? invariant
            : double.Parse(raw, NumberStyles.Float, CultureInfo.CurrentCulture);
}

/// <summary>A zone: the top-level section listed on the dialog's left-hand side.</summary>
public sealed class ParameterZoneNode
{
    public string Name { get; }

    /// <summary>Collapsible groups owned by this zone.</summary>
    public ObservableCollection<ParameterGroupNode> Groups { get; } = new();

    public ParameterZoneNode(string name) => Name = name;
}

/// <summary>A collapsible group inside a zone; its children are the parameter rows.</summary>
public partial class ParameterGroupNode : ViewModelBase
{
    public string Name { get; }

    public ObservableCollection<ParameterEditRow> Children { get; } = new();

    /// <summary>Whether the group's rows are shown (toggled by clicking the group header).</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    public ParameterGroupNode(string name) => Name = name;

    /// <summary>Toggle the group open / closed (bound to the group header click).</summary>
    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>A single property row in the dialog (Key / Label / TypeName read-only; Value bound for editing).</summary>
public partial class ParameterEditRow : ViewModelBase
{
    private bool _syncingOptions;

    public string Key { get; }
    public string Label { get; }
    public string TypeName { get; }

    /// <summary>Optional description shown in the bottom description pane (null falls back to <see cref="TypeName"/>).</summary>
    public string? Description { get; }

    /// <summary>Optional contract-layer parameter this row edits (null for declaration-only rows).</summary>
    public NodeParameter? Source { get; }

    /// <summary>Whether the user is allowed to edit this parameter's value.</summary>
    public bool IsEditable { get; }

    /// <summary>True when this row is read-only; a false selection is read-only, not a mode switch.</summary>
    public bool IsReadOnly => !IsEditable;

    /// <summary>Editor hint inherited from the parameter declaration.</summary>
    public ParameterEditorKind Editor { get; }

    /// <summary>True when this row should render the multi-select dropdown.</summary>
    public bool IsMultiSelect { get; }

    /// <summary>True when this row should render the single-select dropdown.</summary>
    public bool IsSingleSelect { get; }

    /// <summary>True for the normal single-value text box.</summary>
    public bool IsTextEditor => IsEditable && !IsMultiSelect && !IsSingleSelect;

    /// <summary>Selectable choices shown by a select-style dropdown.</summary>
    public ObservableCollection<ParameterOption> Options { get; } = new();

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private ParameterOption? _selectedOption;

    public ParameterEditRow(
        string key,
        string label,
        string value,
        string typeName,
        NodeParameter? source = null,
        bool isEditable = true,
        string? description = null,
        ParameterEditorKind editor = ParameterEditorKind.Auto,
        IReadOnlyList<string>? options = null)
    {
        Key = key;
        Label = label;
        TypeName = typeName;
        _value = value;
        Source = source;
        IsEditable = isEditable;
        Description = description;
        Editor = editor;

        if (editor is not (ParameterEditorKind.MultiSelect or ParameterEditorKind.SingleSelect)
            || options is not { Count: > 0 })
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            var normalizedOption = option?.Trim() ?? "";
            if (normalizedOption.Length == 0 || !seen.Add(normalizedOption))
                continue;
            var row = new ParameterOption(normalizedOption);
            row.PropertyChanged += OnOptionPropertyChanged;
            Options.Add(row);
        }

        // A declaration with only empty / duplicate choices is not usable; fall back to a text box
        // instead of opening an empty dropdown.
        if (Options.Count == 0)
            return;

        IsMultiSelect = isEditable && editor == ParameterEditorKind.MultiSelect;
        IsSingleSelect = isEditable && editor == ParameterEditorKind.SingleSelect;
        if (!IsMultiSelect && !IsSingleSelect)
            return;

        _value = IsMultiSelect
            ? MultiSelectValue.Format(MultiSelectValue.Parse(value))
            : MultiSelectValue.Parse(value).FirstOrDefault() ?? "";
        SyncOptionsFromValue();
    }

    partial void OnValueChanged(string value)
    {
        if (_syncingOptions || (!IsMultiSelect && !IsSingleSelect))
            return;
        SyncOptionsFromValue();
    }

    partial void OnSelectedOptionChanged(ParameterOption? value)
    {
        if (_syncingOptions || !IsSingleSelect)
            return;
        _syncingOptions = true;
        try
        {
            Value = value?.Value ?? "";
        }
        finally
        {
            _syncingOptions = false;
        }
    }

    private void SyncOptionsFromValue()
    {
        if (IsMultiSelect)
        {
            var selected = MultiSelectValue.Parse(Value)
                .ToHashSet(StringComparer.Ordinal);
            _syncingOptions = true;
            try
            {
                foreach (var option in Options)
                    option.IsSelected = selected.Contains(option.Value);
            }
            finally
            {
                _syncingOptions = false;
            }
        }
        else if (IsSingleSelect)
        {
            var selected = MultiSelectValue.Parse(Value).FirstOrDefault();
            _syncingOptions = true;
            try
            {
                SelectedOption = Options.FirstOrDefault(o => o.Value == selected);
            }
            finally
            {
                _syncingOptions = false;
            }
        }
    }

    private void OnOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ParameterOption.IsSelected) || !IsMultiSelect || _syncingOptions)
            return;

        _syncingOptions = true;
        try
        {
            Value = MultiSelectValue.Format(
                Options.Where(o => o.IsSelected).Select(o => o.Value));
        }
        finally
        {
            _syncingOptions = false;
        }
    }
}

/// <summary>One selectable value in a select-style parameter row.</summary>
public partial class ParameterOption : ViewModelBase
{
    public string Value { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ParameterOption(string value)
    {
        Value = value;
        Label = value;
    }
}
