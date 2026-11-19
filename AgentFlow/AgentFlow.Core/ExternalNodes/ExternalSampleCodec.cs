// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalSampleCodec.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using BroadcastKit.Contracts;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>
/// Compatibility facade for the shared language-neutral sample codec. Existing external
/// Worker code keeps using this type while NativeHost and Workers share one wire format.
/// </summary>
public static class ExternalSampleCodec
{
    public const int MaxBinaryBytes = BroadcastSampleCodec.MaxBinaryBytes;

    public static JsonObject? ToProtocol(Sample? sample, string fallbackKey) =>
        BroadcastSampleCodec.ToProtocol(sample, fallbackKey);

    public static Sample? FromProtocol(JsonNode? node, DataType expectedType, string pinName) =>
        BroadcastSampleCodec.FromProtocol(node, expectedType, pinName);

    public static Sample? FromProtocol(JsonNode? node, string pinName) =>
        BroadcastSampleCodec.FromProtocol(node, pinName);

    public static JsonObject ParametersToProtocol(IReadOnlyDictionary<string, object?>? parameters)
    {
        var result = new JsonObject();
        if (parameters is null)
            return result;

        foreach (var pair in parameters)
        {
            result[pair.Key] = pair.Value is null
                ? null
                : JsonSerializer.SerializeToNode(pair.Value);
        }
        return result;
    }
}
