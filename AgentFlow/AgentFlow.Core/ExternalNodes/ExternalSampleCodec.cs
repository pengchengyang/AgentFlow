// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalSampleCodec.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>Maps AgentFlow Sample objects to and from the language-neutral protocol shape.</summary>
public static class ExternalSampleCodec
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
