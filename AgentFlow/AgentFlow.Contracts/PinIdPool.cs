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
/// </summary>
internal static class PinIdPool
{
    private const int MinPinId = 6000;
    private const int MaxPinId = 10000;

    private static readonly object Gate = new();
    private static readonly SortedSet<int> FreeIds = new();
    private static int _nextId = MinPinId;

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

            if (_nextId <= MaxPinId)
                return _nextId++;

            throw new InvalidOperationException(
                $"No available pin id in range [{MinPinId}, {MaxPinId}].");
        }
    }

    /// <summary>Reserve a specific id (used when restoring a saved pin instance).</summary>
    public static void Reserve(int id)
    {
        lock (Gate)
        {
            if (id < MinPinId || id > MaxPinId)
                throw new ArgumentOutOfRangeException(nameof(id), id,
                    $"Pin id must be in range [{MinPinId}, {MaxPinId}].");
            if (id == _nextId)
            {
                _nextId++;
                return;
            }
            if (FreeIds.Remove(id))
                return;
            throw new InvalidOperationException($"Pin id {id} is already in use.");
        }
    }

    public static void Release(int id)
    {
        lock (Gate)
        {
            if (id >= MinPinId && id <= MaxPinId)
                FreeIds.Add(id);
        }
    }

    /// <summary>Clear the free pool and restart allocation from the minimum id.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            FreeIds.Clear();
            _nextId = MinPinId;
        }
    }
}
