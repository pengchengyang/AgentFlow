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
    public const string Business = "BUSINESS";
    public const string Ping = "PING";
    public const string Pong = "PONG";
    public const string Disconnect = "DISCONNECT";

    public const string Guid = "GUID";
    public const string Type = "TYPE";
    public const string Version = "VERSION";
    public const string Sequence = "SEQ";
    public const string Timestamp = "TIMESTAMP";
    public const string Data = "DATA";
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

    public static bool TryValidateEnvelope(JsonObject envelope, out string? error)
    {
        error = null;
        if (!TryGetString(envelope, ProtocolConstants.Guid, out var guid) || string.IsNullOrWhiteSpace(guid))
        {
            error = "GUID is required";
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
        config = new CfgClientConfig { Clients = new Dictionary<string, CfgClientDefinition>(StringComparer.Ordinal) };
        error = null;

        if (!TryParseObject(json, out var root, out error))
            return false;

        var clientsNode = Get(root, "clients");
        if (clientsNode is not JsonArray clientsArray)
        {
            error = "clients must be a JSON array";
            return false;
        }

        var definitions = new Dictionary<string, CfgClientDefinition>(StringComparer.Ordinal);
        foreach (var item in clientsArray)
        {
            if (item is not JsonObject client)
            {
                error = "each clients entry must be a JSON object";
                return false;
            }

            if (!TryGetString(client, ProtocolConstants.Guid, out var guidText) ||
                !IsGuid(guidText, out var normalizedGuid))
            {
                error = "each clients entry must contain a valid GUID";
                return false;
            }

            if (!TryGetString(client, "ROLE", out var role) || string.IsNullOrWhiteSpace(role))
            {
                error = "each clients entry must contain a non-empty ROLE";
                return false;
            }

            if (!definitions.TryAdd(
                    normalizedGuid,
                    new CfgClientDefinition(normalizedGuid, role.Trim())))
            {
                error = $"duplicate client GUID '{normalizedGuid}'";
                return false;
            }
        }

        if (definitions.Count == 0)
        {
            error = "clients must contain at least one entry";
            return false;
        }

        config = new CfgClientConfig { Clients = definitions };
        return true;
    }
    public static JsonObject CreateEnvelope(
        string guid,
        string type,
        string version,
        long sequence,
        string timestamp,
        JsonObject? data = null)
    {
        return new JsonObject
        {
            [ProtocolConstants.Guid] = guid,
            [ProtocolConstants.Type] = type,
            [ProtocolConstants.Version] = version,
            [ProtocolConstants.Sequence] = sequence,
            [ProtocolConstants.Timestamp] = timestamp,
            [ProtocolConstants.Data] = data?.DeepClone() as JsonObject ?? new JsonObject(),
        };
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

internal sealed record CfgClientDefinition(string Guid, string Role);

internal sealed class CfgClientConfig
{
    public required IReadOnlyDictionary<string, CfgClientDefinition> Clients { get; init; }

    public int Count => Clients.Count;
}



