// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastTopicCodec.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;

namespace BroadcastKit.Contracts;

/// <summary>Canonical language-neutral payload kinds used by topic broadcasts.</summary>
public static class BroadcastPayloadKinds
{
    public const string Empty = "empty";
    public const string Json = "json";
    public const string Sample = "sample";
    public const string Samples = "samples";
    public const string SampleDictionary = "dictionary_string_sample";
    public const string SampleListDictionary = "dictionary_string_samples";
    public const string Mixed = "mixed";
}

/// <summary>Maps AgentFlow samples to and from the cross-language protocol shape.</summary>
public static class BroadcastSampleCodec
{
    public const int MaxBinaryBytes = 1024 * 1024;

    public static JsonObject? ToProtocol(Sample? sample, string fallbackKey)
    {
        if (sample is null)
            return null;

        var key = string.IsNullOrEmpty(sample.Key) ? fallbackKey : sample.Key;
        var dataType = sample.Type.ToString();

        return sample.Type switch
        {
            DataType.STRING => Create(key, dataType, "utf8", sample.StringValue),
            DataType.INT32 => Create(key, dataType, "decimal", sample.Int32Value),
            DataType.INT64 => Create(key, dataType, "decimal-string",
                sample.Int64Value?.ToString(CultureInfo.InvariantCulture)),
            DataType.BOOL => Create(key, dataType, "boolean", sample.BoolValue),
            DataType.FLOAT => Create(key, dataType, "ieee754-binary32-bits-hex",
                sample.FloatValue is { } f
                    ? BitConverter.SingleToUInt32Bits(f).ToString("x8", CultureInfo.InvariantCulture)
                    : null),
            DataType.DOUBLE => Create(key, dataType, "ieee754-binary64-bits-hex",
                sample.DoubleValue is { } d
                    ? BitConverter.DoubleToUInt64Bits(d).ToString("x16", CultureInfo.InvariantCulture)
                    : null),
            DataType.BIN => Create(key, dataType, "base64",
                sample.BinValue is { } bytes ? Convert.ToBase64String(bytes) : null),
            _ => throw new InvalidDataException($"Unsupported sample type: {sample.Type}")
        };
    }

    public static Sample? FromProtocol(JsonNode? node, DataType expectedType, string pinName)
    {
        if (node is null)
            return null;

        if (node is not JsonObject obj)
            throw new InvalidDataException($"Sample for pin '{pinName}' must be a JSON object.");

        var dataTypeText = GetString(obj, "dataType")
            ?? throw new InvalidDataException($"Sample for pin '{pinName}' is missing dataType.");
        if (!Enum.TryParse<DataType>(dataTypeText, ignoreCase: true, out var actualType))
            throw new InvalidDataException($"Sample for pin '{pinName}' has unknown dataType '{dataTypeText}'.");
        if (actualType != expectedType)
            throw new InvalidDataException(
                $"Sample type mismatch for pin '{pinName}': expected {expectedType}, received {actualType}.");

        var key = GetString(obj, "key") ?? pinName;
        var encoding = GetString(obj, "encoding") ?? "";

        Sample sample = actualType switch
        {
            DataType.STRING => StringSample.CreateSample(),
            DataType.INT32 => Int32Sample.CreateSample(),
            DataType.INT64 => Int64Sample.CreateSample(),
            DataType.BOOL => BoolSample.CreateSample(),
            DataType.FLOAT => FloatSample.CreateSample(),
            DataType.DOUBLE => DoubleSample.CreateSample(),
            DataType.BIN => BinSample.CreateSample(),
            _ => throw new InvalidDataException($"Unsupported sample dataType: {actualType}")
        };

        object? value = actualType switch
        {
            DataType.STRING when encoding.Equals("utf8", StringComparison.OrdinalIgnoreCase)
                => GetRawValue(obj, "value")?.GetValue<string>(),
            DataType.INT32 when encoding.Equals("decimal", StringComparison.OrdinalIgnoreCase)
                => ParseInt32(obj, pinName),
            DataType.INT64 when encoding.Equals("decimal-string", StringComparison.OrdinalIgnoreCase)
                => ParseInt64(obj, pinName),
            DataType.BOOL when encoding.Equals("boolean", StringComparison.OrdinalIgnoreCase)
                => ParseBool(obj, pinName),
            DataType.FLOAT when encoding.Equals("ieee754-binary32-bits-hex", StringComparison.OrdinalIgnoreCase)
                => ParseFloatBits(obj, pinName),
            DataType.DOUBLE when encoding.Equals("ieee754-binary64-bits-hex", StringComparison.OrdinalIgnoreCase)
                => ParseDoubleBits(obj, pinName),
            DataType.BIN when encoding.Equals("base64", StringComparison.OrdinalIgnoreCase)
                => ParseBinary(obj, pinName),
            _ => throw new InvalidDataException(
                $"Sample for pin '{pinName}' has unsupported encoding '{encoding}' for {actualType}.")
        };

        sample.SetValue(value, key);
        return sample;
    }

