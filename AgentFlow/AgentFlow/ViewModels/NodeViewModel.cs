// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using AgentFlow.Contracts;
using AgentFlow.Core;
using AgentFlow.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.ViewModels;
public static class CategoryColors
{
    private static readonly Dictionary<string, string> ResourceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Input"] = "BrushCatInput",
        ["Logic"] = "BrushCatLogic",
        ["Output"] = "BrushCatOutput",
        ["Flow"] = "BrushCatFlow",
        ["General"] = "BrushCatGeneral",
        ["Amplifier"] = "BrushCatAmplifier",
        ["AI"] = "BrushCatLogic",        // AI nodes such as ReActAgent are grouped into the purple logic category
        ["Network"] = "BrushCatFlow",   // Network nodes such as Socket are grouped into the teal flow category
    };

    private static readonly Dictionary<string, Color> Fallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Input"] = Color.Parse("#0EA5E9"),
        ["Logic"] = Color.Parse("#8B5CF6"),
        ["Output"] = Color.Parse("#F43F5E"),
        ["Flow"] = Color.Parse("#0D9488"),
        ["General"] = Color.Parse("#64748B"),
        ["Amplifier"] = Color.Parse("#F59E0B"),
        ["AI"] = Color.Parse("#8B5CF6"),
        ["Network"] = Color.Parse("#0D9488"),
    };

    /// <summary>Resolve a color by resource key, falling back to the hardcoded default if the resource is absent.</summary>
    private static Color Resolve(string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey, out var value) == true &&
            value is Color c)
            return c;
        return fallback;
    }

    private static Color ColorFor(string category)
    {
        var key = ResourceKeys.TryGetValue(category, out var k) ? k : "BrushCatGeneral";
        var fb = Fallback.TryGetValue(category, out var c) ? c : Fallback["General"];
        return Resolve(key, fb);
    }

    public static SolidColorBrush Accent(string category) => new(ColorFor(category));

    /// <summary>Light header tint (accent at 18% opacity).</summary>
    public static SolidColorBrush AccentTint(string category) => new(ColorFor(category), 0.18);

    /// <summary>Component-list icon color, one per initial letter (A..Z).</summary>
    private static readonly Dictionary<char, string> LetterColors = new()
    {
        ['A'] = "#0EA5E9", ['B'] = "#8B5CF6", ['C'] = "#F43F5E", ['D'] = "#0D9488",
        ['E'] = "#F59E0B", ['F'] = "#6366F1", ['G'] = "#10B981", ['H'] = "#EC4899",
        ['I'] = "#14B8A6", ['J'] = "#F97316", ['K'] = "#3B82F6", ['L'] = "#EF4444",
        ['M'] = "#22C55E", ['N'] = "#A855F7", ['O'] = "#06B6D4", ['P'] = "#84CC16",
        ['Q'] = "#E11D48", ['R'] = "#2563EB", ['S'] = "#7C3AED", ['T'] = "#DC2626",
        ['U'] = "#059669", ['V'] = "#D946EF", ['W'] = "#0891B2", ['X'] = "#CA8A04",
        ['Y'] = "#4F46E5", ['Z'] = "#EA580C",
    };

    private static Color ColorForLetter(char letter)
    {
        var upper = char.ToUpperInvariant(letter);
        return LetterColors.TryGetValue(upper, out var hex) ? Color.Parse(hex) : Color.Parse("#64748B");
    }

    /// <summary>Take the first letter of the name, matching the UI icon.</summary>
    private static char InitialLetter(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return '\0';
        foreach (var ch in name)
            if (char.IsLetter(ch))
                return char.ToUpperInvariant(ch);
        return '\0';
    }

    /// <summary>Icon accent for a node name, colored by its initial letter.</summary>
    public static SolidColorBrush AccentByInitial(string name) => new(ColorForLetter(InitialLetter(name)));

    /// <summary>Header tint matching the initial-letter icon color (18% opacity), so the canvas card matches the sidebar icon.</summary>
    public static SolidColorBrush AccentTintByInitial(string name) => new(ColorForLetter(InitialLetter(name)), 0.18);
}
public partial class ParameterViewModel : ViewModelBase
{
    /// <summary>The contract-layer parameter declaration (non-UI data).</summary>
    public ParameterDefinition basePin { get; }

    public string Key => basePin.Name;
    public string Label => basePin.DisplayName;
    public string? Hint => basePin.Description;
    public string TypeName => basePin.DataType.Name;

    [ObservableProperty]
    private string _value = "";

    public ParameterViewModel(ParameterDefinition definition)
    {
        basePin = definition;
        Value = basePin.DefaultValue?.ToString() ?? "";
    }

    /// <summary>Convert the text to the declared type.</summary>
    public object? ToValue()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return basePin.DefaultValue;

        var t = basePin.DataType;
        try
        {
            if (t == typeof(string)) return Value;
            if (t == typeof(bool)) return bool.Parse(Value);
            if (t == typeof(int)) return int.Parse(Value);
            if (t == typeof(long)) return long.Parse(Value);
            if (t == typeof(double)) return double.Parse(Value);
            if (t == typeof(float)) return float.Parse(Value);
            if (t.IsEnum) return Enum.Parse(t, Value);
            return Convert.ChangeType(Value, t);
        }
        catch
        {
            return Value;   // Fall back to string; the node handles it.
        }
    }
}

