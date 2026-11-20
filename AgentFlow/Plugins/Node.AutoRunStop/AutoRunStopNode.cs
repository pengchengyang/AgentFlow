// -----------------------------------------------------------------------
// <copyright company="Your Company" file="AutoRunStopNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using AgentFlow.Shared.RTS.CFG.Broadcasts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.AutoRunStop;

/// <summary>
/// Handles workflow RUN and DISCONNECT controls. A valid RUN is forwarded through the
/// CFG launcher start handshake and produces a correlated RUN_ACK, while DISCONNECT publishes
/// the stop broadcast and requests a normal workflow stop from the host.
/// </summary>
public sealed class AutoRunStopNode : BaseNode
{
    private const string RunType = "RUN";
    private const string RunAckType = "RUN_ACK";
    private const string StopRequestFragment = "{\"Request\":\"Stop\"}";
    private const string DisconnectMessageType = "DISCONNECT";
    private const string RunReplyTimeoutParameterName = "RunReplyTimeoutMilliseconds";
    private const int DefaultRunReplyTimeoutMilliseconds = 60000;

    private readonly BasePin _sampleInputPin =
        new("input_string_sample", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _sampleOutputPin =
        new("output_string_sample", DataType.STRING, PinDirection.Output);

    private readonly object _runHandshakeGate = new();

    private INodeContext? _context;
    private ILogger? _logger;
    private ITopicBus? _topicBus;
    private ITopicSubscription? _launcherStartReplySubscription;
    private IWorkflowStopRequestSink? _stopRequestSink;
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _pendingRunTimeoutCts;
    private PendingRun? _pendingRun;
    private long _runAckSequence;

    public AutoRunStopNode()
    {
        Uuid = "C60BAA9B-C0AB-4817-8683-F1646BC863AE";
        AddParam();
        AddPins();
    }

    public override string Name => "Auto Run Stop";
    public override string DisplayName { get; set; } = "Auto Run Stop";
    public override string Category => "C";

    public override void AddPins()
    {
        AddInputPin(_sampleInputPin);
        AddOutputPin(_sampleOutputPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            RunReplyTimeoutParameterName, typeof(int), DefaultRunReplyTimeoutMilliseconds,
            isEditable: true, group: "General", zone: "AutoRunStopZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue(RunReplyTimeoutParameterName, out var timeout))
        {
            Param(RunReplyTimeoutParameterName).Value =
                ParameterValues.GetInt(timeout, DefaultRunReplyTimeoutMilliseconds);
        }
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;
        _topicBus = ResolveTopicBus(context);
        _stopRequestSink = context.GetService<IWorkflowStopRequestSink>();

        CancelPendingRun();
        DisposeSubscriptions();
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = new CancellationTokenSource();

        _launcherStartReplySubscription = _topicBus.Subscribe(
            CFGBroadcastSubscriptions.CFGLauncherStartReply,
            OnLauncherStartReplyBroadcastAsync);

        _logger?.LogTrace(
            "Auto Run Stop subscribed to launcher start replies on {ReplyTopic} during initialization.",
            CFGBroadcastSubscriptions.CFGLauncherStartReply);
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;
        _topicBus = ResolveTopicBus(context);
        _stopRequestSink = context.GetService<IWorkflowStopRequestSink>();
        Running = true;
        _logger?.LogTrace(
            "Auto Run Stop is waiting for RUN or DISCONNECT JSON objects on {Pin}.",
            _sampleInputPin.Name);
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        CancelPendingRun();
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        DisposeSubscriptions();
        _topicBus = null;
        _stopRequestSink = null;
        _logger = null;
        _context = null;
        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _sampleInputPin))
        {
            if (!Running)
                return;

            HandleSampleInput(value);
        }
    }

    private void HandleSampleInput(Sample? value)
    {
        if (value is not StringSample input ||
            input.StringValue is not { } inputText ||
            string.IsNullOrWhiteSpace(inputText))
        {
            _logger?.LogWarning(
                "Auto Run Stop ignored invalid input on {Pin}: expected a non-empty StringSample.",
                _sampleInputPin.Name);
            return;
        }

        if (!TryParseInput(inputText, out var root, out var parseError))
        {
            _logger?.LogWarning(
                "Auto Run Stop ignored input on {Pin} because it could not be parsed as a JSON object: {Error}",
                _sampleInputPin.Name,
                parseError);
            return;
        }

        using (root)
        {
            if (!TryGetRequiredStringProperty(root.RootElement, "TYPE", out var messageType))
            {
                _logger?.LogWarning(
                    "Auto Run Stop ignored input on {Pin} because TYPE is missing or not a non-empty string.",
                    _sampleInputPin.Name);
                return;
            }

            if (string.Equals(messageType, DisconnectMessageType, StringComparison.Ordinal))
            {
                _logger?.LogInformation(
                    "Auto Run Stop received DISCONNECT message on {Pin}: {Message}",
                    _sampleInputPin.Name,
                    inputText);
                _ = BroadcastStopRequestAsync(inputText);
                return;
            }

            if (string.Equals(messageType, RunType, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadRun(root.RootElement, out var pendingRun, out var runError))
                {
                    _logger?.LogError(
                        "Auto Run Stop rejected RUN input: {Error}. Payload: {Payload}",
                        runError,
                        inputText);
                    return;
                }

                StartRunHandshake(pendingRun);
                return;
            }

            _logger?.LogWarning(
                "Auto Run Stop ignored input on {Pin} because TYPE {Type} is not RUN or DISCONNECT.",
                _sampleInputPin.Name,
                messageType);
        }
    }

