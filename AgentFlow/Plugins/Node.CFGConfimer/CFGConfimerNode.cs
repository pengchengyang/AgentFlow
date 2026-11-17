// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CFGConfimerNode.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.CFGConfimer;

/// <summary>
/// Confirms that a complete CFG protocol envelope targets this local GUID and extracts the
/// opaque top-level DATA object as a standalone JSON string.
/// </summary>
public sealed class CFGConfimerNode : BaseNode
{
    private const string DefaultRequiredType = "BUSINESS";

    private readonly object _stateGate = new();
    private ILogger? _logger;
    private string _localGuid = string.Empty;
    private string _requiredType = DefaultRequiredType;
    private DateTimeOffset? _lastTimestampUtc;

    public CFGConfimerNode()
    {
        Uuid = "90C3A453-044E-45BB-82B4-90499E07693D";
        AddParam();
        AddPins();
    }

    public override string Name => "CFG Confimer";
    public override string DisplayName { get; set; } = "CFG Confimer";
    public override string Category => "Protocol";

    public BasePin LocalGuidInputPin { get; } =
        new("input_string_local_guid", DataType.STRING, PinDirection.Input, required: false);

    public BasePin MessageInputPin { get; } =
        new("input_string_message", DataType.STRING, PinDirection.Input, required: false);

    public BasePin DataOutputPin { get; } =
        new("output_string_data", DataType.STRING, PinDirection.Output);

    public override void AddPins()
    {
        AddInputPin(LocalGuidInputPin);
        AddInputPin(MessageInputPin);
        AddOutputPin(DataOutputPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter("RequiredType", typeof(string), DefaultRequiredType,
            isEditable: true, group: "Protocol", zone: "CFGConfimerZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("RequiredType", out var requiredType) && requiredType is not null)
            Param("RequiredType").Value = ParameterValues.GetString(requiredType, DefaultRequiredType) ?? DefaultRequiredType;
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        var configuredType = (ParameterValues.GetString(Param("RequiredType").Value, DefaultRequiredType) ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(configuredType))
        {
            context.Logger.LogWarning(
                "CFG Confimer RequiredType is empty; using {Fallback}.",
                DefaultRequiredType);
            configuredType = DefaultRequiredType;
            Param("RequiredType").Value = configuredType;
        }

        lock (_stateGate)
        {
            _requiredType = configuredType.ToUpperInvariant();
            _localGuid = string.Empty;
            _lastTimestampUtc = null;
        }

        context.Logger.LogInformation(
            "CFG Confimer initialized; requiredType={RequiredType}, waiting for input_string_local_guid.",
            _requiredType);
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        context.Logger.LogInformation("CFG Confimer is running; requiredType={RequiredType}.", _requiredType);
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        lock (_stateGate)
            _lastTimestampUtc = null;
        SetIdle();
        context.Logger.LogInformation("CFG Confimer stopped.");
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default) =>
        Stop(context, ct);

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, LocalGuidInputPin))
        {
            HandleLocalGuid(value);
            return;
        }

