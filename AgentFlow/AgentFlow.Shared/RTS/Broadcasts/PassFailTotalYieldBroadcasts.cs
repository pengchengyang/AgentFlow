// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="PassFailTotalYieldBroadcasts.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Shared.RTS.Broadcasts;

/// <summary>
/// Broadcast topics used to control the pass, fail, total, and yield node.
/// </summary>
public static class PassFailTotalYieldBroadcasts
{
    /// <summary>GUI-to-node command that resets all Yield counters.</summary>
    public const string Reset = "Pass.Fail.Total.Yield.Reset";
}
