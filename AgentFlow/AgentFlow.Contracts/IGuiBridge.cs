// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="IGuiBridge.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>Message published through the GuiBridge.</summary>
public sealed record GuiMessage(string Topic, object? Payload, DateTimeOffset Timestamp);

/// <summary>
/// Communication bridge between node instances and external business GUIs
/// (industrial dashboards, yield monitors, etc.).
/// Nodes only depend on this interface; the editor, CLI, and business GUIs
/// each provide their own implementation. V1 is in-process; swapping to an
/// inter-process implementation later requires no node code changes.
/// </summary>
public interface IGuiBridge
{
    /// <summary>Publish data to the external GUI (e.g. yield rate, device status).</summary>
    void Publish(string topic, object? payload);

    /// <summary>Subscribe to a topic (used by the external GUI).</summary>
    IDisposable Subscribe(string topic, Action<GuiMessage> handler);
}
