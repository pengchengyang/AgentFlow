// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="PinIdAllocator.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Hands out the persistent pin ids used by the editor document
/// (<c>logic.pins[].id</c> and the connection endpoints that reference them).
/// <para>
/// One allocator belongs to one document (<see cref="AgentFlow.Core.EditorGraph"/>), so the ids only
/// have to be unique <b>inside that document</b> — there is no process-wide pool any more. That means:
/// </para>
/// <list type="bullet">
/// <item>Creating nodes / pins consumes no ids: they are assigned lazily, right before a save
/// (<c>EditorGraph.EnsurePinIds</c>). Headless runs (CLI) therefore never touch this class at all,
/// so a process can host any number of workflow instances.</item>
/// <item>Loading is tolerant: persisted ids are only reported via <see cref="Observe(int)"/>, which
/// raises the counter above them — ids may be sparse / arbitrary and nothing can collide.</item>
/// <item>Removing a node / pin needs no bookkeeping; freed ids are simply skipped.</item>
/// </list>
/// </summary>
internal sealed class PinIdAllocator
{
    /// <summary>First id handed out. Kept at 6000 so documents saved by the previous pool stay compatible.</summary>
    private const int MinPinId = 6000;

    private readonly object _gate = new();
    private int _max = MinPinId - 1;

    /// <summary>Allocate the next id for a pin that was created since the last save.</summary>
    public int Allocate()
    {
        lock (_gate) return ++_max;
    }

    /// <summary>
    /// Report an id that already exists (restored from a document) so future allocations stay above it.
    /// Idempotent and safe to call for every pin, every save.
    /// </summary>
    public void Observe(int id)
    {
        lock (_gate)
        {
            if (id > _max) _max = id;
        }
    }

    /// <summary>
    /// Forget every id handed out so far (used when the owning document is cleared / a different
    /// document is loaded). The next save observes the ids of the newly loaded pins, so allocation
    /// restarts just above the ids that document actually uses instead of continuing to grow.
    /// </summary>
    public void Reset()
    {
        lock (_gate) _max = MinPinId - 1;
    }
}
