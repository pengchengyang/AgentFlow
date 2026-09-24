// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastMessage.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Broadcast;

/// <summary>A broadcast message sent through <see cref="BroadcastHub"/>.</summary>
public sealed record BroadcastMessage(
    string Topic,
    object? Payload,
    object? Sender,
    DateTimeOffset Timestamp);
