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
/// 1) Pure metadata (default): <c>new BasePin("In", DataType.STRING, PinDirection.Input)</c>,
///    in which case Send / Receive simply pass through.
/// 2) Custom behaviour: subclass this and override <see cref="OnReceive" /> / <see cref="OnSend" />.
/// </para>
/// Data flow between nodes: an upstream OUTPUT pin calls <see cref="SendSample"/> which forwards
/// to the connected INPUT pin's <see cref="ReceiveSample"/>. The INPUT pin then runs
/// <see cref="OnReceive"/> and hands the value to its owning node.
/// </summary>
public class BasePin
{
    /// <summary>Pin name (unique within the node).</summary>
    public string Name { get; }

    /// <summary>Data type (used for connection validation).</summary>
    public DataType DataType { get; }

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
    /// Reference to the connected pin on the opposite side. On an output pin this points to
    /// the downstream input pin; on an input pin this points to the upstream output pin.
    /// Set by the engine when a connection is established, and null on initialise / disconnect.
    /// </summary>
    public BasePin? ConnectedPin { get; set; }

    /// <summary>The node that owns this pin. Runtime pins set this when they are created.</summary>
    public BaseNode? Owner { get; set; }

    /// <summary>
    /// The execution context currently bound to this pin. Used when an input pin hands the
    /// received value to its owning node via <see cref="BaseNode.ReceiveSample"/>.
    /// </summary>
    public INodeContext? Context { get; set; }

    /// <summary>
    /// Unique pin instance id in the range 6000..10000. Assigned at construction and returned
    /// to the id pool when the pin is released (smallest available id is always reused).
    /// Used to persist connection info (output pin id + input pin id) to JSON.
    /// </summary>
    public int Id { get; internal set; }

    public BasePin(string name, DataType dataType, PinDirection direction, bool required = true)
        : this(name, dataType, direction, required, id: null)
    {
    }

    internal BasePin(string name, DataType dataType, PinDirection direction, bool required, int? id)
    {
        Name = name;
        DataType = dataType;
        Direction = direction;
        Required = required;
        Uuid = Guid.NewGuid().ToString("N");
        Id = id ?? PinIdPool.Allocate();
    }

    /// <summary>
    /// Replace this pin's id with a saved id (restoration path). The old id is returned to the
    /// pool and the saved id is reserved so future allocations never collide with persisted ids.
    /// </summary>
    internal void RestoreId(int newId)
    {
        if (Id == newId) return;
        PinIdPool.Release(Id);
        PinIdPool.Reserve(newId);
        Id = newId;
    }

    /// <summary>Return this pin's id to the id pool so it can be reused (e.g. when its node is removed).</summary>
    public void ReleaseId() => PinIdPool.Release(Id);

    /// <summary>
    /// Called on an INPUT pin when data pushed from an upstream OUTPUT pin arrives.
    /// Default no-op; subclasses may override for validation, type conversion, logging,
    /// event-driven wake-up, etc.
    /// </summary>
    /// <param name="value">The value pushed from upstream (may be null at runtime).</param>
    public virtual void OnReceive(Sample? value) { }

    /// <summary>
    /// Called on an OUTPUT pin before data is sent to downstream INPUT pins.
    /// Default no-op; subclasses may override for serialisation, masking, sampling, logging, etc.
    /// </summary>
    /// <param name="value">The value about to be sent downstream.</param>
    public virtual void OnSend(Sample? value) { }

    /// <summary>
    /// Send data from an OUTPUT pin to its connected INPUT pin.
    /// Default implementation runs <see cref="OnSend"/> and forwards the value to
    /// <see cref="ConnectedPin"/>.<see cref="ReceiveSample(Sample?)"/>.
    /// </summary>
    public virtual void SendSample(Sample? value)
    {
        if (Direction != PinDirection.Output)
            throw new InvalidOperationException($"Pin '{Name}' is not an output pin and cannot Send.");

        if (value is not null && value.Type != DataType)
            throw new InvalidOperationException(
                $"Output pin {Name} is {DataType}, cannot send {value.Type}");

        OnSend(value);
        ConnectedPin?.ReceiveSample(value);
    }

    /// <summary>
    /// Receive data on an INPUT pin.
    /// Default implementation runs <see cref="OnReceive"/> and, when this is an input pin,
    /// hands the value to its owning node via <see cref="BaseNode.ReceiveSample"/>.
    /// </summary>
    public virtual void ReceiveSample(Sample? value)
    {
        if (Direction != PinDirection.Input)
            return;

        OnReceive(value);

        if (Owner is null)
            return;

        Owner.ReceiveSample(this, value);
    }
}


