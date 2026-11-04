// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BasePin.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Threading;

namespace AgentFlow.Contracts;

/// <summary>Pin direction.</summary>
public enum PinDirection
{
    Input,
    Output
}

/// <summary>
/// A node pin: the single pin type used for declaration, persistable identity and runtime wiring.
/// Plugins create pins with <c>new BasePin(name, type, direction)</c> (or subclass it to override
/// <see cref="OnReceive"/>/<see cref="OnSend"/>) and register them via
/// <see cref="BaseNode.AddInputPin"/>/<see cref="BaseNode.AddOutputPin"/>. At run time the engine
/// wires these same objects, so a node can deliver data either with
/// <see cref="SendSample"/> on its output pin or with <see cref="INodeContext.SetOutput"/>;
/// sending a given value through both paths at once would deliver it twice.
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
    /// A fixed pin identifier generated when the instance is created; it stays constant for the
    /// pin instance, so it can be used to reliably identify the same pin across connect /
    /// serialise / match operations (independent of the persistent <see cref="Id"/>).
    /// </summary>
    public string Uuid { get; }

    /// <summary>
    /// Reference to the connected pin on the opposite side. On an output pin this points to
    /// the downstream input pin; on an input pin this points to the upstream output pin.
    /// Set by the engine when a connection is established, and null on initialise / disconnect.
    /// </summary>
    public BasePin? ConnectedPin { get; internal set; }

    /// <summary>Latest value received on this input pin (null until something arrives).</summary>
    public Sample? Value => Volatile.Read(ref _value);

    /// <summary>True once at least one value has arrived on this input pin.</summary>
    public bool HasValue => Volatile.Read(ref _hasValue) != 0;

    /// <summary>Optional data-arrival callback (usable for event-driven nodes / waiting for upstream).</summary>
    public event Action<Sample?>? ValueReceived;

    /// <summary>
    /// Connected downstream input pins. The contract is one-to-one today, so at most one entry
    /// exists; the list is kept copy-on-write so a send never observes a half-modified wiring
    /// while the UI is connecting / disconnecting (see <see cref="SendSample"/>).
    /// </summary>
    public IReadOnlyList<BasePin> Targets => Volatile.Read(ref _targets);

    /// <summary>Number of connected downstream input pins (0 or 1 with the one-to-one rule).</summary>
    public int TargetCount => Volatile.Read(ref _targets).Length;

    private BasePin[] _targets = Array.Empty<BasePin>();
    private readonly object _wiringGate = new();
    private Sample? _value;
    private int _hasValue;

    /// <summary>The node that owns this pin. Set when the pin is added via AddInputPin / AddOutputPin.</summary>
    public BaseNode? Owner { get; set; }

    /// <summary>
    /// The execution context currently bound to this pin. Used when an input pin hands the
    /// received value to its owning node via <see cref="BaseNode.ReceiveSample"/>.
    /// </summary>
    public INodeContext? Context { get; set; }

    /// <summary>
    /// Persistent pin id, used to write connections (<c>pins[].id</c>) into the editor JSON.
    /// It is assigned lazily per document by the owning graph (see <see cref="PinIdAllocator"/>)
    /// when the node is added and again right before a save; it stays 0 in graphs that never
    /// persist (headless runs), so pin ids are not a per-process resource.
    /// </summary>
    public int Id { get; internal set; }

    public BasePin(string name, DataType dataType, PinDirection direction, bool required = true)
    {
        Name = name;
        DataType = dataType;
        Direction = direction;
        Required = required;
        Uuid = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Assign an id that was persisted in a saved document (restoration path). The caller reports
    /// the id to its document's <see cref="PinIdAllocator"/> so later allocations never collide.
    /// </summary>
    internal void RestoreId(int newId) => Id = newId;

    /// <summary>Assign a fresh id (called by the owning document when it persists its pins).</summary>
    internal void AssignId(int newId) => Id = newId;

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
    /// Runs <see cref="OnSend"/> and forwards the value to every connected target (one-to-one
    /// today). The target array is read once, so concurrent rewiring never causes a
    /// "collection was modified" exception inside the sending node.
    /// </summary>
    public virtual void SendSample(Sample? value)
    {
        if (Direction != PinDirection.Output)
            throw new PinWiringException($"Pin '{Name}' is not an output pin and cannot Send.");

        if (value is not null && value.Type != DataType)
            throw new PinWiringException(
                $"Output pin {Name} is {DataType}, cannot send {value.Type}");

        OnSend(value);

        var targets = Volatile.Read(ref _targets);
        if (targets.Length > 0)
        {
            foreach (var target in targets)
                target.ReceiveSample(value);
        }
    }

    /// <summary>
    /// Receive data on an INPUT pin: records the latest value, raises <see cref="ValueReceived"/>,
    /// runs <see cref="OnReceive"/>, then hands the value to the owning node.
    /// </summary>
    public virtual void ReceiveSample(Sample? value)
    {
        if (Direction != PinDirection.Input)
            return;

        Volatile.Write(ref _value, value);
        Volatile.Write(ref _hasValue, 1);
        ValueReceived?.Invoke(value);
        OnReceive(value);

        if (Owner is null)
            return;

        Owner.ReceiveSample(this, value);
    }

    /// <summary>
    /// Wait until a value arrives on this input pin. Returns the current value immediately when one
    /// has already arrived, null on timeout, and throws when the token is cancelled. A node that
    /// needs upstream data should await this instead of treating "no data yet" as an empty value.
    /// </summary>
    public async Task<Sample?> WaitForValueAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (HasValue)
            return Value;

        var completion = new TaskCompletionSource<Sample?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnValue(Sample? value)
        {
            ValueReceived -= OnValue;
            completion.TrySetResult(value);
        }

        ValueReceived += OnValue;
        CancellationTokenRegistration registration = default;

        try
        {
            // Lost-wakeup guard: the value may have arrived between the check above and the subscription.
            if (HasValue)
            {
                ValueReceived -= OnValue;
                return Value;
            }

            if (timeout is { } limit)
                _ = Task.Delay(limit, CancellationToken.None)
                    .ContinueWith(_ =>
                    {
                        if (completion.TrySetResult(null))
                            ValueReceived -= OnValue;
                    }, TaskScheduler.Default);

            if (cancellationToken.CanBeCanceled)
            {
                // Keep the registration so it can be disposed on every exit path. Otherwise every
                // completed wait leaks its callback on the node's long-lived run token.
                registration = cancellationToken.Register(() =>
                {
                    ValueReceived -= OnValue;
                    completion.TrySetCanceled(cancellationToken);
                });
            }

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
            ValueReceived -= OnValue;
        }
    }

    /// <summary>Wire this output pin to a downstream input pin (one-to-one; type-checked).</summary>
    internal void ConnectTo(BasePin input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Direction != PinDirection.Output)
            throw new PinWiringException($"Pin '{Name}' is not an output pin.");
        if (input.Direction != PinDirection.Input)
            throw new PinWiringException($"Pin '{input.Name}' is not an input pin.");
        if (input.DataType != DataType)
            throw new PinWiringException(
                $"Pin type mismatch: {DataType} -> {input.DataType} ({Name} -> {input.Name})");

        lock (_wiringGate)
        {
            var current = Volatile.Read(ref _targets);
            if (current.Contains(input))
                return;
            if (current.Length > 0)
                throw new PinWiringException(
                    $"Output pin '{Name}' is already connected to '{current[0].Name}'.");
            if (input.ConnectedPin is not null)
                throw new PinWiringException(
                    $"Input pin '{input.Name}' is already connected to '{input.ConnectedPin.Name}'.");

            Volatile.Write(ref _targets, new[] { input });
            ConnectedPin = input;
            input.ConnectedPin = this;
        }
    }

    /// <summary>Remove the wire to <paramref name="input"/> (no-op when it is not connected).</summary>
    internal void DisconnectFrom(BasePin input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_wiringGate)
        {
            var current = Volatile.Read(ref _targets);
            if (!current.Contains(input))
                return;

            Volatile.Write(ref _targets, Array.Empty<BasePin>());
            if (ReferenceEquals(ConnectedPin, input))
                ConnectedPin = null;
            if (ReferenceEquals(input.ConnectedPin, this))
                input.ConnectedPin = null;
        }
    }

    /// <summary>
    /// Clear the received value and value subscribers (but keep the wiring) so a reused instance
    /// starts a new run without the previous run's input values.
    /// </summary>
    internal void ClearValue()
    {
        Volatile.Write(ref _value, null);
        Volatile.Write(ref _hasValue, 0);
        ValueReceived = null;
    }

    /// <summary>
    /// Clear per-run state (wiring, received value, context) so a reused node instance starts
    /// from a clean runtime picture. Called when an instance is released / removed.
    /// </summary>
    internal void ResetRuntimeState()
    {
        lock (_wiringGate)
        {
            foreach (var target in Volatile.Read(ref _targets))
                if (ReferenceEquals(target.ConnectedPin, this))
                    target.ConnectedPin = null;

            Volatile.Write(ref _targets, Array.Empty<BasePin>());

            if (ConnectedPin is { } opposite && ReferenceEquals(opposite.ConnectedPin, this))
                opposite.ConnectedPin = null;
            ConnectedPin = null;
        }

        ClearValue();
        Context = null;
    }
}


