// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BasePinDispatchLogging.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace AgentFlow.Contracts;

/// <summary>
/// Adds consistent dispatch logging around the synchronous <see cref="BasePin.SendSample"/>
/// call without changing delivery behavior or downstream execution order.
/// </summary>
public static class BasePinDispatchLogging
{
    /// <summary>
    /// Log a dispatch start, send the sample synchronously, then log dispatch completion.
    /// High-frequency sends should use <see cref="LogLevel.Trace"/>; low-frequency business
    /// events may use <see cref="LogLevel.Information"/>.
    /// </summary>
    public static bool SendSampleWithDispatchLog(
        this BasePin outputPin,
        Sample? value,
        string operation,
        LogLevel level = LogLevel.Trace)
    {
        ArgumentNullException.ThrowIfNull(outputPin);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var logger = outputPin.Context?.Logger;
        var nodeName = outputPin.Owner?.DisplayName ?? "<unassigned>";

        logger?.Log(
            level,
            "{Node}.{Pin} dispatching {Operation}.",
            nodeName,
            outputPin.Name,
            operation);

        var sent = outputPin.SendSample(value);

        logger?.Log(
            level,
            "{Node}.{Pin} dispatch completed for {Operation}; sent={Sent}.",
            nodeName,
            outputPin.Name,
            operation,
            sent);

        return sent;
    }
}