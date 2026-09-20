using AgentFlow.Contracts;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.ViewModels;

/// <summary>Pin ViewModel: an input/output port of a node.</summary>
public partial class PinViewModel : ViewModelBase
{
    public NodeViewModel Node { get; }
    public PinDefinition Definition { get; }

    public string Name => Definition.Name;
    public PinDirection Direction => Definition.Direction;
    public string TypeName => Definition.DataType.Name;

    /// <summary>Anchor point on the canvas (written back by the connector control).</summary>
    [ObservableProperty]
    private Point _anchor;

    public PinViewModel(NodeViewModel node, PinDefinition definition)
    {
        Node = node;
        Definition = definition;
    }
}