        if (ReferenceEquals(pin, MessageInputPin))
            HandleMessage(value);
    }

    private void HandleLocalGuid(Sample? value)
    {
        if (value is not StringSample text || text.StringValue is null)
        {
            _logger?.LogError("CFG Confimer ignored input_string_local_guid: expected StringSample.");
            return;
        }

        if (!Guid.TryParse(text.StringValue.Trim(), out var parsedGuid))
        {
            _logger?.LogError(
                "CFG Confimer rejected local GUID {Guid}: invalid GUID format.",
                text.StringValue);
            return;
        }

        var normalized = parsedGuid.ToString("D").ToLowerInvariant();
        lock (_stateGate)
        {
            if (!string.Equals(_localGuid, normalized, StringComparison.Ordinal))
                _lastTimestampUtc = null;
            _localGuid = normalized;
        }

        _logger?.LogInformation("CFG Confimer accepted local GUID {Guid}.", normalized);
    }

    private void HandleMessage(Sample? value)
    {
        if (value is not StringSample text || text.StringValue is null)
        {
            _logger?.LogError("CFG Confimer ignored input_string_message: expected StringSample.");
            return;
        }

        string localGuid;
        string requiredType;
        lock (_stateGate)
        {
            localGuid = _localGuid;
            requiredType = _requiredType;
        }

        if (string.IsNullOrWhiteSpace(localGuid))
        {
            _logger?.LogWarning(
                "CFG Confimer ignored message because input_string_local_guid has not received a valid GUID yet.");
            return;
        }

        if (!TryParseObject(text.StringValue, out var envelope, out var error))
        {
            _logger?.LogError("CFG Confimer rejected message: {Error}.", error);
            return;
        }

        if (!TryGetString(envelope, "GUID", out var guidText) ||
            !Guid.TryParse(guidText.Trim(), out var targetGuid))
        {
            _logger?.LogError("CFG Confimer rejected message: GUID is missing or invalid.");
            return;
        }

        var targetGuidNormalized = targetGuid.ToString("D").ToLowerInvariant();
        if (!string.Equals(targetGuidNormalized, localGuid, StringComparison.Ordinal))
        {
            _logger?.LogWarning(
                "CFG Confimer ignored message for GUID {TargetGuid}; local GUID is {LocalGuid}.",
                targetGuidNormalized, localGuid);
            return;
        }

        if (!TryGetString(envelope, "TYPE", out var type) || string.IsNullOrWhiteSpace(type))
        {
            _logger?.LogError("CFG Confimer rejected message: TYPE is missing.");
            return;
        }

        if (!string.Equals(type.Trim(), requiredType, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning(
                "CFG Confimer ignored TYPE {Type}; required TYPE is {RequiredType}.",
                type, requiredType);
            return;
        }

        if (!TryValidateProtocolFields(envelope, out var data, out var timestampUtc, out error))
        {
            _logger?.LogError("CFG Confimer rejected message: {Error}.", error);
            return;
        }

        lock (_stateGate)
        {
            if (_lastTimestampUtc == timestampUtc)
            {
                _logger?.LogWarning(
                    "CFG Confimer ignored duplicate TIMESTAMP {Timestamp} for GUID {Guid}.",
                    timestampUtc,
                    localGuid);
                return;
            }

            var json = JsonSerializer.Serialize(data);
            var sample = StringSample.CreateSample();
            sample.SetValue(json, DataOutputPin.Name);
            var sent = DataOutputPin.SendSampleWithDispatchLog(
                sample,
                $"confirmed DATA for {localGuid}",
                LogLevel.Information);

            if (sent)
                _lastTimestampUtc = timestampUtc;
            else
                _logger?.LogWarning(
                    "CFG Confimer did not cache TIMESTAMP {Timestamp} because output_string_data was not connected.",
                    timestampUtc);
        }
    }

    private static bool TryParseObject(string json, out JsonObject envelope, out string? error)
    {
        envelope = new JsonObject();
        error = null;
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

    private static bool TryValidateProtocolFields(
        JsonObject envelope,
        out JsonObject data,
        out DateTimeOffset timestampUtc,
        out string? error)
    {
        data = new JsonObject();
        timestampUtc = default;
        error = null;

        if (!TryGetString(envelope, "TIMESTAMP", out var timestamp) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTimestamp))
        {
            error = "TIMESTAMP must be a valid timestamp";
            return false;
        }

        if (Get(envelope, "DATA") is not JsonObject parsedData)
        {
            error = "DATA must be a JSON object";
            return false;
        }

        timestampUtc = parsedTimestamp.ToUniversalTime();
        data = parsedData;
        return true;
    }

    private static JsonNode? Get(JsonObject obj, string key)
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

    private static bool TryGetString(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (Get(obj, key) is JsonValue node &&
            node.TryGetValue<string>(out var text) &&
            text is not null)
        {
            value = text;
            return true;
        }

        return false;
    }


    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);
}




