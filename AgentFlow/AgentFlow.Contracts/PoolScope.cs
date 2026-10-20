// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="PoolScope.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Provides the <b>current</b> node / pin id pools for a creation operation.
/// <para>
/// Nodes and pins allocate their ids at construction (in their subclass constructors, reached
/// through reflection), so there is no constructor parameter to pass a pool into. Instead this
/// ambient scope lets an executor (<see cref="AgentFlow.Core.WorkflowRunner"/> owned mode) install a
/// per-run pool before instantiating a workflow: every pin created while the scope is active
/// draws from that run's pool, so concurrent runs never share ids. When no scope is active the
/// <see cref="NodeIdPool.Default"/> / <see cref="PinIdPool.Default"/> singletons are used, preserving the
/// classic Desktop editing behaviour exactly.
/// </para>
/// <para>The scope is <see cref="AsyncLocal{T}"/>-backed so it flows correctly across asynchronous
/// boundaries and is isolated per concurrent run.</para>
/// </summary>
internal static class PoolScope
{
    private static readonly AsyncLocal<NodeIdPool?> NodePoolHolder = new();
    private static readonly AsyncLocal<PinIdPool?> PinPoolHolder = new();

    /// <summary>The current node id pool (the default singleton when no scope is active).</summary>
    public static NodeIdPool NodePool => NodePoolHolder.Value ?? NodeIdPool.Default;

    /// <summary>The current pin id pool (the default singleton when no scope is active).</summary>
    public static PinIdPool PinPool => PinPoolHolder.Value ?? PinIdPool.Default;

    /// <summary>
    /// Install per-run pools for the duration of the returned scope. Pass null to leave that
    /// pool as the default singleton.
    /// </summary>
    public static IDisposable Enter(NodeIdPool? nodePool, PinIdPool? pinPool)
    {
        var prevNode = NodePoolHolder.Value;
        var prevPin = PinPoolHolder.Value;
        NodePoolHolder.Value = nodePool ?? NodeIdPool.Default;
        PinPoolHolder.Value = pinPool ?? PinIdPool.Default;
        return new Restore(prevNode, prevPin);
    }

    private sealed class Restore : IDisposable
    {
        private readonly NodeIdPool? _node;
        private readonly PinIdPool? _pin;
        private bool _disposed;

        public Restore(NodeIdPool? node, PinIdPool? pin)
        {
            _node = node;
            _pin = pin;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NodePoolHolder.Value = _node;
            PinPoolHolder.Value = _pin;
        }
    }
}
