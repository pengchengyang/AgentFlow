namespace AgentFlow.Contracts;

/// <summary>
/// Optional lifecycle support for nodes that own resources (cameras, sockets,
/// DAQ boards, etc.). The engine calls these hooks around a workflow run:
/// InitializeAsync before the first node executes, StartAsync just before the
/// execution pass, and StopAsync after the pass completes (or is cancelled).
/// Nodes that do not need lifecycle management simply do not implement this
/// interface.
/// </summary>
public interface ILifecycleNode
{
    /// <summary>Called once before any node executes.</summary>
    Task InitializeAsync(INodeContext context, CancellationToken cancellationToken = default);

    /// <summary>Called just before the execution pass starts (all nodes, priority order).</summary>
    Task StartAsync(INodeContext context, CancellationToken cancellationToken = default);

    /// <summary>Called after the execution pass ends (reverse priority order).</summary>
    Task StopAsync(INodeContext context, CancellationToken cancellationToken = default);
}
