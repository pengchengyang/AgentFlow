// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="RunStartGate.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Services;

/// <summary>
/// Synchronously reserves the single start transition shared by Editor Run and Batch Run. The
/// reservation is held until the started workflow has published its real running state, closing
/// the gap caused by timer-based UI state refresh.
/// </summary>
public sealed class RunStartGate
{
    private int _held;

    public bool IsHeld => Volatile.Read(ref _held) != 0;

    public bool TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _held, 0);
}
