// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="PinIdPool.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Allocates unique pin instance ids in the range 6000..10000. Each <see cref="BasePin"/>
/// receives an id at construction; ids are returned to the pool when a pin is released so
/// the next created pin reuses the smallest available id.
/// <para>
/// The pool is <b>instance-level</b>: every <see cref="AgentFlow.Core.WorkflowRunner"/> (owned mode) creates
/// its own pool so each workflow's pin ids start from the minimum again and concurrent runs
/// never share / collide. The <see cref="Default"/> singleton backs the classic Desktop editing flow
/// (via <see cref="PoolScope"/>) with no behaviour change.
/// </para>
/// </summary>
internal sealed class PinIdPool
{
    private const int MinPinId = 6000;
    private const int MaxPinId = 10000;

    /// <summary>The shared pool used when no explicit scope is active (Desktop editing / attached mode).</summary>
    public static PinIdPool Default { get; } = new();

    private readonly object _gate = new();
    private readonly SortedSet<int> _freeIds = new();
    private int _nextId = MinPinId;

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

            if (_nextId <= MaxPinId)
                return _nextId++;

            throw new InvalidOperationException(
                $"No available pin id in range [{MinPinId}, {MaxPinId}].");
        }
    }

    /// <summary>Reserve a specific id (used when restoring a saved pin instance).</summary>
    public void Reserve(int id)
    {
        lock (_gate)
        {
            if (id < MinPinId || id > MaxPinId)
                throw new ArgumentOutOfRangeException(nameof(id), id,
                    $"Pin id must be in range [{MinPinId}, {MaxPinId}].");
            if (id == _nextId)
            {
                _nextId++;
                return;
            }
            if (_freeIds.Remove(id))
                return;
            throw new InvalidOperationException($"Pin id {id} is already in use.");
        }
    }

    public void Release(int id)
    {
        lock (_gate)
        {
            if (id >= MinPinId && id <= MaxPinId)
                _freeIds.Add(id);
        }
    }

    /// <summary>Clear the free pool and restart allocation from the minimum id.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _freeIds.Clear();
            _nextId = MinPinId;
        }
    }
}