    private void StartRunHandshake(PendingRun pendingRun)
    {
        CancellationTokenSource? previousTimeoutCts = null;
        CancellationTokenSource timeoutCts;
        lock (_runHandshakeGate)
        {
            if (_pendingRun is { } existing)
            {
                previousTimeoutCts = _pendingRunTimeoutCts;
                _logger?.LogWarning(
                    "Auto Run Stop replaced pending RUN GUID {PreviousGuid}, LOCATION {PreviousLocation}, RUN_SEQ {PreviousSequence} with GUID {Guid}, LOCATION {Location}, RUN_SEQ {Sequence}.",
                    existing.Guid,
                    existing.Location,
                    existing.Sequence,
                    pendingRun.Guid,
                    pendingRun.Location,
                    pendingRun.Sequence);
            }

            var lifetimeToken = _lifetimeCts?.Token ?? CancellationToken.None;
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            _pendingRun = pendingRun;
            _pendingRunTimeoutCts = timeoutCts;
        }

        previousTimeoutCts?.Cancel();

        var timeoutMilliseconds = GetRunReplyTimeoutMilliseconds();
        _ = WaitForRunReplyTimeoutAsync(pendingRun, timeoutCts, timeoutMilliseconds);
        _ = PublishRunRequestAsync(pendingRun, timeoutCts.Token);
    }

    private async Task PublishRunRequestAsync(PendingRun pendingRun, CancellationToken cancellationToken)
    {
        try
        {
            var topicBus = _topicBus;
            if (topicBus is null)
                throw new InvalidOperationException("Auto Run Stop topic bus is not initialized.");

            var sample = StringSample.CreateSample();
            sample.StringValue = CFGLauncherStartSignals.CFGRequest;
            await topicBus.PublishAsync(
                CFGBroadcastSubscriptions.CFGLauncherStart,
                BroadcastTopicMessage.FromSample(sample),
                ct: cancellationToken).ConfigureAwait(false);

            _logger?.LogInformation(
                "Auto Run Stop broadcast launcher request {Signal} for GUID {Guid}, LOCATION {Location}, RUN_SEQ {RunSequence} on {Topic}.",
                CFGLauncherStartSignals.CFGRequest,
                pendingRun.Guid,
                pendingRun.Location,
                pendingRun.Sequence,
                CFGBroadcastSubscriptions.CFGLauncherStart);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogTrace(
                "Auto Run Stop launcher request for GUID {Guid}, LOCATION {Location}, RUN_SEQ {RunSequence} was cancelled.",
                pendingRun.Guid,
                pendingRun.Location,
                pendingRun.Sequence);
        }
        catch (Exception ex)
        {
            ClearPendingRun(pendingRun);
            _logger?.LogError(
                ex,
                "Auto Run Stop failed to broadcast launcher request {Signal} for GUID {Guid}, LOCATION {Location}, RUN_SEQ {RunSequence}: {Message}",
                CFGLauncherStartSignals.CFGRequest,
                pendingRun.Guid,
                pendingRun.Location,
                pendingRun.Sequence,
                ex.Message);
        }
    }

