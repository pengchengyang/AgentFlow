namespace AgentFlow.Contracts;

/// <summary>
/// Convenience base for <see cref="INode"/>. A node's pins are fixed at development
/// time, so subclasses only declare their <see cref="InputPins"/> / <see cref="OutputPins"/>
/// as read-only lists (e.g. with collection expressions) and provide <see cref="TypeId"/>,
/// <see cref="DisplayName"/>, <see cref="Configure"/> and <see cref="ExecuteAsync"/>.
/// </summary>
public abstract class BaseNode : INode
{
    /// <inheritdoc/>
    public abstract string TypeId { get; }

    /// <inheritdoc/>
    /// <remarks>Default 0 until AgentFlow.Core assigns it. AgentFlow.Core may
    /// reassign it to a user/UI-chosen value at runtime.</remarks>
    public int InstanceId { get; set; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public abstract IReadOnlyList<PinDefinition> InputPins { get; }

    /// <inheritdoc/>
    public abstract IReadOnlyList<PinDefinition> OutputPins { get; }

    /// <inheritdoc/>
    public virtual IReadOnlyList<ParameterDefinition> Parameters => Array.Empty<ParameterDefinition>();

    /// <inheritdoc/>
    public virtual void Receive(INodeContext context, PinDefinition pin, object? value) { }

    /// <inheritdoc/>
    public abstract void Configure(IReadOnlyDictionary<string, object?> parameters);

    /// <inheritdoc/>
    public abstract Task ExecuteAsync(INodeContext context, CancellationToken cancellationToken = default);
}
