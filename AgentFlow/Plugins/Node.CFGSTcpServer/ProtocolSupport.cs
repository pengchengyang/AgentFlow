// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ProtocolSupport.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Node.CFGTcpProtocol;

internal static class ProtocolConstants
{
    public const string Auth = "AUTH";
    public const string AuthOk = "AUTH_OK";
    public const string AuthFail = "AUTH_FAIL";
    public const string Run = "RUN";
    public const string RunAck = "RUN_ACK";
    public const string Business = "BUSINESS";
    public const string Ping = "PING";
    public const string Pong = "PONG";
    public const string Disconnect = "DISCONNECT";

    public const string Guid = "GUID";
    public const string Location = "LOCATION";
    public const string Type = "TYPE";
    public const string Version = "VERSION";
    public const string Sequence = "SEQ";
    public const string Timestamp = "TIMESTAMP";
    public const string Data = "DATA";

    public const string Cfg1 = "CFG-1";
    public const string Cfg2 = "CFG-2";
    public const string Idle = "IDLE";
    public const string Test = "TEST";
}

internal static class ProtocolMessage
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryParseObject(string? json, out JsonObject envelope, out string? error)
    {
        envelope = new JsonObject();
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "message is empty";
            return false;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed)
            {
                error = "message must be a JSON object";
                return false;
            }

            envelope = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return false;
        }
    }

    public static bool TryValidateEnvelope(
        JsonObject envelope,
        out string? error,
        bool requireLocation = false)
    {
        error = null;
        if (!TryGetString(envelope, ProtocolConstants.Guid, out var guid) || string.IsNullOrWhiteSpace(guid))
        {
            error = "GUID is required";
            return false;
        }

        if (TryGetString(envelope, ProtocolConstants.Location, out var location) &&
            !string.IsNullOrWhiteSpace(location) &&
            !IsLocation(location))
        {
            error = "LOCATION must be CFG-1 or CFG-2";
            return false;
        }

        if (requireLocation &&
            (!TryGetString(envelope, ProtocolConstants.Location, out location) ||
             string.IsNullOrWhiteSpace(location)))
        {
            error = "LOCATION is required";
            return false;
        }

        if (!TryGetString(envelope, ProtocolConstants.Type, out var type) || string.IsNullOrWhiteSpace(type))
        {
            error = "TYPE is required";
            return false;
        }

        if (!TryGetString(envelope, ProtocolConstants.Version, out var version) || string.IsNullOrWhiteSpace(version))
        {
            error = "VERSION is required";
            return false;
        }

        if (!TryGetInt64(envelope, ProtocolConstants.Sequence, out var sequence) || sequence <= 0)
        {
            error = "SEQ must be a positive integer";
            return false;
        }

        if (!TryGetString(envelope, ProtocolConstants.Timestamp, out var timestamp) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            !DateTimeOffset.TryParse(timestamp, out _))
        {
            error = "TIMESTAMP must be a valid timestamp";
            return false;
        }

        if (Get(envelope, ProtocolConstants.Data) is not JsonObject)
        {
            error = "DATA must be a JSON object";
            return false;
        }

        return true;
    }

    public static bool TryParseClientConfig(
        string? json,
        out CfgClientConfig config,
        out string? error)
    {
        config = new CfgClientConfig
        {
            Clients = new Dictionary<string, CfgClientDefinition>(StringComparer.Ordinal),
        };
        error = null;

        if (!TryParseObject(json, out var root, out error))
            return false;

        var definitions = new Dictionary<string, CfgClientDefinition>(StringComparer.Ordinal);
        if (!TryAddConfiguredClient(root, ProtocolConstants.Cfg1, definitions, out error) ||
            !TryAddConfiguredClient(root, ProtocolConstants.Cfg2, definitions, out error))
        {
            return false;
        }

        if (definitions.Count == 0)
        {
            error = "configuration must contain GUID.CFG-1 or GUID.CFG-2";
            return false;
        }

        int? port = null;
        if (!TryGetIniValues(root, "PORT.LISTEN", out var portValues, out var portFound, out error))
            return false;

        if (portFound)
        {
            var firstPort = portValues.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (firstPort is null || !TryParsePort(firstPort, out var parsedPort))
            {
                error = "PORT.LISTEN must contain a valid integer port from 1 through 65535";
                return false;
            }

            port = parsedPort;
        }

        config = new CfgClientConfig { Clients = definitions, Port = port };
        return true;
    }

    private static bool TryAddConfiguredClient(
        JsonObject root,
        string location,
        Dictionary<string, CfgClientDefinition> definitions,
        out string? error)
    {
        var key = $"GUID.{location}";
        if (!TryGetIniValues(root, key, out var values, out var found, out error))
            return false;

        if (!found)
            return true;

        var guidText = values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (guidText is null || !IsGuid(guidText, out var normalizedGuid))
        {
            error = $"{key} must contain a valid GUID";
            return false;
        }

        if (!definitions.TryAdd(
                normalizedGuid,
                new CfgClientDefinition(normalizedGuid, NormalizeLocation(location))))
        {
            error = $"duplicate client GUID '{normalizedGuid}'";
            return false;
        }

        return true;
    }

    private static bool TryGetIniValues(
        JsonObject root,
        string key,
        out IReadOnlyList<string> values,
        out bool found,
        out string? error)
    {
        values = Array.Empty<string>();
        found = false;
        error = null;

        var node = Get(root, key);
        if (node is null)
            return true;

        found = true;
        if (node is not JsonArray array)
        {
            error = $"{key} must be a JSON array produced by Node.ReadIniFile";
            return false;
        }

        var parsed = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                text is null)
            {
                error = $"{key} must contain only strings";
                return false;
            }

            parsed.Add(text);
        }

        values = parsed;
        return true;
    }

    public static bool TryParsePort(string? value, out int port)
    {
        port = 0;
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return int.TryParse(text, out port) && port is >= 1 and <= 65535;
    }

    public static JsonObject CreateEnvelope(
        string guid,
        string location,
        string type,
        string version,
        long sequence,
        string timestamp,
        JsonObject? data = null)
    {
        var envelope = new JsonObject
        {
            [ProtocolConstants.Guid] = guid,
        };

        if (!string.IsNullOrWhiteSpace(location))
            envelope[ProtocolConstants.Location] = NormalizeLocation(location);

        envelope[ProtocolConstants.Type] = type;
        envelope[ProtocolConstants.Version] = version;
        envelope[ProtocolConstants.Sequence] = sequence;
        envelope[ProtocolConstants.Timestamp] = timestamp;
        envelope[ProtocolConstants.Data] = data?.DeepClone() as JsonObject ?? new JsonObject();
        return envelope;
    }

    public static JsonObject CreateBusinessTemplate(JsonObject input, out string? error)
    {
        error = null;
        if (!TryGetString(input, ProtocolConstants.Type, out var type) ||
            !string.Equals(type, ProtocolConstants.Business, StringComparison.OrdinalIgnoreCase))
        {
            error = "TYPE must be BUSINESS";
            return new JsonObject();
        }

        if (!TryGetString(input, ProtocolConstants.Guid, out var guid) || string.IsNullOrWhiteSpace(guid))
        {
            error = "GUID is required";
            return new JsonObject();
        }

        if (Get(input, ProtocolConstants.Data) is not JsonObject data)
        {
            error = "DATA must be a JSON object";
            return new JsonObject();
        }

        return new JsonObject
        {
            [ProtocolConstants.Guid] = NormalizeGuidOrOriginal(guid),
            [ProtocolConstants.Type] = ProtocolConstants.Business,
            [ProtocolConstants.Data] = data.DeepClone(),
        };
    }

    public static bool TryCreateBusinessTemplate(
        JsonObject input,
        out JsonObject data,
        out string? error)
    {
        data = new JsonObject();
        error = null;
        if (!TryGetString(input, ProtocolConstants.Type, out var type) ||
            !string.Equals(type, ProtocolConstants.Business, StringComparison.OrdinalIgnoreCase))
        {
            error = "TYPE must be BUSINESS";
            return false;
        }

        if (Get(input, ProtocolConstants.Data) is not JsonObject sourceData)
        {
            error = "DATA must be a JSON object";
            return false;
        }

        data = sourceData.DeepClone().AsObject();
        return true;
    }

    public static bool TryValidateBusinessData(
        JsonObject data,
        string location,
        out string? error)
    {
        error = null;
        if (!IsLocation(location))
        {
            error = "LOCATION must be CFG-1 or CFG-2";
            return false;
        }

        var normalizedLocation = NormalizeLocation(location);
        var expectedStartSlot = string.Equals(normalizedLocation, ProtocolConstants.Cfg1, StringComparison.Ordinal)
            ? 1
            : 6;
        var expectedEndSlot = expectedStartSlot + 4;

        if (Get(data, "MESSAGES") is not JsonArray messages ||
            messages.Count < 1 ||
            messages.Count > 5)
        {
            error = "DATA.MESSAGES must contain between one and five records";
            return false;
        }

        var seenSlots = new bool[5];
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not JsonObject message)
            {
                error = $"DATA.MESSAGES[{index}] must be a JSON object";
                return false;
            }

            if (!TryGetString(message, "SLOT", out var slotText) ||
                !int.TryParse(slotText, out var slot) ||
                slot < expectedStartSlot ||
                slot > expectedEndSlot)
            {
                error = $"DATA.MESSAGES[{index}].SLOT must be an integer from {expectedStartSlot} through {expectedEndSlot}";
                return false;
            }

            var slotIndex = slot - expectedStartSlot;
            if (seenSlots[slotIndex])
            {
                error = $"DATA.MESSAGES contains duplicate SLOT {slot}";
                return false;
            }
            seenSlots[slotIndex] = true;

            if (!TryGetString(message, "FSN", out var fsn))
            {
                error = $"DATA.MESSAGES[{index}].FSN must be a string";
                return false;
            }

            if (!TryGetString(message, "IMEI", out var imei))
            {
                error = $"DATA.MESSAGES[{index}].IMEI must be a string";
                return false;
            }

            if (string.IsNullOrEmpty(fsn) != string.IsNullOrEmpty(imei))
            {
                error = $"DATA.MESSAGES[{index}] must set both FSN and IMEI or leave both empty";
                return false;
            }

            if (!TryGetString(message, "STATUS", out var statusText) ||
                !(IsLaunchStatus(statusText) || IsResultCode(statusText)))
            {
                error = $"DATA.MESSAGES[{index}].STATUS must be IDLE, TEST, or a non-negative integer";
                return false;
            }

            var status = statusText.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(fsn) &&
                !string.Equals(status, ProtocolConstants.Idle, StringComparison.Ordinal))
            {
                error = $"DATA.MESSAGES[{index}] with empty FSN/IMEI must use STATUS IDLE";
                return false;
            }
        }

        return true;
    }

    public static bool IsLocation(string value) =>
        string.Equals(NormalizeLocation(value), ProtocolConstants.Cfg1, StringComparison.Ordinal) ||
        string.Equals(NormalizeLocation(value), ProtocolConstants.Cfg2, StringComparison.Ordinal);

    public static string NormalizeLocation(string value) =>
        value.Trim().ToUpperInvariant();

    public static bool TryGetGuid(JsonObject obj, string key, out string normalizedGuid)
    {
        normalizedGuid = string.Empty;
        return TryGetString(obj, key, out var text) && IsGuid(text, out normalizedGuid);
    }
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    public static string SerializeFrame(JsonObject envelope)
    {
        var json = JsonSerializer.Serialize(envelope);
        json = json.Replace(";", "\\u003B", StringComparison.Ordinal);
        return $";{json};";
    }

    public static JsonNode? Get(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var exact))
            return exact;

        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    // Launch batches (CFGS to CFG) use IDLE/TEST. Result frames (CFG to CFGS) carry the decimal
    // RET code, where 0 is a pass and any positive value is a failure.
    private static bool IsLaunchStatus(string status)
    {
        var normalized = status.Trim().ToUpperInvariant();
        return string.Equals(normalized, ProtocolConstants.Idle, StringComparison.Ordinal) ||
               string.Equals(normalized, ProtocolConstants.Test, StringComparison.Ordinal);
    }

    private static bool IsResultCode(string status) =>
        long.TryParse(
            status.Trim(),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);

    public static bool TryGetString(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (Get(obj, key) is JsonValue node &&
            node.TryGetValue<string>(out var text) &&
            text is not null)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static bool TryGetInt64(JsonObject obj, string key, out long value)
    {
        value = 0;
        if (Get(obj, key) is not JsonValue node)
            return false;
        if (node.TryGetValue<long>(out var longValue))
        {
            value = longValue;
            return true;
        }
        if (node.TryGetValue<int>(out var intValue))
        {
            value = intValue;
            return true;
        }
        return node.TryGetValue<string>(out var text) && long.TryParse(text, out value);
    }

    public static string NormalizeGuidOrOriginal(string value)
    {
        return Guid.TryParse(value, out var parsed)
            ? parsed.ToString("D").ToLowerInvariant()
            : value.Trim();
    }

    public static bool IsGuid(string value, out string normalized)
    {
        normalized = string.Empty;
        if (!Guid.TryParse(value, out var parsed))
            return false;
        normalized = parsed.ToString("D").ToLowerInvariant();
        return true;
    }

    public static byte[] EncodeFrame(JsonObject envelope) => StrictUtf8.GetBytes(SerializeFrame(envelope));

    public static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string text, out string? error)
    {
        text = string.Empty;
        error = null;
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException ex)
        {
            error = $"invalid UTF-8 payload: {ex.Message}";
            return false;
        }
    }
}

