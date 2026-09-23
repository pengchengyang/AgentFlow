using System.Collections.ObjectModel;
using AgentFlow.Contracts;
using AgentFlow.Core;
using AgentFlow.Models;
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
    /// <summary>契约层参数声明（非 UI 数据）。</summary>
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
/// Node ViewModel：node body + pin 集合 + 参数编辑器。
/// 非界面数据（类型/名称/分类/pin/参数）来自 <see cref="NodeModel"/>（包装契约层 <see cref="BaseNode"/>）；
/// 纯 UI 相关元素（坐标、选中态、层叠顺序、颜色）保留在本层。
/// </summary>
public partial class NodeViewModel : ViewModelBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>领域模型（包装契约层 BaseNode）。</summary>
    public NodeModel Model { get; }

    public string TypeId => Model.TypeId;

    /// <summary>Core 层编辑态节点（持有 BaseNode 实例和运行时 pin）。GUI 不直接操作它的 pin。</summary>
    public EditorNode? Runtime { get; set; }

    /// <summary>Canvas title: user-editable instance name falls back to the type display name.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? Model.DisplayName : Name;
    public string Category => Model.Category;

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

    public NodeViewModel(NodeModel model)
    {
        Model = model;
        Accent = CategoryColors.Accent(model.Category);
        AccentTint = CategoryColors.AccentTint(model.Category);

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

