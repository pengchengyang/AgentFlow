// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeIdPool.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Allocates unique node instance ids in the range 1000..5000. Each node created through
/// <see cref="AgentFlow.Core.PluginLoader.CreateNodeInstance"/> receives an id; ids are returned to the pool
/// when a node is deleted so the next created node reuses the smallest available id.
/// <para>
/// The pool is <b>instance-level</b>: every <see cref="AgentFlow.Core.WorkflowRunner"/> (owned mode) creates
/// its own pool so each workflow's ids start from the minimum again and concurrent runs never
/// share / collide. The <see cref="Default"/> singleton backs the classic Desktop editing flow
/// (via <see cref="PoolScope"/>) with no behaviour change.
/// </para>
/// </summary>
internal sealed class NodeIdPool
{
    private const int MinNodeId = 1000;
    private const int MaxNodeId = 5000;

    /// <summary>The shared pool used when no explicit scope is active (Desktop editing / attached mode).</summary>
    public static NodeIdPool Default { get; } = new();

    private readonly object _gate = new();
    private readonly SortedSet<int> _freeIds = new();
    private int _nextId = MinNodeId;

    public int Allocate()
    {
        lock (_gate)
        {
            if (_freeIds.Count > 0)
            {
                int id = _freeIds.Min;
                _freeIds.Remove(id);
                return id;
            }

            if (_nextId <= MaxNodeId)
                return _nextId++;

            throw new InvalidOperationException(
                $"No available node id in range [{MinNodeId}, {MaxNodeId}].");
        }
    }

    /// <summary>Reserve a specific id (used when restoring a saved node instance).</summary>
    public void Reserve(int id)
    {
        lock (_gate)
        {
            if (id < MinNodeId || id > MaxNodeId)
                throw new ArgumentOutOfRangeException(nameof(id), id,
                    $"Node id must be in range [{MinNodeId}, {MaxNodeId}].");
            if (id == _nextId)
            {
                _nextId++;
                return;
            }
            if (_freeIds.Remove(id))
                return;
            throw new InvalidOperationException($"Node id {id} is already in use.");
        }
    }

    public void Release(int id)
    {
        lock (_gate)
        {
            if (id >= MinNodeId && id <= MaxNodeId)
                _freeIds.Add(id);
        }
    }

    /// <summary>Clear the free pool and restart allocation from the minimum id.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _freeIds.Clear();
            _nextId = MinNodeId;
        }
    }
}
