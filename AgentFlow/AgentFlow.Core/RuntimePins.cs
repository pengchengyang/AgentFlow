// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="RuntimePins.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Common base for runtime pins. Inherits <see cref="BasePin"/> so an input or
/// output runtime pin can be used anywhere a <see cref="BasePin"/> is expected,
/// and keeps the original definition's <see cref="BasePin.OnReceive"/> /
/// <see cref="BasePin.OnSend"/> hooks alive.
/// </summary>
public abstract class RuntimePin : BasePin
{
    private readonly BasePin _definition;

    protected RuntimePin(BasePin definition, BaseNode? owner)
        : base(definition.Name, definition.DataType, definition.Direction, definition.Required, definition.Id)
    {
        _definition = definition;
        Owner = owner;
        Context = definition.Context;
    }

    /// <inheritdoc/>
    public override void OnReceive(Sample? value) => _definition.OnReceive(value);

    /// <inheritdoc/>
    public override void OnSend(Sample? value) => _definition.OnSend(value);
}

/// <summary>
/// Runtime input pin: receives data from upstream output pins.
/// Inherits <see cref="BasePin"/>, so the receiving node can inspect the pin
/// (name, data type, hooks) directly in <see cref="BaseNode.Receive"/>.
/// </summary>
public sealed class RuntimeInputPin : RuntimePin
{
    /// <summary>Latest value received via <see cref="Receive"/>.</summary>
    public Sample? Value { get; private set; }

    /// <summary>Optional data-arrival callback (usable for event-driven nodes).</summary>
    public event Action<Sample?>? ValueReceived;

    public RuntimeInputPin(BasePin definition, BaseNode? owner = null, INodeContext? context = null)
        : base(definition, owner)
    {
        Context = context;
    }

    /// <summary>
    /// Receive data (called by the upstream output pin's Send).
    /// Stores the latest value, notifies listeners, then lets <see cref="BasePin.Receive"/>
    /// run <see cref="OnReceive"/> and hand the value to the owning node.
    /// </summary>
    public override void ReceiveSample(Sample? value)
    {
        Value = value;
        ValueReceived?.Invoke(value);
        base.ReceiveSample(value);
    }
}

/// <summary>
/// Runtime output pin: holds references to all connected input pins.
/// Inherits <see cref="BasePin"/>, so it can be used anywhere a pin definition
/// is expected and shares the common base with input pins.
/// </summary>
public sealed class RuntimeOutputPin : RuntimePin
{
    private readonly List<RuntimeInputPin> _targets = new();

    /// <summary>Connected downstream input pins.</summary>
    public IReadOnlyList<RuntimeInputPin> Targets => _targets;

    public RuntimeOutputPin(BasePin definition, BaseNode? owner = null)
        : base(definition, owner)
    {
    }

    /// <summary>Establish a connection (called by the engine / editor graph when wiring).</summary>
    public void Connect(RuntimeInputPin input)
    {
        if (input.DataType != DataType)
            throw new InvalidOperationException(
                $"Pin type mismatch: {DataType} -> {input.DataType} ({Name} -> {input.Name})");
        // One-to-one rule: an output pin connects to exactly one input pin.
        // If it is already connected, simply don't connect (no exception).
        if (_targets.Contains(input) || _targets.Count > 0)
            return;

        _targets.Add(input);
        ConnectedPin = input;
        input.ConnectedPin = this;
    }

    /// <summary>Remove a previously connected downstream input pin.</summary>
    public void Disconnect(RuntimeInputPin input)
    {
        _targets.Remove(input);
        if (ReferenceEquals(ConnectedPin, input))
            ConnectedPin = null;
        if (ReferenceEquals(input.ConnectedPin, this))
            input.ConnectedPin = null;
    }

    /// <summary>
    /// Send data: runs the pin-level OnSend hook, then calls Receive on the single connected
    /// input pin. Connections are one-to-one, so at most one target exists.
    /// </summary>
    public override void SendSample(Sample? value)
    {
        if (value is not null && value.Type != DataType)
            throw new InvalidOperationException(
                $"Output pin {Name} is {DataType}, cannot send {value.Type}");

        OnSend(value);

        foreach (var target in _targets)
            target.ReceiveSample(value);
    }
}


