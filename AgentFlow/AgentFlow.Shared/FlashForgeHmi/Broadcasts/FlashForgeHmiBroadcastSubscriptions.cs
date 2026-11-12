// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="FlashForgeHmiBroadcastSubscriptions.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Shared.FlashForgeHmi.Broadcasts;

/// <summary>
/// Broadcast subscription names shared by FlashForgeHMI nodes and GUI consumers.
/// Keep these values centralized so publishers and subscribers cannot drift apart.
/// </summary>
public static class FlashForgeHmiBroadcastSubscriptions
{
    /// <summary>Subscription used to exchange launcher settings between nodes and GUI hosts.</summary>
    public const string LauncherSettings = "FACT.Launcher.Settings";

    /// <summary>Subscription used to send a launcher start request.</summary>
    public const string LauncherStart = "FACT.Launcher.Start";

    /// <summary>Subscription used to return the correlated launcher start reply.</summary>
    public const string LauncherStartReply = "FACT.Launcher.Start.Reply";

    /// <summary>Subscription used to broadcast a launcher stop request or signal.</summary>
    public const string LauncherStop = "FACT.Launcher.Stop";

    /// <summary>Subscription used to broadcast a successfully dispatched FACT command.</summary>
    public const string LauncherCommand = "FACT.Launcher.Command";

    /// <summary>Subscription used to publish parsed PRI file settings as a keyed Sample dictionary.</summary>
    public const string PriFileSettings = "Pri.File.Settings";

    /// <summary>Subscription used to publish pass, fail, total, and yield values.</summary>
    public const string PassFailTotalYield = "Pass.Fail.Total.Yield";
}

/// <summary>Shared payload values for the launcher start request/reply protocol.</summary>
public static class FlashForgeHmiLauncherStartSignals
{
    public const string Request = "Start";
    public const string Started = "Started";
}