    public static Sample? FromProtocol(JsonNode? node, string pinName)
    {
        if (node is not JsonObject obj)
        {
            return node is null
                ? null
                : throw new InvalidDataException($"Sample for pin '{pinName}' must be a JSON object.");
        }

        var dataTypeText = GetString(obj, "dataType")
            ?? throw new InvalidDataException($"Sample for pin '{pinName}' is missing dataType.");
        if (!Enum.TryParse<DataType>(dataTypeText, ignoreCase: true, out var dataType))
            throw new InvalidDataException($"Sample for pin '{pinName}' has unknown dataType '{dataTypeText}'.");

        return FromProtocol(node, dataType, pinName);
    }

    private static JsonObject Create(
        string key,
        string dataType,
        string encoding,
        object? value)
    {
        return new JsonObject
        {
            ["key"] = key,
            ["dataType"] = dataType,
            ["encoding"] = encoding,
            ["value"] = value is null ? null : JsonValue.Create(value)
        };
    }

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonNode? GetRawValue(JsonObject obj, string name) => obj[name];

    private static int ParseInt32(JsonObject obj, string pinName)
    {
        var node = GetRawValue(obj, "value");
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number)) return number;
            if (value.TryGetValue<long>(out var longNumber)) return checked((int)longNumber);
            if (value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        throw new InvalidDataException($"Sample for pin '{pinName}' has an invalid INT32 value.");
    }

    private static long ParseInt64(JsonObject obj, string pinName)
    {
        var node = GetRawValue(obj, "value");
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var number)) return number;
            if (value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        throw new InvalidDataException($"Sample for pin '{pinName}' has an invalid INT64 value.");
    }

    private static bool ParseBool(JsonObject obj, string pinName)
    {
        var node = GetRawValue(obj, "value");
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean)) return boolean;
            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed)) return parsed;
        }
        throw new InvalidDataException($"Sample for pin '{pinName}' has an invalid BOOL value.");
    }

    private static float ParseFloatBits(JsonObject obj, string pinName)
    {
        var text = GetRawValue(obj, "value")?.GetValue<string>();
        if (uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
            return BitConverter.UInt32BitsToSingle(bits);
        throw new InvalidDataException($"Sample for pin '{pinName}' has invalid FLOAT bits.");
    }

    private static double ParseDoubleBits(JsonObject obj, string pinName)
    {
        var text = GetRawValue(obj, "value")?.GetValue<string>();
        if (ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
            return BitConverter.UInt64BitsToDouble(bits);
        throw new InvalidDataException($"Sample for pin '{pinName}' has invalid DOUBLE bits.");
    }

    private static byte[] ParseBinary(JsonObject obj, string pinName)
    {
        var text = GetRawValue(obj, "value")?.GetValue<string>() ?? "";
        try
        {
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length > MaxBinaryBytes)
                throw new InvalidDataException(
                    $"Sample for pin '{pinName}' exceeds the {MaxBinaryBytes} byte BIN limit.");
            return bytes;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"Sample for pin '{pinName}' has invalid base64 BIN data.", ex);
        }
    }
}

/// <summary>Maps <see cref="BroadcastTopicMessage"/> to and from the cross-language payload shape.</summary>
public static class BroadcastTopicCodec
{
    public static BroadcastTopicMessage FromWirePayload(string? payloadKind, JsonNode? payload)
    {
        var kind = string.IsNullOrWhiteSpace(payloadKind)
            ? BroadcastPayloadKinds.Json
            : payloadKind.Trim().ToLowerInvariant();

        return kind switch
        {
            BroadcastPayloadKinds.Sample => BroadcastTopicMessage.FromSample(ParseSample(payload, "sample")),
            BroadcastPayloadKinds.Samples => BroadcastTopicMessage.FromSamples(ParseSamples(payload, "samples")),
            BroadcastPayloadKinds.SampleDictionary => BroadcastTopicMessage.FromSampleDictionary(ParseSampleDictionary(payload)),
            BroadcastPayloadKinds.SampleListDictionary => BroadcastTopicMessage.FromSampleListDictionary(ParseSampleListDictionary(payload)),
            BroadcastPayloadKinds.Empty => new BroadcastTopicMessage(),
            BroadcastPayloadKinds.Mixed => FromMixedPayload(payload),
            BroadcastPayloadKinds.Json => BroadcastTopicMessage.FromPayload(GetPayloadText(payload) ?? string.Empty),
            _ => throw new InvalidDataException(
                $"Unsupported broadcast payload kind '{payloadKind}'. Expected one of: " +
                $"{BroadcastPayloadKinds.Empty}, {BroadcastPayloadKinds.Json}, {BroadcastPayloadKinds.Sample}, " +
                $"{BroadcastPayloadKinds.Samples}, {BroadcastPayloadKinds.SampleDictionary}, " +
                $"{BroadcastPayloadKinds.SampleListDictionary}, {BroadcastPayloadKinds.Mixed}.")
        };
    }

