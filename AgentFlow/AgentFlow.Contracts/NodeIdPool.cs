// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeIdPool.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Allocates unique node instance ids in the range 1000..5000 for one workflow document.
/// <para>
/// The pool is an instance (owned by <see cref="AgentFlow.Core.PluginLoader"/>) rather than
/// process-wide static state, so two documents / loaders in the same process cannot collide.
/// Each node created through <see cref="AgentFlow.Core.PluginLoader.CreateNodeInstance"/> receives
/// an id; ids are returned to the pool when a node is deleted, and a restored document may reuse
/// ids with holes (e.g. 1000 / 1002 after 1001 was deleted).
/// </para>
/// </summary>
internal sealed class NodeIdPool
{
    private const int MinNodeId = 1000;
    private const int MaxNodeId = 5000;

    private readonly object _gate = new();
    private readonly SortedSet<int> _freeIds = new();
    private readonly HashSet<int> _reservedIds = new();
    private int _nextId = MinNodeId;

    public int Allocate()
    {
        lock (_gate)
        {
            if (_freeIds.Count > 0)
            {
                int id = _freeIds.Min;
                _freeIds.Remove(id);
                _reservedIds.Add(id);
                return id;
            }

            if (_nextId <= MaxNodeId)
            {
                int id = _nextId++;
                _reservedIds.Add(id);
                return id;
            }

            throw new InvalidOperationException(
                $"No available node id in range [{MinNodeId}, {MaxNodeId}].");
        }
    }

    /// <summary>
    /// Reserve a specific id (used when restoring a saved node instance). Unlike the previous
    /// "only the next id" rule this accepts any id in range that is not reserved yet, and advances
    /// the allocation cursor past it, so a document with id holes loads correctly.
    /// </summary>
    public void Reserve(int id)
    {
        lock (_gate)
        {
            if (id < MinNodeId || id > MaxNodeId)
                throw new ArgumentOutOfRangeException(nameof(id), id,
                    $"Node id must be in range [{MinNodeId}, {MaxNodeId}].");

            if (_reservedIds.Contains(id))
                throw new InvalidOperationException($"Node id {id} is already in use.");

            _freeIds.Remove(id);
            _reservedIds.Add(id);
            if (id >= _nextId)
                _nextId = id + 1;
        }
    }

    public void Release(int id)
    {
        lock (_gate)
        {
            if (id < MinNodeId || id > MaxNodeId)
                return;

            if (_reservedIds.Remove(id))
                _freeIds.Add(id);
        }
    }

    /// <summary>Clear the free pool and restart allocation from the minimum id.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _freeIds.Clear();
            _reservedIds.Clear();
            _nextId = MinNodeId;
        }
    }
}