/// <summary>
/// Node ViewModel: node body + pin collection + parameter editor.
/// Non-visual data (type / name / category / pins / parameters) comes from
/// <see cref="NodeModel"/> (which wraps the contract-layer <see cref="BaseNode"/>);
/// pure UI concerns (position, selection, z-order, color) stay in this layer.
/// </summary>
public partial class NodeViewModel : ViewModelBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The domain model (wraps the contract-layer BaseNode).</summary>
    public NodeModel Model { get; }

    public string Name => Model.Name;
    public string Uuid => Model.Uuid;
    public int InstanceId => Model.InstanceId;

    public int DependsOn => Model.Node.DependsOn;

    /// <summary>Notify listeners that the DependsOn parameter changed (raised after the dialog applies it).</summary>
    public void NotifyDependsOnChanged() => OnPropertyChanged(nameof(DependsOn));

    /// <summary>The Core-layer editor node (holds the BaseNode instance and runtime pins). The GUI does not operate its pins directly.</summary>
    public EditorNode? Runtime { get; set; }

    /// <summary>Canvas title: user-editable display name falls back to the type id (Name).</summary>
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Model.Name : DisplayName;
    public string Category => Model.Category;

    /// <summary>Category accent color (header icon dot / header tint).</summary>
    public SolidColorBrush Accent { get; }
    public SolidColorBrush AccentTint { get; }

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private int _priority;

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Title));

    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Visual stacking order (higher = rendered on top, hit-tested first).</summary>
    [ObservableProperty]
    private int _zIndex;

    /// <summary>Section visibility flags (empty sections collapse in the card template).</summary>
    /// <summary>Whether the underlying runtime node is currently executing (drives the header spin).</summary>
    [ObservableProperty]
    private bool _running;

    /// <summary>
    /// Whether the underlying runtime node has been started successfully (initialized + Run returned
    /// without errors) and has not been stopped yet. Stays true after a one-shot node finished its
    /// work — the workflow is still running until Stop.
    /// </summary>
    [ObservableProperty]
    private bool _started;

    /// <summary>Whether the underlying runtime node is still starting up (connecting / waiting for a resource).</summary>
    [ObservableProperty]
    private bool _connecting;

    /// <summary>Whether the underlying runtime node failed to start.</summary>
    [ObservableProperty]
    private bool _failed;

    [ObservableProperty]
    private bool _hasInputs;

    [ObservableProperty]
    private bool _hasOutputs;

    [ObservableProperty]
    private bool _hasParameters;

    public ObservableCollection<PinViewModel> Inputs { get; } = new();
    public ObservableCollection<PinViewModel> Outputs { get; } = new();
    public ObservableCollection<ParameterViewModel> Parameters { get; } = new();

    public NodeViewModel(NodeModel model)
    {
        Model = model;
        DisplayName = model.Node.DisplayName ?? "";
        // Match the sidebar icon color: accent/tint/pins all follow the node-name initial letter, not the category.
        Accent = CategoryColors.AccentByInitial(Title);
        AccentTint = CategoryColors.AccentTintByInitial(Title);

        foreach (var pin in model.Inputs)
            Inputs.Add(new PinViewModel(this, pin));
        foreach (var pin in model.Outputs)
            Outputs.Add(new PinViewModel(this, pin));
        // Auto-generate property panel editors from parameter declarations (with defaults).
        foreach (var param in model.Parameters)
            Parameters.Add(new ParameterViewModel(param));

        RefreshSectionFlags();
    }

    private void RefreshSectionFlags()
    {
        HasInputs = Inputs.Count > 0;
        HasOutputs = Outputs.Count > 0;
        HasParameters = Parameters.Count > 0;
    }

    /// <summary>Override parameter values from a saved JSON document.</summary>
    /// <summary>Refresh the runtime state mirrors (executing / started / connecting / failed) from the underlying BaseNode.</summary>
    public void NotifyRunningChanged()
    {
        var node = Model.Node;
        Running = node.Running;                       // executing right now (spinner)
        Started = node.State == NodeState.Running;    // started successfully, not stopped yet
        Connecting = node.Connecting;
        Failed = node.Failed;
    }

    public void ApplyParameterValues(IReadOnlyDictionary<string, object?> values)
    {
        foreach (var kv in values)
        {
            var p = Parameters.FirstOrDefault(p => p.Key == kv.Key);
            if (p is not null)
                p.Value = kv.Value?.ToString() ?? "";
            else
            {
                // Backward compatibility: undeclared parameters are kept as strings.
                var legacy = new ParameterViewModel(
                    new ParameterDefinition(kv.Key, typeof(string), kv.Key));
                legacy.Value = kv.Value?.ToString() ?? "";
                Parameters.Add(legacy);
            }
        }

        RefreshSectionFlags();
    }
}

