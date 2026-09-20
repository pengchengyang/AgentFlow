namespace AgentFlow.Contracts;

/// <summary>
/// Runtime input pin: receives data from upstream output pins.
/// Connection rule: an output pin holds references to its connected input pins.
/// </summary>
public interface IInputPin
{
    string Name { get; }
    Type DataType { get; }

    /// <summary>Receive data (called by the upstream output pin's Send).</summary>
    void Receive(object? value);
}

/// <summary>
/// Runtime output pin: holds references to all connected input pins.
/// Send calls Receive on each of them, enabling direct node-to-node communication.
/// </summary>
public interface IOutputPin
{
    string Name { get; }
    Type DataType { get; }

    /// <summary>Connected downstream input pins.</summary>
    IReadOnlyList<IInputPin> Targets { get; }

    /// <summary>Establish a connection (called by the engine when wiring the graph).</summary>
    void Connect(IInputPin input);

    /// <summary>Send data: calls Receive on every connected input pin.</summary>
    void Send(object? value);
}
