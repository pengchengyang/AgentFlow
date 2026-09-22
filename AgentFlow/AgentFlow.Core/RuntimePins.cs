using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Common base for runtime pins. Inherits <see cref="PinDefinition"/> so an input or
/// output runtime pin can be used anywhere a <see cref="PinDefinition"/> is expected,
/// and keeps the original definition's <see cref="PinDefinition.OnReceive"/> /
/// <see cref="PinDefinition.OnSend"/> hooks alive.
/// </summary>
public abstract class RuntimePin : PinDefinition
{
    private readonly PinDefinition _definition;

    /// <summary>The node that owns this runtime pin.</summary>
    public INode? Owner { get; }

    protected RuntimePin(PinDefinition definition, INode? owner)
        : base(definition.Name, definition.DataType, definition.Direction, definition.Required)
    {
        _definition = definition;
        Owner = owner;
    }

    /// <inheritdoc/>
    public override void OnReceive(object? value) => _definition.OnReceive(value);

    /// <inheritdoc/>
    public override void OnSend(object? value) => _definition.OnSend(value);
}

/// <summary>
/// Runtime input pin: receives data from upstream output pins.
/// Inherits <see cref="PinDefinition"/>, so the receiving node can inspect the pin
/// (name, data type, hooks) directly in <see cref="INode.Receive"/>.
/// </summary>
public sealed class RuntimeInputPin : RuntimePin
{
    private readonly INodeContext? _context;

    /// <summary>Latest value received via <see cref="Receive"/>.</summary>
    public object? Value { get; private set; }

    /// <summary>Optional data-arrival callback (usable for event-driven nodes).</summary>
    public event Action<object?>? ValueReceived;

    public RuntimeInputPin(PinDefinition definition, INode? owner = null, INodeContext? context = null)
        : base(definition, owner)
    {
        _context = context;
    }

    /// <summary>
    /// Receive data (called by the upstream output pin's Send).
    /// Runs the pin-level hook, stores the value, then hands the data to the owning node
    /// via <see cref="INode.Receive"/> for immediate reaction.
    /// </summary>
    public void Receive(object? value)
    {
        // 1. 先跑 pin 级自定义钩子（校验/清洗/日志/事件唤醒）
        OnReceive(value);
        // 2. 存值供节点读取
        Value = value;
        // 3. 通知引擎层
        ValueReceived?.Invoke(value);
        // 4. 把数据连同输入 pin 定义交给拥有该 pin 的节点，实现节点间直接通信
        if (Owner is not null && _context is not null)
            Owner.Receive(_context, this, value);
    }
}

/// <summary>
/// Runtime output pin: holds references to all connected input pins.
/// Inherits <see cref="PinDefinition"/>, so it can be used anywhere a pin definition
/// is expected and shares the common base with input pins.
/// </summary>
public sealed class RuntimeOutputPin : RuntimePin
{
    private readonly List<RuntimeInputPin> _targets = new();

    /// <summary>Connected downstream input pins.</summary>
    public IReadOnlyList<RuntimeInputPin> Targets => _targets;

    public RuntimeOutputPin(PinDefinition definition, INode? owner = null)
        : base(definition, owner)
    {
    }

    /// <summary>Establish a connection (called by the engine / editor graph when wiring).</summary>
    public void Connect(RuntimeInputPin input)
    {
        if (!input.DataType.IsAssignableFrom(DataType))
            throw new InvalidOperationException(
                $"Pin type mismatch: {DataType.Name} -> {input.DataType.Name} ({Name} -> {input.Name})");
        if (_targets.Contains(input))
            throw new InvalidOperationException(
                $"Output pin {Name} is already connected to input pin {input.Name}.");

        _targets.Add(input);
    }

    /// <summary>Remove a previously connected downstream input pin.</summary>
    public void Disconnect(RuntimeInputPin input) => _targets.Remove(input);

    /// <summary>
    /// Send data: runs the pin-level OnSend hook, then calls Receive on every connected input pin.
    /// </summary>
    public void Send(object? value)
    {
        if (value is not null && !DataType.IsInstanceOfType(value))
            throw new InvalidOperationException(
                $"Output pin {Name} is {DataType.Name}, cannot send {value.GetType().Name}");

        // 1. 先跑 pin 级自定义钩子（序列化/脱敏/日志）
        OnSend(value);

        // 2. 推给每个下游输入 pin
        foreach (var target in _targets)
            target.Receive(value);
    }
}
