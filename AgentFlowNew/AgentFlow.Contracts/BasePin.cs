// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BasePin.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>Pin direction.</summary>
public enum PinDirection
{
    Input,
    Output
}

/// <summary>
/// Pin definition: the contract for a node's input / output port.
/// <para>
/// Two usage modes:
/// 1) Pure metadata (default): <c>new BasePin("Value", typeof(double), PinDirection.Output)</c>,
///    in which case Send / Receive simply pass through.
/// 2) Custom behaviour: subclass this and override <see cref="OnReceive" /> / <see cref="OnSend" />,
///    or use the <see cref="Input{T}(string, Action{object?}?, bool)" /> /
///    <see cref="Output{T}(string, Action{object?}?, bool)" /> factories to pass lambdas so a
///    concrete node can add validation, sanitisation, serialisation, logging and other
///    business logic at the pin level.
/// </para>
/// Data flow between nodes: an upstream OUTPUT pin calls Send(value) → for every connected
/// INPUT pin Receive(value) is called, passing through OnSend / OnReceive hooks in turn.
/// </summary>
public class BasePin
{
    /// <summary>Pin name (unique within the node).</summary>
    public string Name { get; }

    /// <summary>Data type (used for connection validation).</summary>
    public Type DataType { get; }

    /// <summary>Direction: input or output.</summary>
    public PinDirection Direction { get; }

    /// <summary>Whether the pin is required (validation error if unconnected).</summary>
    public bool Required { get; }

    /// <summary>
    /// A fixed pin identifier generated when the instance is created; it stays constant for
    /// every pin instance (including runtime pins derived from this class), so it can be used
    /// to reliably identify the same pin across connect / serialise / match operations.
    /// </summary>
    public string Uuid { get; }

    /// <summary>
    /// Reference to a connected downstream input pin (only meaningful on output pins;
    /// input pins are always null). Set by the engine when a connection is established,
    /// and null on initialise / disconnect.
    /// </summary>
    public BasePin? ConnectedInput { get; set; }

    public BasePin(string name, Type dataType, PinDirection direction, bool required = true)
    {
        Name = name;
        DataType = dataType;
        Direction = direction;
        Required = required;
        Uuid = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Called on an INPUT pin when data pushed from an upstream OUTPUT pin arrives.
    /// Default no-op; subclasses may override for validation, type conversion, logging,
    /// event-driven wake-up, etc.
    /// </summary>
    /// <param name="value">The value pushed from upstream (may be null at runtime).</param>
    public virtual void OnReceive(object? value) { }

    /// <summary>
    /// Called on an OUTPUT pin before data is sent to downstream INPUT pins.
    /// Default no-op; subclasses may override for serialisation, masking, sampling, logging, etc.
    /// </summary>
    /// <param name="value">The value about to be sent downstream.</param>
    public virtual void OnSend(object? value) { }

    // ---- Convenience factories: inject pin behaviour via lambdas without subclassing ----

    /// <summary>Declare an input pin, optionally passing a callback invoked when data arrives.</summary>
    public static BasePin Input<T>(string name, Action<object?>? onReceive = null, bool required = true)
        => new DelegatePin(name, typeof(T), PinDirection.Input, required, onReceive, null);

    /// <summary>Declare an output pin, optionally passing a callback invoked before data is sent.</summary>
    public static BasePin Output<T>(string name, Action<object?>? onSend = null, bool required = true)
        => new DelegatePin(name, typeof(T), PinDirection.Output, required, null, onSend);

    /// <summary>Internal implementation: wraps lambdas into a BasePin.</summary>
    private sealed class DelegatePin : BasePin
    {
        private readonly Action<object?>? _onReceive;
        private readonly Action<object?>? _onSend;

        public DelegatePin(string name, Type dataType, PinDirection direction, bool required,
                          Action<object?>? onReceive, Action<object?>? onSend)
            : base(name, dataType, direction, required)
        {
            _onReceive = onReceive;
            _onSend = onSend;
        }

        public override void OnReceive(object? value) => _onReceive?.Invoke(value);
        public override void OnSend(object? value) => _onSend?.Invoke(value);
    }
}
