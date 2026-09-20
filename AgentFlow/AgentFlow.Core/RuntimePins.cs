using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Runtime input pin: Receive stores the value so the node can read it during Execute.
/// </summary>
public sealed class RuntimeInputPin : IInputPin
{
    public string Name { get; }
    public Type DataType { get; }

    /// <summary>Latest value received via Receive.</summary>
    public object? Value { get; private set; }

    /// <summary>Optional data-arrival callback (usable for event-driven nodes).</summary>
    public event Action<object?>? ValueReceived;

    public RuntimeInputPin(PinDefinition definition)
    {
        Name = definition.Name;
        DataType = definition.DataType;
    }

    public void Receive(object? value)
    {
        Value = value;
        ValueReceived?.Invoke(value);
    }
}

/// <summary>
/// Runtime output pin: holds references to downstream input pins.
/// Send calls Receive on each of them, completing direct node-to-node communication.
/// </summary>
public sealed class RuntimeOutputPin : IOutputPin
{
    private readonly List<IInputPin> _targets = new();

    public string Name { get; }
    public Type DataType { get; }
    public IReadOnlyList<IInputPin> Targets => _targets;

    public RuntimeOutputPin(PinDefinition definition)
    {
        Name = definition.Name;
        DataType = definition.DataType;
    }

    public void Connect(IInputPin input)
    {
        if (!input.DataType.IsAssignableFrom(DataType))
            throw new InvalidOperationException(
                $"Pin type mismatch: {DataType.Name} -> {input.DataType.Name} ({Name} -> {input.Name})");
        _targets.Add(input);
    }

    public void Send(object? value)
    {
        if (value is not null && !DataType.IsInstanceOfType(value))
            throw new InvalidOperationException(
                $"Output pin {Name} is {DataType.Name}, cannot send {value.GetType().Name}");

        foreach (var target in _targets)
            target.Receive(value);
    }
}