    private Task OnLauncherStartReplyBroadcastAsync(TopicMessage message)
    {
        try
        {
            if (!string.Equals(
                    message.Topic,
                    CFGBroadcastSubscriptions.CFGLauncherStartReply,
                    StringComparison.Ordinal))
            {
                _logger?.LogWarning(
                    "Auto Run Stop ignored launcher reply from unexpected topic {Topic}.",
                    message.Topic);
                return Task.CompletedTask;
            }

            if (message.Payload is not BroadcastTopicMessage
                {
                    Sample: StringSample { StringValue: { } payload }
                })
            {
                _logger?.LogWarning(
                    "Auto Run Stop ignored launcher start reply because the payload did not contain a StringSample with a non-null value.");
                return Task.CompletedTask;
            }

            if (!string.Equals(payload, CFGLauncherStartSignals.CFGStarted, StringComparison.Ordinal))
            {
                _logger?.LogTrace(
                    "Auto Run Stop ignored launcher start reply value {Payload}; expected {Expected}.",
                    payload,
                    CFGLauncherStartSignals.CFGStarted);
                return Task.CompletedTask;
            }

            PendingRun? pendingRun;
            CancellationTokenSource? timeoutCts;
            lock (_runHandshakeGate)
            {
                pendingRun = _pendingRun;
                timeoutCts = _pendingRunTimeoutCts;
                if (pendingRun is null)
                {
                    _pendingRunTimeoutCts = null;
                }
                else
                {
                    _pendingRun = null;
                    _pendingRunTimeoutCts = null;
                }
            }

            if (pendingRun is null)
            {
                _logger?.LogWarning(
                    "Auto Run Stop received launcher start reply {Payload}, but no RUN request is pending.",
                    payload);
                return Task.CompletedTask;
            }

            timeoutCts?.Cancel();

            var acknowledgementJson = CreateRunAck(pendingRun);
            var sample = StringSample.CreateSample();
            sample.StringValue = acknowledgementJson;
            var sent = _sampleOutputPin.SendSampleWithDispatchLog(
                sample,
                $"RUN_ACK for GUID {pendingRun.Guid}, LOCATION {pendingRun.Location}, RUN_SEQ {pendingRun.Sequence}",
                LogLevel.Information);

            if (!sent)
            {
                _logger?.LogError(
                    "Auto Run Stop could not emit RUN_ACK for GUID {Guid}, LOCATION {Location}, RUN_SEQ {RunSequence}; output_string_sample is not connected.",
                    pendingRun.Guid,
                    pendingRun.Location,
                    pendingRun.Sequence);
                return Task.CompletedTask;
            }

            _logger?.LogInformation(
                "Auto Run Stop emitted RUN_ACK after launcher reply {Payload} for GUID {Guid}, LOCATION {Location}, RUN_SEQ {RunSequence}: {Json}",
                payload,
                pendingRun.Guid,
                pendingRun.Location,
                pendingRun.Sequence,
                acknowledgementJson);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Auto Run Stop failed to process launcher start reply on {Topic}: {Message}",
                message.Topic,
                ex.Message);
        }

