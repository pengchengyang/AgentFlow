// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalBroadcastCodec.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using BroadcastKit.Contracts;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>Maps BroadcastKit payloads to the language-neutral Worker payload shape.</summary>
public static class ExternalBroadcastCodec
{
    public static BroadcastTopicMessage ToLocalPayload(JsonObject message)
    {
        var payloadKind = ExternalProtocol.GetString(message, "payloadKind") ?? "json";
        var payload = message["payload"];
        switch (payloadKind.ToLowerInvariant())
        {
            case "sample":
                return BroadcastTopicMessage.FromSample(ParseSample(payload, "sample"));

            case "samples":
                return BroadcastTopicMessage.FromSamples(ParseSamples(payload, "samples"));

            case "dictionary_string_sample":
                return BroadcastTopicMessage.FromSampleDictionary(ParseSampleDictionary(payload));

            case "dictionary_string_samples":
                return BroadcastTopicMessage.FromSampleListDictionary(ParseSampleListDictionary(payload));

            case "empty":
                return new BroadcastTopicMessage();

            case "mixed":
            {
                if (payload is not JsonObject mixed)
                    throw new InvalidDataException("Mixed broadcast payload must be a JSON object.");
                return new BroadcastTopicMessage(
                    payload: GetPayloadText(mixed["payload"]),
                    sample: mixed["sample"] is { } sampleNode ? ParseSample(sampleNode, "sample") : null,
                    samples: mixed["samples"] is { } samplesNode ? ParseSamples(samplesNode, "samples") : null,
                    samplesByKey: mixed["samplesByKey"] is { } samplesByKeyNode
                        ? ParseSampleDictionary(samplesByKeyNode)
                        : null,
                    sampleListsByKey: mixed["sampleListsByKey"] is { } sampleListsByKeyNode
                        ? ParseSampleListDictionary(sampleListsByKeyNode)
                        : null);
            }

            case "json":
            default:
                return BroadcastTopicMessage.FromPayload(GetPayloadText(payload) ?? string.Empty);
        }
    }

    public static (string PayloadKind, JsonNode? Payload) ToWorkerPayload(BroadcastTopicMessage message)
    {
        var hasSamples = message.Sample is not null ||
            (message.Samples is { Count: > 0 }) ||
            (message.SamplesByKey is { Count: > 0 }) ||
            (message.SampleListsByKey is { Count: > 0 } &&
                message.SampleListsByKey.Any(pair => pair.Value.Count > 0));

        if (message.Payload is not null && hasSamples)
        {
            var mixed = new JsonObject
            {
                ["payload"] = message.Payload,
                ["sample"] = ExternalSampleCodec.ToProtocol(message.Sample, "sample"),
                ["samples"] = ToProtocolArray(message.Samples),
                ["samplesByKey"] = ToProtocolDictionary(message.SamplesByKey),
                ["sampleListsByKey"] = ToProtocolListDictionary(message.SampleListsByKey)
            };
            return ("mixed", mixed);
        }

        if (message.Sample is not null)
            return ("sample", ExternalSampleCodec.ToProtocol(message.Sample, "sample"));

        if (message.Samples is { Count: > 0 })
            return ("samples", ToProtocolArray(message.Samples));

        if (message.SamplesByKey is { Count: > 0 })
            return ("dictionary_string_sample", ToProtocolDictionary(message.SamplesByKey));

        if (message.SampleListsByKey is { Count: > 0 } &&
            message.SampleListsByKey.Any(pair => pair.Value.Count > 0))
            return ("dictionary_string_samples", ToProtocolListDictionary(message.SampleListsByKey));

        if (message.Payload is not null)
            return ("json", message.Payload);

        return ("empty", null);
    }

    private static Sample ParseSample(JsonNode? node, string label) =>
        ExternalSampleCodec.FromProtocol(node, label)
        ?? throw new InvalidDataException($"Broadcast {label} payload must not be null.");

    private static IReadOnlyList<Sample> ParseSamples(JsonNode? node, string label)
    {
        if (node is not JsonArray array)
            throw new InvalidDataException($"Broadcast {label} payload must be a JSON array.");
        return array.Select((item, index) =>
            ExternalSampleCodec.FromProtocol(item, $"{label}[{index}]")
            ?? throw new InvalidDataException($"Broadcast {label}[{index}] must not be null.")).ToList();
    }

    private static IReadOnlyDictionary<string, Sample> ParseSampleDictionary(JsonNode? node)
    {
        if (node is not JsonObject obj)
            throw new InvalidDataException("Broadcast sample dictionary payload must be a JSON object.");
        var result = new Dictionary<string, Sample>(StringComparer.Ordinal);
        foreach (var pair in obj)
            result[pair.Key] = ExternalSampleCodec.FromProtocol(pair.Value, pair.Key)
                ?? throw new InvalidDataException($"Broadcast sample dictionary value '{pair.Key}' must not be null.");
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<Sample>> ParseSampleListDictionary(JsonNode? node)
    {
        if (node is not JsonObject obj)
            throw new InvalidDataException("Broadcast sample list dictionary payload must be a JSON object.");
        var result = new Dictionary<string, IReadOnlyList<Sample>>(StringComparer.Ordinal);
        foreach (var pair in obj)
            result[pair.Key] = ParseSamples(pair.Value, $"sampleListsByKey.{pair.Key}");
        return result;
    }

    private static JsonArray? ToProtocolArray(IReadOnlyList<Sample>? samples)
    {
        if (samples is null)
            return null;
        var array = new JsonArray();
        foreach (var sample in samples)
            array.Add(ExternalSampleCodec.ToProtocol(sample, "sample"));
        return array;
    }

    private static JsonObject? ToProtocolDictionary(IReadOnlyDictionary<string, Sample>? samples)
    {
        if (samples is null)
            return null;
        var obj = new JsonObject();
        foreach (var pair in samples)
            obj[pair.Key] = ExternalSampleCodec.ToProtocol(pair.Value, pair.Key);
        return obj;
    }

    private static JsonObject? ToProtocolListDictionary(
        IReadOnlyDictionary<string, IReadOnlyList<Sample>>? samples)
    {
        if (samples is null)
            return null;
        var obj = new JsonObject();
        foreach (var pair in samples)
            obj[pair.Key] = ToProtocolArray(pair.Value);
        return obj;
    }

    private static string? GetPayloadText(JsonNode? payload)
    {
        if (payload is null)
            return null;
        if (payload is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return payload.ToJsonString();
    }
}

