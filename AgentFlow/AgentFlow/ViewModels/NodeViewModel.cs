using System.Collections.ObjectModel;
using AgentFlow.Core;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.ViewModels;
public static class CategoryColors
{
    private static readonly Dictionary<string, Color> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Input"] = Color.Parse("#3B82F6"),   // Blue
        ["Logic"] = Color.Parse("#8B5CF6"),   // Purple
        ["Output"] = Color.Parse("#10B981"),  // Green
        ["Flow"] = Color.Parse("#0D9488"),    // Teal
        ["General"] = Color.Parse("#64748B"), // Gray
    };

    public static SolidColorBrush Accent(string category) =>
        new(Map.TryGetValue(category, out var c) ? c : Map["General"]);

    /// <summary>Light header tint (accent at 14% opacity).</summary>
    public static SolidColorBrush AccentTint(string category) =>
        new(Map.TryGetValue(category, out var c) ? c : Map["General"], 0.14);
}

/// <summary>Node parameter (an auto-generated editor row in the property panel).</summary>
public partial class ParameterViewModel : ViewModelBase
{
    public ParameterDescriptor Definition { get; }

    public string Key => Definition.Name;
    public string Label => Definition.DisplayName;
    public string? Hint => Definition.Description;
    public string TypeName => Definition.DataType.Name;

    [ObservableProperty]
    private string _value = "";

    public ParameterViewModel(ParameterDescriptor definition)
    {
        Definition = definition;
        Value = definition.DefaultValue?.ToString() ?? "";
    }

    /// <summary>Convert the text to the declared type.</summary>
    public object? ToValue()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return Definition.DefaultValue;

        var t = Definition.DataType;
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

/// <summary>Node ViewModel: node body + pin collections + parameter editors.</summary>
public partial class NodeViewModel : ViewModelBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public NodeDescriptor Descriptor { get; }
    public string TypeId => Descriptor.TypeId;

    /// <summary>Core 层编辑态节点（持有 INode 实例和运行时 pin）。GUI 不直接操作它的 pin。</summary>
    public EditorNode? Runtime { get; set; }

    /// <summary>Canvas title: user-editable instance name falls back to the type display name.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? Descriptor.DisplayName : Name;
    public string Category => Descriptor.Category;

    /// <summary>Category accent color (header icon dot / header tint).</summary>
    public SolidColorBrush Accent { get; }
    public SolidColorBrush AccentTint { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private int _priority;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Title));

    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Visual stacking order (higher = rendered on top, hit-tested first).</summary>
    [ObservableProperty]
    private int _zIndex;

    /// <summary>Section visibility flags (empty sections collapse in the card template).</summary>
    [ObservableProperty]
    private bool _hasInputs;

    [ObservableProperty]
    private bool _hasOutputs;

    [ObservableProperty]
    private bool _hasParameters;

    public ObservableCollection<PinViewModel> Inputs { get; } = new();
    public ObservableCollection<PinViewModel> Outputs { get; } = new();
    public ObservableCollection<ParameterViewModel> Parameters { get; } = new();

    public NodeViewModel(NodeDescriptor descriptor)
    {
        Descriptor = descriptor;
        Accent = CategoryColors.Accent(descriptor.Category);
        AccentTint = CategoryColors.AccentTint(descriptor.Category);

        foreach (var pin in descriptor.InputPins)
            Inputs.Add(new PinViewModel(this, pin));
        foreach (var pin in descriptor.OutputPins)
            Outputs.Add(new PinViewModel(this, pin));
        // Auto-generate property panel editors from parameter declarations (with defaults).
        foreach (var param in descriptor.Parameters)
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
                    new ParameterDescriptor(kv.Key, typeof(string), kv.Key));
                legacy.Value = kv.Value?.ToString() ?? "";
                Parameters.Add(legacy);
            }
        }

        RefreshSectionFlags();
    }
}

