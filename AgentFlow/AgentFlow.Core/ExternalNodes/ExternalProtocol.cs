// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalProtocol.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>Small helpers for the NDJSON protocol used by multilingual Workers.</summary>
public static class ExternalProtocol
{
    public const int ProtocolVersion = 1;
    public const int DefaultMaxLineBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static JsonObject Create(string type, string? messageId = null)
    {
        var message = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["type"] = type
        };
        if (!string.IsNullOrEmpty(messageId))
            message["messageId"] = messageId;
        return message;
    }

    public static string Serialize(JsonObject message) => message.ToJsonString(JsonOptions);

    public static JsonObject? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? GetString(JsonObject message, string name) =>
        message[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static int? GetInt32(JsonObject message, string name)
    {
        if (message[name] is not JsonValue value)
            return null;
        if (value.TryGetValue<int>(out var number))
            return number;
        if (value.TryGetValue<long>(out var longNumber))
            return checked((int)longNumber);
        if (value.TryGetValue<string>(out var text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    public static bool? GetBool(JsonObject message, string name)
    {
        if (message[name] is not JsonValue value)
            return null;
        if (value.TryGetValue<bool>(out var boolean))
            return boolean;
        if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed))
            return parsed;
        return null;
    }

    public static JsonObject? GetObject(JsonObject message, string name) => message[name] as JsonObject;

    public static JsonArray? GetArray(JsonObject message, string name) => message[name] as JsonArray;
}
