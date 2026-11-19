// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalBroadcastCodec.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json.Nodes;
using BroadcastKit.Contracts;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>
/// Compatibility facade for the shared broadcast codec. External Worker callers remain
/// source-compatible while NativeHost uses the same payload definitions.
/// </summary>
public static class ExternalBroadcastCodec
{
    public static BroadcastTopicMessage ToLocalPayload(JsonObject message)
    {
        var payloadKind = ExternalProtocol.GetString(message, "payloadKind");
        return BroadcastTopicCodec.FromWirePayload(payloadKind, message["payload"]);
    }

    public static (string PayloadKind, JsonNode? Payload) ToWorkerPayload(BroadcastTopicMessage message) =>
        BroadcastTopicCodec.ToWirePayload(message);
}