        return Task.CompletedTask;
    }

    private async Task BroadcastStopRequestAsync(string triggerMessage)
    {
        var topicBus = _topicBus ?? TopicBus.Instance;
        var stopRequestSink = _stopRequestSink;
        try
        {
            var sample = StringSample.CreateSample();
            sample.SetValue(StopRequestFragment);

            await topicBus.PublishAsync(
                CFGBroadcastSubscriptions.CFGAutoStopExit,
                BroadcastTopicMessage.FromSample(sample),
                ct: CancellationToken.None).ConfigureAwait(false);

            _logger?.LogTrace(
                "Auto Run Stop broadcast the stop request on {Topic}.",
                CFGBroadcastSubscriptions.CFGAutoStopExit);

            if (stopRequestSink is not null)
            {
                if (!stopRequestSink.RequestStop(Name, triggerMessage))
                    _logger?.LogWarning("Auto Run Stop stop request was not accepted by the current workflow host.");
            }
            else
            {
                _logger?.LogWarning(
                    "Auto Run Stop has no workflow stop sink in this host; only the stop broadcast was sent.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Auto Run Stop failed to broadcast the stop request on {Topic}.",
                CFGBroadcastSubscriptions.CFGAutoStopExit);
        }
    }

    private async Task WaitForRunReplyTimeoutAsync(
        PendingRun pendingRun,
        CancellationTokenSource timeoutCts,
        int timeoutMilliseconds)
    {
        try
        {
            await Task.Delay(timeoutMilliseconds, timeoutCts.Token).ConfigureAwait(false);

            var expired = false;
            lock (_runHandshakeGate)
            {
                if (ReferenceEquals(_pendingRun, pendingRun))
                {
                    _pendingRun = null;
                    _pendingRunTimeoutCts = null;
                    expired = true;
                }
            }

            if (expired)
            {
                _logger?.LogError(
                    "Auto Run Stop timed out after {TimeoutMilliseconds} ms waiting for launcher start reply {Signal} for GUID {Guid}, LOCATION {Location}, RUN_SEQ {RunSequence}.",
                    timeoutMilliseconds,
                    CFGLauncherStartSignals.CFGStarted,
                    pendingRun.Guid,
                    pendingRun.Location,
                    pendingRun.Sequence);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Auto Run Stop failed while waiting for launcher start reply for GUID {Guid}, LOCATION {Location}, RUN_SEQ {RunSequence}: {Message}",
                pendingRun.Guid,
                pendingRun.Location,
                pendingRun.Sequence,
                ex.Message);
        }
        finally
        {
            timeoutCts.Dispose();
        }
    }

    private string CreateRunAck(PendingRun pendingRun)
    {
        var acknowledgement = new JsonObject
        {
            ["GUID"] = pendingRun.Guid,
            ["LOCATION"] = pendingRun.Location,
            ["TYPE"] = RunAckType,
            ["VERSION"] = pendingRun.Version,
            ["SEQ"] = Interlocked.Increment(ref _runAckSequence),
            ["TIMESTAMP"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["DATA"] = new JsonObject
            {
                ["RESULT"] = "OK",
                ["STATE"] = "RUNNING",
                ["RUN_SEQ"] = pendingRun.Sequence,
            },
        };

        return JsonSerializer.Serialize(acknowledgement);
    }

    private void CancelPendingRun()
    {
        CancellationTokenSource? timeoutCts = null;
        lock (_runHandshakeGate)
        {
            timeoutCts = _pendingRunTimeoutCts;
            _pendingRun = null;
            _pendingRunTimeoutCts = null;
        }

        timeoutCts?.Cancel();
    }

    private void ClearPendingRun(PendingRun pendingRun)
    {
        CancellationTokenSource? timeoutCts = null;
        lock (_runHandshakeGate)
        {
            if (!ReferenceEquals(_pendingRun, pendingRun))
                return;

            _pendingRun = null;
            timeoutCts = _pendingRunTimeoutCts;
            _pendingRunTimeoutCts = null;
        }

        timeoutCts?.Cancel();
    }

    private int GetRunReplyTimeoutMilliseconds()
    {
        var timeoutMilliseconds = ParameterValues.GetInt(
            Param(RunReplyTimeoutParameterName).Value,
            DefaultRunReplyTimeoutMilliseconds);
        if (timeoutMilliseconds > 0)
            return timeoutMilliseconds;

        _logger?.LogWarning(
            "Auto Run Stop {Parameter}={Value} is invalid; using {Fallback} ms.",
            RunReplyTimeoutParameterName,
            timeoutMilliseconds,
            DefaultRunReplyTimeoutMilliseconds);
        return DefaultRunReplyTimeoutMilliseconds;
    }

    private static bool TryReadRun(
        JsonElement root,
        out PendingRun pendingRun,
        out string? error)
    {
        pendingRun = default!;
        error = null;

        if (!TryGetRequiredStringProperty(root, "GUID", out var guidText) ||
            !Guid.TryParse(guidText, out var guid))
        {
            error = "GUID must be a valid GUID";
            return false;
        }

        if (!TryGetRequiredStringProperty(root, "LOCATION", out var location))
        {
            error = "LOCATION is required";
            return false;
        }

        if (!TryGetRequiredStringProperty(root, "VERSION", out var version))
        {
            error = "VERSION is required";
            return false;
        }

        if (!TryGetInt64Property(root, "SEQ", out var sequence) || sequence <= 0)
        {
            error = "SEQ must be a positive integer";
            return false;
        }

        if (!TryGetPropertyIgnoreCase(root, "DATA", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            error = "DATA must be a JSON object";
            return false;
        }

        pendingRun = new PendingRun(
            guid.ToString("D").ToLowerInvariant(),
            location.Trim(),
            version.Trim(),
            sequence);
        return true;
    }

    private static bool TryParseInput(
        string inputText,
        out JsonDocument document,
        out string? error)
    {
        document = default!;
        error = null;
        var jsonText = inputText.Trim().Trim(';').Trim();
        try
        {
            var parsed = JsonDocument.Parse(jsonText);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                error = "The input root must be a JSON object.";
                return false;
            }

            document = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryGetRequiredStringProperty(
        JsonElement element,
        string key,
        out string value)
    {
        if (TryGetPropertyIgnoreCase(element, key, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()?.Trim() ?? string.Empty;
            if (value.Length > 0)
                return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt64Property(
        JsonElement element,
        string key,
        out long value)
    {
        value = 0;
        if (!TryGetPropertyIgnoreCase(element, key, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value))
            return true;

        return property.ValueKind == JsonValueKind.String &&
               long.TryParse(
                   property.GetString(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static ITopicBus ResolveTopicBus(INodeContext context) =>
        context.GetService<ITopicBus>() ?? TopicBus.Instance;

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    private void DisposeSubscriptions()
    {
        _launcherStartReplySubscription?.Dispose();
        _launcherStartReplySubscription = null;
    }

    /// <summary>Final application-exit hook; runtime resources were already released by Stop.</summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Stop(context, ct);

    private sealed record PendingRun(
        string Guid,
        string Location,
        string Version,
        long Sequence);
}
