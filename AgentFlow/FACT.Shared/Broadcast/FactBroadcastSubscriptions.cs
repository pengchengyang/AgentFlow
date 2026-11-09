// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="FactBroadcastSubscriptions.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace FACT.Shared.Broadcast;

/// <summary>
/// Broadcast subscription names shared by FACT nodes and GUI consumers.
/// Keep these values centralized so publishers and subscribers cannot drift apart.
/// </summary>
public static class FactBroadcastSubscriptions
{
    /// <summary>Subscription used to exchange FACT Launcher settings between nodes and GUI hosts.</summary>
    public const string LauncherSettings = "fact.launcher.settings";

    /// <summary>Subscription used to send a FACT Launcher start request.</summary>
    public const string LauncherStart = "fact.launcher.start";

    /// <summary>Subscription used to return the correlated FACT Launcher start reply.</summary>
    public const string LauncherStartReply = "fact.launcher.start.reply";

    /// <summary>Subscription used to broadcast a FACT Launcher stop request or signal.</summary>
    public const string LauncherStop = "fact.launcher.stop";
}

/// <summary>Shared payload values for the FACT Launcher start request/reply protocol.</summary>
public static class FactLauncherStartSignals
{
    public const string Request = "Start";
    public const string Started = "Started";
}
