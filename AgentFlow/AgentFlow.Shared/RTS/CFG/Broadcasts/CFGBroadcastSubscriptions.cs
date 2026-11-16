// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CFGBroadcastSubscriptions.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Shared.RTS.CFG.Broadcasts;

/// <summary>
/// Broadcast subscription names shared by RTS nodes and GUI consumers.
/// Keep these values centralized so publishers and subscribers cannot drift apart.
/// </summary>
public static class CFGBroadcastSubscriptions
{
    /// <summary>Subscription used to exchange launcher settings between nodes and GUI hosts.</summary>
    public const string CFGLauncherSettings = "FACT.Launcher.Settings.48D865B9-AB18-4DD0-A7A8-0F79D63EEDBB";

    /// <summary>Subscription used to send a launcher start request.</summary>
    public const string CFGLauncherStart = "FACT.Launcher.Start.660079D0-4F28-4B98-8C7D-81EF060AC9DD";

    /// <summary>Subscription used to return the correlated launcher start reply.</summary>
    public const string CFGLauncherStartReply = "FACT.Launcher.Start.Reply.C394EB3B-0382-45B2-A27A-91E82FD37917";

    /// <summary>Subscription used to broadcast a launcher stop request or signal.</summary>
    public const string CFGLauncherStop = "FACT.Launcher.Stop.628779B8-57FF-407F-B252-FD9C448B67CC";

    /// <summary>Subscription used to broadcast a successfully dispatched FACT command.</summary>
    public const string CFGLauncherCommand = "FACT.Launcher.Command.C3578D71-A049-43B8-AFE1-2482C1D0FFDD";

    /// <summary>Subscription used to publish parsed PRI file settings as a keyed Sample dictionary.</summary>
    public const string CFGPriFileSettings = "Pri.File.Settings.00850D6A-1F19-4040-B2D2-BA4E3E8E0F93";

    /// <summary>Subscription used to broadcast the Auto Stop exit JSON payload.</summary>
    public const string CFGAutoStopExit = "Workflow.AutoStop.Exit.5526559F-0CEF-4728-90F2-C87943496AA8";

    /// <summary>Subscription used to publish pass, fail, total, and yield values.</summary>
    public const string CFGPassFailTotalYield = "Pass.Fail.Total.Yield.0314C9D4-964F-4520-AC02-8E1A8729754A";
}

/// <summary>Shared payload values for the launcher start request/reply protocol.</summary>
public static class CFGLauncherStartSignals
{
    public const string CFGRequest = "Start";
    public const string CFGStarted = "Started";
}