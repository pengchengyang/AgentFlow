// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastHandlerAttribute.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Broadcast;

/// <summary>
/// Marks a method as a broadcast handler for one or more topics.
/// The method can have either zero parameters or a single parameter
/// assignable from <see cref="BroadcastMessage"/> (e.g. <c>BroadcastMessage</c> or <c>object</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class BroadcastHandlerAttribute : Attribute
{
    public string Topic { get; }

    public BroadcastHandlerAttribute(string topic)
    {
        Topic = topic;
    }
}
