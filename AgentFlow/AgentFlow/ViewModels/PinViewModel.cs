using AgentFlow.Contracts;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.ViewModels;

/// <summary>
/// Pin ViewModel: an input/output port of a node.
/// Mirrors the runtime pin contract (<see cref="IInputPin"/> / <see cref="IOutputPin"/>):
/// an OUTPUT pin keeps references to every connected INPUT pin, so data can be pushed
/// "over the wire" via <see cref="Send"/>. The editor keeps this in sync as wires are
/// created/removed/loaded; the workflow engine keeps the equivalent references on its
/// own runtime pins during a run.
/// </summary>
public partial class PinViewModel : ViewModelBase
{
    // OUTPUT pin only: the downstream input pins this pin is wired to.
    private readonly List<PinViewModel> _targets = new();

    public NodeViewModel Node { get; }
    public PinDefinition Definition { get; }

    public string Name => Definition.Name;
    public PinDirection Direction => Definition.Direction;
    public string TypeName => Definition.DataType.Name;
    public bool Required => Definition.Required;
    public bool IsInput => Direction == PinDirection.Input;
    public bool IsOutput => Direction == PinDirection.Output;

    /// <summary>Anchor point on the canvas (written back by the Nodify connector).</summary>
    [ObservableProperty]
    private Point _anchor;

    /// <summary>True when the pin takes part in at least one connection (drives the dot look).</summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>INPUT pin only: the single upstream OUTPUT pin feeding it (one input = one wire).</summary>
    public PinViewModel? Source { get; private set; }

    /// <summary>INPUT pin only: the latest value that arrived over the connected wire.</summary>
    [ObservableProperty]
    private object? _receivedValue;

    /// <summary>OUTPUT pin only: downstream input pins reachable through this pin (the wire targets).</summary>
    public IReadOnlyList<PinViewModel> Targets => _targets;

    /// <summary>Raised on an INPUT pin whenever a value arrives over a wire.</summary>
    public event Action<object?>? ValueReceived;

    public PinViewModel(NodeViewModel node, PinDefinition definition)
    {
        Node = node;
        Definition = definition;
    }

    public string? ReceivedValueText => ReceivedValue?.ToString();

    partial void OnReceivedValueChanged(object? value) => OnPropertyChanged(nameof(ReceivedValueText));

    /// <summary>
    /// OUTPUT pin: remember the connected INPUT pin (the editor-side twin of
    /// <see cref="IOutputPin.Connect"/>).
    /// </summary>
    public void ConnectTo(PinViewModel input)
    {
        if (Direction != PinDirection.Output || input.Direction != PinDirection.Input)
            throw new InvalidOperationException("A connection must start on an output pin and end on an input pin.");
        if (ReferenceEquals(Node, input.Node))
            throw new InvalidOperationException("A pin cannot be connected to another pin on the same node.");

        if (_targets.Contains(input))
            return;

        _targets.Add(input);
        input.Source = this;
        IsConnected = true;
        input.IsConnected = true;
    }

    /// <summary>OUTPUT pin: drop the reference to one downstream INPUT pin (wire deleted).</summary>
    public void DisconnectTarget(PinViewModel input)
    {
        if (!_targets.Remove(input))
            return;

        if (ReferenceEquals(input.Source, this))
            input.Source = null;
        input.ReceivedValue = null;

        if (_targets.Count == 0)
            IsConnected = false;
        input.IsConnected = input.Source is not null;
    }

    /// <summary>
    /// OUTPUT pin: push a value over every connected wire. Each target INPUT pin has its
    /// <see cref="Receive"/> called, exactly like <see cref="IOutputPin.Send"/>.
    /// </summary>
    public void Send(object? value)
    {
        foreach (var target in _targets)
            target.Receive(value);
    }

    /// <summary>INPUT pin: accept a value pushed by the upstream OUTPUT pin.</summary>
    public void Receive(object? value)
    {
        ReceivedValue = value;
        ValueReceived?.Invoke(value);
    }

    /// <summary>Remove every wire touching this pin (used when its node is deleted).</summary>
    public void DetachAll()
    {
        if (IsOutput)
        {
            foreach (var target in _targets.ToList())
            {
                if (ReferenceEquals(target.Source, this))
                    target.Source = null;
                target.ReceivedValue = null;
                target.IsConnected = false;
            }
            _targets.Clear();
            IsConnected = false;
        }
        else if (IsInput && Source is not null)
        {
            Source._targets.Remove(this);
            if (Source._targets.Count == 0)
                Source.IsConnected = false;
            Source = null;
            ReceivedValue = null;
            IsConnected = false;
        }
    }
}
