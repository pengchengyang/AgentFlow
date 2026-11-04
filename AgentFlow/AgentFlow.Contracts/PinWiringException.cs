// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="PinWiringException.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// A pin-level wiring error (type mismatch, output used as input, ...). It is a node-configuration /
/// graph problem rather than a business failure, so the host logs it with a distinct message.
/// </summary>
public sealed class PinWiringException : InvalidOperationException
{
    public PinWiringException(string message) : base(message)
    {
    }
}
