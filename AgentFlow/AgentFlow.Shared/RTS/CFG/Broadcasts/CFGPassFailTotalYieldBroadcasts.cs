// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CFGPassFailTotalYieldBroadcasts.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Shared.RTS.CFG.Broadcasts;

/// <summary>
/// Broadcast topics used to control the pass, fail, total, and yield node.
/// </summary>
public static class CFGPassFailTotalYieldBroadcasts
{
    /// <summary>GUI-to-node command that resets all Yield counters.</summary>
    public const string CFGReset = "Pass.Fail.Total.Yield.Reset.70CBA995-4A34-4CC8-8789-F0E29F5058C0";
}