    public static (string PayloadKind, JsonNode? Payload) ToWirePayload(BroadcastTopicMessage message)
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
                ["sample"] = BroadcastSampleCodec.ToProtocol(message.Sample, "sample"),
                ["samples"] = ToProtocolArray(message.Samples),
                ["samplesByKey"] = ToProtocolDictionary(message.SamplesByKey),
                ["sampleListsByKey"] = ToProtocolListDictionary(message.SampleListsByKey)
            };
            return (BroadcastPayloadKinds.Mixed, mixed);
        }

        if (message.Sample is not null)
            return (BroadcastPayloadKinds.Sample, BroadcastSampleCodec.ToProtocol(message.Sample, "sample"));

        if (message.Samples is { Count: > 0 })
            return (BroadcastPayloadKinds.Samples, ToProtocolArray(message.Samples));

        if (message.SamplesByKey is { Count: > 0 })
            return (BroadcastPayloadKinds.SampleDictionary, ToProtocolDictionary(message.SamplesByKey));

        if (message.SampleListsByKey is { Count: > 0 } &&
            message.SampleListsByKey.Any(pair => pair.Value.Count > 0))
            return (BroadcastPayloadKinds.SampleListDictionary, ToProtocolListDictionary(message.SampleListsByKey));

        if (message.Payload is not null)
            return (BroadcastPayloadKinds.Json, message.Payload);

        return (BroadcastPayloadKinds.Empty, null);
    }

    public static BroadcastTopicMessage ToLocalPayload(object? payload)
    {
        return payload switch
        {
            null => new BroadcastTopicMessage(),
            BroadcastTopicMessage native => native,
            string text => BroadcastTopicMessage.FromPayload(text),
            Sample sample => BroadcastTopicMessage.FromSample(sample),
            IReadOnlyList<Sample> samples => BroadcastTopicMessage.FromSamples(samples),
            IReadOnlyDictionary<string, Sample> samplesByKey
                => BroadcastTopicMessage.FromSampleDictionary(samplesByKey),
            IReadOnlyDictionary<string, IReadOnlyList<Sample>> sampleListsByKey
                => BroadcastTopicMessage.FromSampleListDictionary(sampleListsByKey),
            _ => BroadcastTopicMessage.FromPayload(JsonSerializer.Serialize(payload))
        };
    }

    private static BroadcastTopicMessage FromMixedPayload(JsonNode? payload)
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

    private static Sample ParseSample(JsonNode? node, string label) =>
        BroadcastSampleCodec.FromProtocol(node, label)
        ?? throw new InvalidDataException($"Broadcast {label} payload must not be null.");

    private static IReadOnlyList<Sample> ParseSamples(JsonNode? node, string label)
    {
        if (node is not JsonArray array)
            throw new InvalidDataException($"Broadcast {label} payload must be a JSON array.");
        return array.Select((item, index) =>
            BroadcastSampleCodec.FromProtocol(item, $"{label}[{index}]")
            ?? throw new InvalidDataException($"Broadcast {label}[{index}] must not be null.")).ToList();
    }

    private static IReadOnlyDictionary<string, Sample> ParseSampleDictionary(JsonNode? node)
    {
        if (node is not JsonObject obj)
            throw new InvalidDataException("Broadcast sample dictionary payload must be a JSON object.");
        var result = new Dictionary<string, Sample>(StringComparer.Ordinal);
        foreach (var pair in obj)
            result[pair.Key] = BroadcastSampleCodec.FromProtocol(pair.Value, pair.Key)
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
            array.Add(BroadcastSampleCodec.ToProtocol(sample, "sample"));
        return array;
    }

    private static JsonObject? ToProtocolDictionary(IReadOnlyDictionary<string, Sample>? samples)
    {
        if (samples is null)
            return null;
        var obj = new JsonObject();
        foreach (var pair in samples)
            obj[pair.Key] = BroadcastSampleCodec.ToProtocol(pair.Value, pair.Key);
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