internal sealed class CfgFrameAccumulator
{
    private readonly List<byte> _buffer = new();

    public bool TryAppend(ReadOnlySpan<byte> bytes, int maxFrameBytes, out List<string> frames, out string? error)
    {
        frames = new List<string>();
        error = null;

        foreach (var value in bytes)
            _buffer.Add(value);

        while (_buffer.Count > 0)
        {
            if (_buffer[0] != (byte)';')
            {
                error = "protocol frame must begin with ';'";
                _buffer.Clear();
                return false;
            }

            var endIndex = -1;
            for (var index = 1; index < _buffer.Count; index++)
            {
                if (_buffer[index] == (byte)';')
                {
                    endIndex = index;
                    break;
                }
            }

            if (endIndex < 0)
            {
                if (_buffer.Count > maxFrameBytes)
                {
                    error = $"protocol frame exceeded {maxFrameBytes} bytes before the terminating ';'";
                    _buffer.Clear();
                    return false;
                }

                break;
            }

            if (endIndex + 1 > maxFrameBytes)
            {
                error = $"protocol frame exceeded {maxFrameBytes} bytes";
                _buffer.Clear();
                return false;
            }

            if (endIndex <= 1)
            {
                error = "protocol frame payload is empty";
                _buffer.Clear();
                return false;
            }

            var payload = _buffer.GetRange(1, endIndex - 1).ToArray();
            _buffer.RemoveRange(0, endIndex + 1);

            if (!ProtocolMessage.TryDecodeUtf8(payload, out var text, out error))
            {
                _buffer.Clear();
                return false;
            }

            frames.Add(text);
        }

        return true;
    }
}

internal sealed record CfgClientDefinition(string Guid, string Location);

internal sealed class CfgClientConfig
{
    public required IReadOnlyDictionary<string, CfgClientDefinition> Clients { get; init; }

    public int? Port { get; init; }

    public int Count => Clients.Count;
}



