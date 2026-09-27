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
/// </summary>
internal static class NodeIdPool
{
    private const int MinNodeId = 1000;
    private const int MaxNodeId = 5000;

    private static readonly object Gate = new();
    private static readonly SortedSet<int> FreeIds = new();
    private static int _nextId = MinNodeId;

    public static int Allocate()
    {
        lock (Gate)
        {
            if (FreeIds.Count > 0)
            {
                int id = FreeIds.Min;
                FreeIds.Remove(id);
                return id;
            }

            if (_nextId <= MaxNodeId)
                return _nextId++;

            throw new InvalidOperationException(
                $"No available node id in range [{MinNodeId}, {MaxNodeId}].");
        }
    }

    /// <summary>Reserve a specific id (used when restoring a saved node instance).</summary>
    public static void Reserve(int id)
    {
        lock (Gate)
        {
            if (id < MinNodeId || id > MaxNodeId)
                throw new ArgumentOutOfRangeException(nameof(id), id,
                    $"Node id must be in range [{MinNodeId}, {MaxNodeId}].");
            if (id == _nextId)
            {
                _nextId++;
                return;
            }
            if (FreeIds.Remove(id))
                return;
            throw new InvalidOperationException($"Node id {id} is already in use.");
        }
    }

    public static void Release(int id)
    {
        lock (Gate)
        {
            if (id >= MinNodeId && id <= MaxNodeId)
                FreeIds.Add(id);
        }
    }

    /// <summary>Clear the free pool and restart allocation from the minimum id.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            FreeIds.Clear();
            _nextId = MinNodeId;
        }
    }
}
