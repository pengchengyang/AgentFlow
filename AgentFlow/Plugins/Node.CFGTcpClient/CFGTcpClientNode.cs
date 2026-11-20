// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CFGTcpClientNode.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentFlow.Contracts;
using AgentFlow.Shared.RTS.CFGS.Broadcasts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;
using Node.CFGTcpProtocol;
using TcpSocket = System.Net.Sockets.TcpClient;

namespace Node.CFGTcpClient;

/// <summary>
/// CFG TCP client session node. It waits for a runtime GUID on input_string_guid, authenticates
/// with that GUID, maintains a persistent reconnectable TCP session, sends PING heartbeats,
/// buffers business envelopes while offline, and transports complete JSON envelopes inside the
/// shared semicolon frame.
/// </summary>
public sealed class CFGTcpClientNode : BaseNode
{
    private const int DefaultServerPort = 9000;
    private const int DefaultConnectTimeoutSeconds = 5;
    private const int DefaultAuthTimeoutSeconds = 5;
    private const int DefaultAuthenticationTimeoutMilliseconds = 60000;
    private const int DefaultHeartbeatIntervalSeconds = 10;
    private const int DefaultHeartbeatTimeoutSeconds = 30;
    private const int DefaultReconnectDelaySeconds = 3;
    private const int DefaultMaxMessageBytes = 65536;
    private const int DefaultSendQueueCapacity = 256;
    private const int DefaultWriteTimeoutSeconds = 10;
    private const int DefaultReceiveBufferSize = 4096;
    private const string ImmediateConnectMode = "Immediate";
    private const string WaitForEndpointConnectMode = "WaitForEndpoint";
    private static readonly string[] ConnectModeOptions = { ImmediateConnectMode, WaitForEndpointConnectMode };

    private readonly object _endpointGate = new();
    private readonly object _supervisorStartGate = new();
    private readonly object _pendingBusinessInputGate = new();
    private readonly Queue<string> _pendingBusinessInputs = new();
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpSocket? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _connectionCts;
    private Task? _supervisorTask;
    private Channel<JsonObject>? _sendChannel;
    private ILogger? _logger;
    private ITopicBus? _topicBus;
    private INodeContext? _context;

    private string _clientGuid = string.Empty;
    private string _host = "127.0.0.1";
    private int _port = DefaultServerPort;
    private string _connectMode = ImmediateConnectMode;
    private string _protocolVersion = "1.0";
    private string _location = string.Empty;
    private int _connectTimeoutSeconds = DefaultConnectTimeoutSeconds;
    private int _authTimeoutSeconds = DefaultAuthTimeoutSeconds;
    private int _authenticationTimeoutMilliseconds = DefaultAuthenticationTimeoutMilliseconds;
    private int _heartbeatIntervalSeconds = DefaultHeartbeatIntervalSeconds;
    private int _heartbeatTimeoutSeconds = DefaultHeartbeatTimeoutSeconds;
    private int _reconnectDelaySeconds = DefaultReconnectDelaySeconds;
    private int _maxMessageBytes = DefaultMaxMessageBytes;
    private int _sendQueueCapacity = DefaultSendQueueCapacity;
    private int _writeTimeoutSeconds = DefaultWriteTimeoutSeconds;
    private int _receiveBufferSize = DefaultReceiveBufferSize;
    private long _nextSequence;
    private DateTimeOffset _lastReceivedUtc = DateTimeOffset.UtcNow;
    private volatile bool _authenticated;
    private volatile bool _endpointInputReceived;
    private volatile bool _guidInputReceived;

    private long _sentFrames;
    private long _receivedFrames;
    private long _cachedFrames;
    private long _droppedFrames;

    public CFGTcpClientNode()
    {
        Uuid = "13A9624A-371B-46E4-8C1F-21C67CF535C9";
        AddParam();
        AddPins();
    }

    public override string Name => "CFG TCP Client";
    public override string DisplayName { get; set; } = "CFG TCP Client";
    public override string Category => "C";
    public override bool RequiresPersistentHost => true;

    public BasePin GuidInputPin { get; } =
        new("input_string_guid", DataType.STRING, PinDirection.Input, required: false);

    public BasePin EndpointInputPin { get; } =
        new("input_string_endpoint", DataType.STRING, PinDirection.Input, required: false);

    public BasePin MessageInputPin { get; } =
        new("input_string_message", DataType.STRING, PinDirection.Input, required: false);

    public BasePin MessageOutputPin { get; } =
        new("output_string_message", DataType.STRING, PinDirection.Output);

    public override void AddPins()
    {
        AddInputPin(GuidInputPin);
        AddInputPin(EndpointInputPin);
        AddInputPin(MessageInputPin);
        AddOutputPin(MessageOutputPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter("ClientGuid", typeof(string), string.Empty,
            isEditable: true, group: "Identity", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("ServerHost", typeof(string), "127.0.0.1",
            isEditable: true, group: "Connection", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("ServerPort", typeof(int), DefaultServerPort,
            isEditable: true, group: "Connection", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("ConnectMode", typeof(string), ImmediateConnectMode,
            isEditable: true, group: "Connection", zone: "CFGTcpClientZone",
            editor: ParameterEditorKind.SingleSelect, options: ConnectModeOptions));
        AddParameter(new NodeParameter("ProtocolVersion", typeof(string), "1.0",
            isEditable: true, group: "Protocol", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("ConnectTimeoutSeconds", typeof(int), DefaultConnectTimeoutSeconds,
            isEditable: true, group: "Protocol", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("AuthTimeoutSeconds", typeof(int), DefaultAuthTimeoutSeconds,
            isEditable: true, group: "Protocol", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("AuthenticationTimeoutMilliseconds", typeof(int), DefaultAuthenticationTimeoutMilliseconds,
            isEditable: true, group: "Protocol", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("HeartbeatIntervalSeconds", typeof(int), DefaultHeartbeatIntervalSeconds,
            isEditable: true, group: "Protocol", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("HeartbeatTimeoutSeconds", typeof(int), DefaultHeartbeatTimeoutSeconds,
            isEditable: true, group: "Protocol", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("ReconnectDelaySeconds", typeof(int), DefaultReconnectDelaySeconds,
            isEditable: true, group: "Runtime", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("MaxMessageBytes", typeof(int), DefaultMaxMessageBytes,
            isEditable: true, group: "Runtime", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("SendQueueCapacity", typeof(int), DefaultSendQueueCapacity,
            isEditable: true, group: "Runtime", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("WriteTimeoutSeconds", typeof(int), DefaultWriteTimeoutSeconds,
            isEditable: true, group: "Runtime", zone: "CFGTcpClientZone"));
        AddParameter(new NodeParameter("ReceiveBufferSize", typeof(int), DefaultReceiveBufferSize,
            isEditable: true, group: "Runtime", zone: "CFGTcpClientZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        ApplyIfPresent(parameters, "ClientGuid", value => Param("ClientGuid").Value = ParameterValues.GetString(value, string.Empty) ?? string.Empty);
        ApplyIfPresent(parameters, "ServerHost", value => Param("ServerHost").Value = ParameterValues.GetString(value, "127.0.0.1") ?? "127.0.0.1");
        ApplyIfPresent(parameters, "ServerPort", value => Param("ServerPort").Value = ParameterValues.GetInt(value, DefaultServerPort));
        ApplyIfPresent(parameters, "ConnectMode", value => Param("ConnectMode").Value = ParameterValues.GetString(value, ImmediateConnectMode) ?? ImmediateConnectMode);
        ApplyIfPresent(parameters, "ProtocolVersion", value => Param("ProtocolVersion").Value = ParameterValues.GetString(value, "1.0") ?? "1.0");
        ApplyIfPresent(parameters, "ConnectTimeoutSeconds", value => Param("ConnectTimeoutSeconds").Value = ParameterValues.GetInt(value, DefaultConnectTimeoutSeconds));
        ApplyIfPresent(parameters, "AuthTimeoutSeconds", value => Param("AuthTimeoutSeconds").Value = ParameterValues.GetInt(value, DefaultAuthTimeoutSeconds));
        ApplyIfPresent(parameters, "AuthenticationTimeoutMilliseconds", value => Param("AuthenticationTimeoutMilliseconds").Value = ParameterValues.GetInt(value, DefaultAuthenticationTimeoutMilliseconds));
        ApplyIfPresent(parameters, "HeartbeatIntervalSeconds", value => Param("HeartbeatIntervalSeconds").Value = ParameterValues.GetInt(value, DefaultHeartbeatIntervalSeconds));
        ApplyIfPresent(parameters, "HeartbeatTimeoutSeconds", value => Param("HeartbeatTimeoutSeconds").Value = ParameterValues.GetInt(value, DefaultHeartbeatTimeoutSeconds));
        ApplyIfPresent(parameters, "ReconnectDelaySeconds", value => Param("ReconnectDelaySeconds").Value = ParameterValues.GetInt(value, DefaultReconnectDelaySeconds));
        ApplyIfPresent(parameters, "MaxMessageBytes", value => Param("MaxMessageBytes").Value = ParameterValues.GetInt(value, DefaultMaxMessageBytes));
        ApplyIfPresent(parameters, "SendQueueCapacity", value => Param("SendQueueCapacity").Value = ParameterValues.GetInt(value, DefaultSendQueueCapacity));
        ApplyIfPresent(parameters, "WriteTimeoutSeconds", value => Param("WriteTimeoutSeconds").Value = ParameterValues.GetInt(value, DefaultWriteTimeoutSeconds));
        ApplyIfPresent(parameters, "ReceiveBufferSize", value => Param("ReceiveBufferSize").Value = ParameterValues.GetInt(value, DefaultReceiveBufferSize));
    }

    private static void ApplyIfPresent(
        IReadOnlyDictionary<string, object?> parameters,
        string name,
        Action<object?> apply)
    {
        if (parameters.TryGetValue(name, out var value) && value is not null)
            apply(value);
    }

    public override async Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        _topicBus = context.GetService<ITopicBus>() ?? TopicBus.Instance;
        _context = context;
        await Stop(context, ct).ConfigureAwait(false);

        var configuredGuid =
            (ParameterValues.GetString(Param("ClientGuid").Value, string.Empty) ?? string.Empty).Trim();
        _clientGuid = string.Empty;
        if (string.IsNullOrWhiteSpace(configuredGuid))
        {
            context.Logger.LogInformation(
                "CFG TCP client initialized without a configured GUID; waiting for input_string_guid.");
        }
        else if (ProtocolMessage.IsGuid(configuredGuid, out var normalizedConfiguredGuid))
        {
            _clientGuid = normalizedConfiguredGuid;
            context.Logger.LogInformation(
                "CFG TCP client configured GUID fallback={Guid}; connection still waits for input_string_guid.",
                _clientGuid);
        }
        else
        {
            context.Logger.LogWarning(
                "CFG TCP client ignored invalid ClientGuid parameter {Guid}; waiting for input_string_guid.",
                configuredGuid);
        }

        _host = (ParameterValues.GetString(Param("ServerHost").Value, "127.0.0.1") ?? "127.0.0.1").Trim();
        _port = ReadBoundedInt(context, "ServerPort", DefaultServerPort, 1, 65535);
        _connectMode = NormalizeConnectMode(context);
        _protocolVersion = (ParameterValues.GetString(Param("ProtocolVersion").Value, "1.0") ?? "1.0").Trim();
        _connectTimeoutSeconds = ReadBoundedInt(context, "ConnectTimeoutSeconds", DefaultConnectTimeoutSeconds, 1, 300);
        _authTimeoutSeconds = ReadBoundedInt(context, "AuthTimeoutSeconds", DefaultAuthTimeoutSeconds, 1, 300);
        _authenticationTimeoutMilliseconds = ReadBoundedInt(context, "AuthenticationTimeoutMilliseconds", DefaultAuthenticationTimeoutMilliseconds, 1000, int.MaxValue);
        _heartbeatIntervalSeconds = ReadBoundedInt(context, "HeartbeatIntervalSeconds", DefaultHeartbeatIntervalSeconds, 1, 3600);
        _heartbeatTimeoutSeconds = ReadBoundedInt(context, "HeartbeatTimeoutSeconds", DefaultHeartbeatTimeoutSeconds, 1, 3600);
        _heartbeatTimeoutSeconds = Math.Max(_heartbeatTimeoutSeconds, _heartbeatIntervalSeconds);
        _reconnectDelaySeconds = ReadBoundedInt(context, "ReconnectDelaySeconds", DefaultReconnectDelaySeconds, 1, 3600);
        _maxMessageBytes = ReadBoundedInt(context, "MaxMessageBytes", DefaultMaxMessageBytes, 64, 16 * 1024 * 1024);
        _sendQueueCapacity = ReadBoundedInt(context, "SendQueueCapacity", DefaultSendQueueCapacity, 1, 65536);
        _writeTimeoutSeconds = ReadBoundedInt(context, "WriteTimeoutSeconds", DefaultWriteTimeoutSeconds, 1, 3600);
        _receiveBufferSize = ReadBoundedInt(context, "ReceiveBufferSize", DefaultReceiveBufferSize, 64, 1_048_576);

        _sendChannel = CreateSendChannel(_sendQueueCapacity);
        context.Logger.LogInformation(
            "CFG TCP client initialized target={Host}:{Port}, connectMode={ConnectMode}, protocol={Version}, authTimeout={AuthTimeout}s, authenticationTimeout={AuthenticationTimeout}ms, heartbeat={Heartbeat}s, reconnectDelay={Reconnect}s; waiting for input_string_guid.",
            _host, _port, _connectMode, _protocolVersion, _authTimeoutSeconds, _authenticationTimeoutMilliseconds, _heartbeatIntervalSeconds, _reconnectDelaySeconds);

        if (_connectMode == WaitForEndpointConnectMode && EndpointInputPin.ConnectedPin is null)
        {
            context.Logger.LogWarning(
                "CFG TCP client ConnectMode=WaitForEndpoint but input_string_endpoint is not connected; Run will wait indefinitely for an endpoint input.");
        }
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        if (_sendChannel is null)
            throw new InvalidOperationException("CFG TCP client must be initialized before Run.");
        if (_supervisorTask is not null)
            throw new InvalidOperationException("CFG TCP client is already running.");

        _cts = new CancellationTokenSource();
        _context = context;
        Running = true;

        if (!_guidInputReceived)
        {
            var endpoint = GetEndpoint();
            context.Logger.LogInformation(
                "CFG TCP client is running and waiting for input_string_guid before connecting; configured fallback target is {Host}:{Port}.",
                endpoint.Host, endpoint.Port);
            return Task.CompletedTask;
        }

        if (!TryStartConnectionSupervisor(context))
        {
            var endpoint = GetEndpoint();
            context.Logger.LogInformation(
                "CFG TCP client is waiting for input_string_endpoint before connecting; configured fallback target is {Host}:{Port}.",
                endpoint.Host, endpoint.Port);
            return Task.CompletedTask;
        }

        context.Logger.LogInformation(
            "CFG TCP client is running: GUID={Guid}, target={Host}:{Port}, connectMode={ConnectMode}.",
            _clientGuid, _host, _port, _connectMode);
        return Task.CompletedTask;
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        await _stopLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Running = false;
            _authenticated = false;

            CancellationTokenSource? cts;
            Task? supervisorTask;
            lock (_supervisorStartGate)
            {
                cts = _cts;
                _cts = null;
                supervisorTask = _supervisorTask;
                _supervisorTask = null;
            }
            cts?.Cancel();

            CancellationTokenSource? connectionCts;
            lock (_endpointGate)
            {
                connectionCts = _connectionCts;
                _connectionCts = null;
            }
            connectionCts?.Cancel();
            DisposeCurrentConnection();

            if (supervisorTask is not null)
            {
                try
                {
                    await supervisorTask.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    context.Logger.LogWarning("CFG TCP client supervisor did not stop within 3s.");
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning(ex, "CFG TCP client supervisor ended with an error while stopping.");
                }
            }

            DisposeCurrentConnection();
            cts?.Dispose();
            _endpointInputReceived = false;
            _guidInputReceived = false;
            _clientGuid = string.Empty;
            _location = string.Empty;
            lock (_pendingBusinessInputGate)
            {
                _pendingBusinessInputs.Clear();
            }

            SetIdle();
            context.Logger.LogInformation(
                "CFG TCP client stopped: sent={Sent}, received={Received}, cached={Cached}, dropped={Dropped}",
                Interlocked.Read(ref _sentFrames),
                Interlocked.Read(ref _receivedFrames),
                Interlocked.Read(ref _cachedFrames),
                Interlocked.Read(ref _droppedFrames));
        }
        finally
        {
            _stopLock.Release();
        }
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default) =>
        Stop(context, ct);

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, GuidInputPin))
        {
            HandleGuidInput(value);
            return;
        }

        if (ReferenceEquals(pin, EndpointInputPin))
        {
            HandleEndpointInput(value);
            return;
        }

        if (ReferenceEquals(pin, MessageInputPin))
            HandleBusinessInput(value);
    }

    private void HandleGuidInput(Sample? value)
    {
        if (value is not StringSample text || text.StringValue is null)
        {
            _logger?.LogError("CFG TCP client ignored GUID input: expected StringSample.");
            return;
        }

        if (!ProtocolMessage.IsGuid(text.StringValue.Trim(), out var normalizedGuid))
        {
            _logger?.LogError(
                "CFG TCP client rejected input_string_guid value {Guid}: expected a valid GUID.",
                text.StringValue);
            return;
        }

        var guidChanged = false;
        lock (_endpointGate)
        {
            guidChanged = !string.Equals(_clientGuid, normalizedGuid, StringComparison.Ordinal);
            _clientGuid = normalizedGuid;
            _guidInputReceived = true;
            if (guidChanged)
            {
                _authenticated = false;
                _location = string.Empty;
                _connectionCts?.Cancel();
            }
        }

        _logger?.LogInformation(
            "CFG TCP client received runtime GUID={Guid}; persisted parameters were not modified.",
            normalizedGuid);

        ReplayPendingBusinessInputs();

        if (Running && _context is not null)
            TryStartConnectionSupervisor(_context);
    }

    private void HandleEndpointInput(Sample? value)
    {
        if (value is not StringSample text || text.StringValue is null)
        {
            _logger?.LogError("CFG TCP client ignored endpoint input: expected StringSample.");
            return;
        }

        if (!TryParseEndpoint(text.StringValue, out var host, out var port))
        {
            _logger?.LogError("CFG TCP client rejected endpoint {Endpoint}: expected host:port.", text.StringValue);
            return;
        }

        _endpointInputReceived = true;
        lock (_endpointGate)
        {
            _host = host;
            _port = port;
            _connectionCts?.Cancel();
        }

        _logger?.LogInformation(
            "CFG TCP client runtime endpoint changed to {Host}:{Port}; persisted parameters were not modified.",
            host, port);

        if (_guidInputReceived && Running && _context is not null)
            TryStartConnectionSupervisor(_context);
    }

    private string NormalizeConnectMode(INodeContext context)
    {
        var configured = (ParameterValues.GetString(Param("ConnectMode").Value, ImmediateConnectMode) ?? ImmediateConnectMode).Trim();
        if (string.Equals(configured, ImmediateConnectMode, StringComparison.OrdinalIgnoreCase))
            return ImmediateConnectMode;
        if (string.Equals(configured, WaitForEndpointConnectMode, StringComparison.OrdinalIgnoreCase))
            return WaitForEndpointConnectMode;

        context.Logger.LogWarning(
            "CFG TCP client parameter ConnectMode={ConnectMode} is invalid; using {Fallback}.",
            configured, ImmediateConnectMode);
        Param("ConnectMode").Value = ImmediateConnectMode;
        return ImmediateConnectMode;
    }

    private bool TryStartConnectionSupervisor(INodeContext context)
    {
        lock (_supervisorStartGate)
        {
            if (!Running || !_guidInputReceived || _supervisorTask is not null || _cts is null)
                return false;
            if (_connectMode == WaitForEndpointConnectMode && !_endpointInputReceived)
                return false;

            var cts = _cts;
            _supervisorTask = Task.Run(
                () => ConnectionSupervisorAsync(context, cts.Token),
                CancellationToken.None);
            var endpoint = GetEndpoint();
            context.Logger.LogInformation(
                "CFG TCP client connection supervisor started for {Host}:{Port}; connectMode={ConnectMode}.",
                endpoint.Host, endpoint.Port, _connectMode);
            return true;
        }
    }

    private (string Host, int Port) GetEndpoint()
    {
        lock (_endpointGate)
            return (_host, _port);
    }

    private void HandleBusinessInput(Sample? value)
    {
        var channel = _sendChannel;
        if (channel is null)
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client ignored business input before initialization.");
            return;
        }

        if (value is not StringSample text || text.StringValue is null)
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client ignored business input: expected StringSample.");
            return;
        }

        HandleBusinessInputText(text.StringValue, channel);
    }

    private void HandleBusinessInputText(string inputText, Channel<JsonObject> channel)
    {
        if (!ProtocolMessage.TryParseObject(inputText, out var input, out var error))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client rejected message input: {Error}.", error);
            return;
        }

        if (!_guidInputReceived)
        {
            CachePendingBusinessInput(inputText);
            return;
        }

        if (ProtocolMessage.TryGetString(input, ProtocolConstants.Type, out var inputType) &&
            string.Equals(inputType, ProtocolConstants.RunAck, StringComparison.OrdinalIgnoreCase))
        {
            HandleProtocolEnvelopeInput(input, inputType);
            return;
        }

        if (!ProtocolMessage.TryCreateBusinessTemplate(input, out var data, out error))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client rejected business input: {Error}.", error);
            return;
        }

        var location = _location;
        if (string.IsNullOrWhiteSpace(location))
        {
            if (!TryResolveBusinessLocation(data, out location, out error))
            {
                Interlocked.Increment(ref _droppedFrames);
                _logger?.LogError("CFG TCP client rejected business input: {Error}.", error);
                return;
            }

            _logger?.LogInformation(
                "CFG TCP client inferred LOCATION={Location} from the one-to-five-record MESSAGES slot range before authentication.",
                location);
            _location = location;
        }

        if (!ProtocolMessage.TryValidateBusinessData(data, location, out error))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client rejected business input: {Error}.", error);
            return;
        }

        var envelope = ProtocolMessage.CreateEnvelope(
            _clientGuid,
            location,
            ProtocolConstants.Business,
            _protocolVersion,
            NextSequence(),
            ProtocolMessage.FormatTimestamp(DateTimeOffset.UtcNow),
            data);

        if (!channel.Writer.TryWrite(envelope))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client send queue is full; business frame was dropped.");
            return;
        }

        Interlocked.Increment(ref _cachedFrames);
        if (!_authenticated)
        {
            _logger?.LogError(
                "CFG TCP client is not authenticated; business frame was cached for reconnect.");
        }
        else
        {
            _logger?.LogTrace("CFG TCP client queued business SEQ={Sequence}.", envelope[ProtocolConstants.Sequence]);
        }
    }

    private void CachePendingBusinessInput(string inputText)
    {
        lock (_pendingBusinessInputGate)
        {
            if (_pendingBusinessInputs.Count >= _sendQueueCapacity)
            {
                Interlocked.Increment(ref _droppedFrames);
                _logger?.LogError(
                    "CFG TCP client pending business queue is full; input was dropped while waiting for input_string_guid.");
                return;
            }

            _pendingBusinessInputs.Enqueue(inputText);
        }

        _logger?.LogTrace(
            "CFG TCP client cached business input while waiting for input_string_guid.");
    }

    private void ReplayPendingBusinessInputs()
    {
        string[] pending;
        lock (_pendingBusinessInputGate)
        {
            if (_pendingBusinessInputs.Count == 0)
                return;

            pending = _pendingBusinessInputs.ToArray();
            _pendingBusinessInputs.Clear();
        }

        var channel = _sendChannel;
        if (channel is null)
            return;

        foreach (var inputText in pending)
            HandleBusinessInputText(inputText, channel);
    }

    private static bool TryResolveBusinessLocation(
        JsonObject data,
        out string location,
        out string? error)
    {
        location = string.Empty;
        if (ProtocolMessage.TryValidateBusinessData(data, ProtocolConstants.Cfg1, out _))
        {
            location = ProtocolConstants.Cfg1;
            error = null;
            return true;
        }

        if (ProtocolMessage.TryValidateBusinessData(data, ProtocolConstants.Cfg2, out _))
        {
            location = ProtocolConstants.Cfg2;
            error = null;
            return true;
        }

        ProtocolMessage.TryValidateBusinessData(data, ProtocolConstants.Cfg1, out error);
        return false;
    }

    private async Task ConnectionSupervisorAsync(INodeContext context, CancellationToken ct)
    {
        long authenticationDeadlineMilliseconds = 0;
        var lastAuthenticationFailure = "authentication has not completed.";

        while (!ct.IsCancellationRequested)
        {
            if (authenticationDeadlineMilliseconds == 0)
                authenticationDeadlineMilliseconds = Environment.TickCount64 + _authenticationTimeoutMilliseconds;

            if (Environment.TickCount64 >= authenticationDeadlineMilliseconds)
            {
                context.Logger.LogError(
                    "CFG TCP client authentication did not complete within {Timeout}ms; stopping the workflow. Last failure: {Failure}",
                    _authenticationTimeoutMilliseconds,
                    lastAuthenticationFailure);
                _authenticated = false;
                Running = false;
                return;
            }

            string host;
            int port;
            lock (_endpointGate)
            {
                host = _host;
                port = _port;
            }

            var remainingAuthenticationTime = TimeSpan.FromMilliseconds(
                Math.Max(1, authenticationDeadlineMilliseconds - Environment.TickCount64));

            TcpSocket? client = null;
            NetworkStream? stream = null;
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var authenticationDeadlineCts = CancellationTokenSource.CreateLinkedTokenSource(connectionCts.Token);
            authenticationDeadlineCts.CancelAfter(remainingAuthenticationTime);
            lock (_endpointGate)
                _connectionCts = connectionCts;

            try
            {
                await PublishStatusAsync(
                    CFGSTcpProtocolConnectionStates.Connecting,
                    host, port, context).ConfigureAwait(false);

                client = new TcpSocket();
                await client.ConnectAsync(host, port, authenticationDeadlineCts.Token)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(_connectTimeoutSeconds), authenticationDeadlineCts.Token)
                    .ConfigureAwait(false);
                stream = client.GetStream();
                SetCurrentConnection(client, stream);
                _lastReceivedUtc = DateTimeOffset.UtcNow;

                await SendAuthAsync(stream, authenticationDeadlineCts.Token).ConfigureAwait(false);
                var location = await ReadAuthenticationAsync(stream, context, authenticationDeadlineCts.Token).ConfigureAwait(false);
                if (location is null)
                    throw new IOException("AUTH_FAIL or authentication timeout.");

                _location = location;
                _authenticated = true;
                authenticationDeadlineMilliseconds = 0;
                authenticationDeadlineCts.CancelAfter(Timeout.InfiniteTimeSpan);
                _lastReceivedUtc = DateTimeOffset.UtcNow;
                await PublishStatusAsync(
                    CFGSTcpProtocolConnectionStates.Authenticated,
                    host, port, context).ConfigureAwait(false);
                context.Logger.LogInformation(
                    "CFG TCP client authenticated GUID={Guid}, LOCATION={Location}, server={Host}:{Port}.",
                    _clientGuid, _location, host, port);

                var writeTask = Task.Run(
                    () => SendBusinessLoopAsync(stream, context, connectionCts.Token),
                    CancellationToken.None);
                var heartbeatTask = Task.Run(
                    () => HeartbeatLoopAsync(stream, context, connectionCts.Token),
                    CancellationToken.None);
                var readTask = Task.Run(
                    () => ReadAuthenticatedLoopAsync(stream, context, connectionCts.Token),
                    CancellationToken.None);

                var completed = await Task.WhenAny(readTask, heartbeatTask).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await completed.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        lastAuthenticationFailure = $"session ended: {ex.Message}";
                        context.Logger.LogWarning(
                            "CFG TCP client session ended: {Message}", ex.Message);
                    }
                }

                connectionCts.Cancel();
                DisposeCurrentConnection();
                await IgnoreTaskCompletionAsync(writeTask, heartbeatTask, readTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected during Stop.
            }
            catch (OperationCanceledException) when (
                authenticationDeadlineCts.IsCancellationRequested &&
                !connectionCts.IsCancellationRequested)
            {
                lastAuthenticationFailure = $"authentication did not complete within {_authenticationTimeoutMilliseconds}ms.";
                context.Logger.LogWarning(
                    "CFG TCP client authentication deadline expired for {Host}:{Port}.",
                    host, port);
            }
            catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
            {
                context.Logger.LogInformation(
                    "CFG TCP client connection target changed or was cancelled; reconnecting.");
            }
            catch (Exception ex)
            {
                lastAuthenticationFailure = $"{host}:{port}: {ex.Message}";
                context.Logger.LogWarning(
                    ex,
                    "CFG TCP client connection to {Host}:{Port} failed: {Message}",
                    host, port, ex.Message);
            }
            finally
            {
                _authenticated = false;
                lock (_endpointGate)
                    _connectionCts = null;
                DisposeCurrentConnection();
                try
                {
                    client?.Dispose();
                }
                catch
                {
                    // Socket disposal is best effort.
                }

                if (!ct.IsCancellationRequested)
                {
                    await PublishStatusAsync(
                        CFGSTcpProtocolConnectionStates.Disconnected,
                        host, port, context).ConfigureAwait(false);
                }
            }

            if (ct.IsCancellationRequested)
                break;

            if (authenticationDeadlineMilliseconds == 0)
                authenticationDeadlineMilliseconds = Environment.TickCount64 + _authenticationTimeoutMilliseconds;

            var remainingAuthenticationMilliseconds = authenticationDeadlineMilliseconds - Environment.TickCount64;
            if (remainingAuthenticationMilliseconds <= 0)
                continue;

            var reconnectDelay = TimeSpan.FromSeconds(_reconnectDelaySeconds);
            var boundedDelay = TimeSpan.FromMilliseconds(
                Math.Min(reconnectDelay.TotalMilliseconds, remainingAuthenticationMilliseconds));
            try
            {
                await Task.Delay(boundedDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendAuthAsync(NetworkStream stream, CancellationToken ct)
    {
        var envelope = ProtocolMessage.CreateEnvelope(
            _clientGuid,
            string.Empty,
            ProtocolConstants.Auth,
            _protocolVersion,
            NextSequence(),
            ProtocolMessage.FormatTimestamp(DateTimeOffset.UtcNow),
            new JsonObject());
        await SendEnvelopeAsync(stream, envelope, ct).ConfigureAwait(false);
        _logger?.LogInformation("CFG TCP client sent AUTH for GUID={Guid}.", _clientGuid);
    }

    private async Task<string?> ReadAuthenticationAsync(
        NetworkStream stream,
        INodeContext context,
        CancellationToken ct)
    {
        var accumulator = new CfgFrameAccumulator();
        var buffer = new byte[_receiveBufferSize];
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(TimeSpan.FromSeconds(_authTimeoutSeconds));

        try
        {
            while (!authCts.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), authCts.Token).ConfigureAwait(false);
                if (count == 0)
                    return null;

                if (!accumulator.TryAppend(buffer.AsSpan(0, count), _maxMessageBytes, out var frames, out var error))
                {
                    context.Logger.LogError("CFG TCP client authentication frame error: {Error}.", error);
                    return null;
                }

                foreach (var frame in frames)
                {
                    Interlocked.Increment(ref _receivedFrames);
                    if (!ProtocolMessage.TryParseObject(frame, out var envelope, out error) ||
                        !ProtocolMessage.TryValidateEnvelope(envelope, out error))
                    {
                        context.Logger.LogError("CFG TCP client authentication envelope error: {Error}.", error);
                        return null;
                    }

                    if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Guid, out var guidText) ||
                        !ProtocolMessage.TryGetGuid(envelope, ProtocolConstants.Guid, out var responseGuid) ||
                        !string.Equals(responseGuid, _clientGuid, StringComparison.Ordinal))
                    {
                        context.Logger.LogError(
                            "CFG TCP client authentication envelope GUID mismatch: {Guid}.", guidText);
                        return null;
                    }

                    if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Type, out var type))
                        return null;

                    if (string.Equals(type, ProtocolConstants.AuthOk, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Version, out var version) ||
                            !string.Equals(version, _protocolVersion, StringComparison.Ordinal))
                        {
                            context.Logger.LogError(
                                "CFG TCP client AUTH_OK VERSION mismatch: {Version}.", version);
                            return null;
                        }

                        if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Location, out var location) &&
                            ProtocolMessage.Get(envelope, ProtocolConstants.Data) is JsonObject data)
                        {
                            if (!ProtocolMessage.TryGetString(data, ProtocolConstants.Location, out location))
                                ProtocolMessage.TryGetString(data, "ROLE", out location);
                        }

                        if (!ProtocolMessage.IsLocation(location))
                        {
                            context.Logger.LogError(
                                "CFG TCP client AUTH_OK did not contain a valid LOCATION.");
                            return null;
                        }

                        return ProtocolMessage.NormalizeLocation(location);
                    }

                    if (string.Equals(type, ProtocolConstants.AuthFail, StringComparison.OrdinalIgnoreCase))
                    {
                        var reason = ProtocolMessage.Get(envelope, ProtocolConstants.Data) is JsonObject data &&
                            ProtocolMessage.TryGetString(data, "REASON", out var text)
                            ? text
                            : "unknown";
                        context.Logger.LogError("CFG TCP client AUTH failed: {Reason}.", reason);
                        return null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            context.Logger.LogError(
                "CFG TCP client authentication timed out after {Timeout}s.", _authTimeoutSeconds);
            return null;
        }

        return null;
    }

    private async Task ReadAuthenticatedLoopAsync(
        NetworkStream stream,
        INodeContext context,
        CancellationToken ct)
    {
        var accumulator = new CfgFrameAccumulator();
        var buffer = new byte[_receiveBufferSize];
        while (!ct.IsCancellationRequested)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (count == 0)
                throw new IOException("server closed the connection");

            if (!accumulator.TryAppend(buffer.AsSpan(0, count), _maxMessageBytes, out var frames, out var error))
                throw new IOException($"invalid protocol frame: {error}");

            foreach (var frame in frames)
            {
                Interlocked.Increment(ref _receivedFrames);
                if (!ProtocolMessage.TryParseObject(frame, out var envelope, out error) ||
                    !ProtocolMessage.TryValidateEnvelope(envelope, out error, requireLocation: true))
                {
                    context.Logger.LogWarning("CFG TCP client ignored invalid envelope: {Error}.", error);
                    continue;
                }

                if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Version, out var version) ||
                    !string.Equals(version, _protocolVersion, StringComparison.Ordinal))
                {
                    context.Logger.LogWarning(
                        "CFG TCP client ignored VERSION {Version}; expected {Expected}.",
                        version, _protocolVersion);
                    continue;
                }

                if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Guid, out var guidText) ||
                    !ProtocolMessage.TryGetGuid(envelope, ProtocolConstants.Guid, out var guid) ||
                    !string.Equals(guid, _clientGuid, StringComparison.Ordinal))
                {
                    context.Logger.LogWarning(
                        "CFG TCP client ignored envelope for another GUID: {Guid}.", guidText);
                    continue;
                }

                if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Location, out var location) ||
                    !string.Equals(ProtocolMessage.NormalizeLocation(location), _location, StringComparison.Ordinal))
                {
                    context.Logger.LogWarning(
                        "CFG TCP client ignored envelope with LOCATION {Location}; expected {Expected}.",
                        location, _location);
                    continue;
                }

                if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Type, out var type))
                    continue;

                _lastReceivedUtc = DateTimeOffset.UtcNow;

                if (string.Equals(type, ProtocolConstants.Pong, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogTrace("CFG TCP client received PONG for GUID={Guid}.", _clientGuid);
                }
                else if (string.Equals(type, ProtocolConstants.AuthFail, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("server sent AUTH_FAIL after authentication");
                }
                else if (IsControlEnvelopeType(type))
                {
                    _logger?.LogTrace("CFG TCP client ignored control TYPE {Type}.", type);
                }
                else
                {
                    PublishMessageEnvelope(frame, type);
                }
            }
        }
    }

    private async Task SendBusinessLoopAsync(
        NetworkStream stream,
        INodeContext context,
        CancellationToken ct)
    {
        var channel = _sendChannel;
        if (channel is null)
            return;

        await foreach (var envelope in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await SendEnvelopeAsync(stream, envelope, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _sentFrames);
                Interlocked.Decrement(ref _cachedFrames);
                context.Logger.LogTrace(
                    "CFG TCP client sent BUSINESS SEQ={Sequence}.",
                    ProtocolMessage.Get(envelope, ProtocolConstants.Sequence)?.ToJsonString() ?? "?");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                channel.Writer.TryWrite(envelope);
                throw;
            }
        }
    }

    private async Task HeartbeatLoopAsync(
        NetworkStream stream,
        INodeContext context,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_heartbeatIntervalSeconds), ct).ConfigureAwait(false);
            if ((DateTimeOffset.UtcNow - _lastReceivedUtc).TotalSeconds > _heartbeatTimeoutSeconds)
                throw new TimeoutException($"heartbeat timeout after {_heartbeatTimeoutSeconds}s");

            var ping = ProtocolMessage.CreateEnvelope(
                _clientGuid,
                _location,
                ProtocolConstants.Ping,
                _protocolVersion,
                NextSequence(),
                ProtocolMessage.FormatTimestamp(DateTimeOffset.UtcNow),
                new JsonObject());
            await SendEnvelopeAsync(stream, ping, ct).ConfigureAwait(false);
            context.Logger.LogTrace("CFG TCP client sent PING for GUID={Guid}.", _clientGuid);
        }
    }

    private async Task SendEnvelopeAsync(NetworkStream stream, JsonObject envelope, CancellationToken ct)
    {
        var bytes = ProtocolMessage.EncodeFrame(envelope);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes.AsMemory(), ct)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(_writeTimeoutSeconds), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void HandleProtocolEnvelopeInput(JsonObject envelope, string type)
    {
        var channel = _sendChannel;
        if (channel is null)
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client ignored protocol input before initialization.");
            return;
        }

        if (!ProtocolMessage.TryValidateEnvelope(envelope, out var error, requireLocation: true))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client rejected {Type} input: {Error}.", type, error);
            return;
        }

        if (!ProtocolMessage.TryGetGuid(envelope, ProtocolConstants.Guid, out var guid) ||
            !string.Equals(guid, _clientGuid, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client rejected {Type} input with mismatched GUID.", type);
            return;
        }

        if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Location, out var location) ||
            !string.Equals(ProtocolMessage.NormalizeLocation(location), _location, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError(
                "CFG TCP client rejected {Type} input with mismatched LOCATION {Location}; expected {Expected}.",
                type,
                location,
                _location);
            return;
        }

        if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Version, out var version) ||
            !string.Equals(version, _protocolVersion, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client rejected {Type} input with mismatched VERSION.", type);
            return;
        }

        var clonedEnvelope = envelope.DeepClone().AsObject();
        if (!channel.Writer.TryWrite(clonedEnvelope))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFG TCP client send queue is full; {Type} frame was dropped.", type);
            return;
        }

        Interlocked.Increment(ref _cachedFrames);
        _logger?.LogInformation(
            "CFG TCP client queued {Type} for GUID={Guid}, LOCATION={Location}, SEQ={Sequence}.",
            type,
            _clientGuid,
            _location,
            ProtocolMessage.Get(envelope, ProtocolConstants.Sequence)?.ToJsonString() ?? "?");
    }

    private void PublishMessageEnvelope(string frame, string type)
    {
        var sample = StringSample.CreateSample();
        sample.SetValue(frame, MessageOutputPin.Name);
        MessageOutputPin.SendSampleWithDispatchLog(
            sample,
            $"{type} for {_clientGuid}",
            LogLevel.Information);
    }

    private static bool IsControlEnvelopeType(string type) =>
        string.Equals(type, ProtocolConstants.Auth, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ProtocolConstants.AuthOk, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ProtocolConstants.AuthFail, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ProtocolConstants.Ping, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ProtocolConstants.Pong, StringComparison.OrdinalIgnoreCase);

    private async Task PublishStatusAsync(
        string state,
        string host,
        int port,
        INodeContext context)
    {
        var samples = new Dictionary<string, Sample>(StringComparer.Ordinal)
        {
            [CFGSTcpProtocolStatusKeys.GUID] = CreateStringSample(CFGSTcpProtocolStatusKeys.GUID, _clientGuid),
            [CFGSTcpProtocolStatusKeys.ROLE] = CreateStringSample(CFGSTcpProtocolStatusKeys.ROLE, _location),
            [CFGSTcpProtocolStatusKeys.STATE] = CreateStringSample(CFGSTcpProtocolStatusKeys.STATE, state),
            [CFGSTcpProtocolStatusKeys.ENDPOINT] = CreateStringSample(CFGSTcpProtocolStatusKeys.ENDPOINT, $"{host}:{port}"),
            [CFGSTcpProtocolStatusKeys.LAST_SEEN_UTC] = CreateStringSample(
                CFGSTcpProtocolStatusKeys.LAST_SEEN_UTC,
                ProtocolMessage.FormatTimestamp(_lastReceivedUtc)),
            [CFGSTcpProtocolStatusKeys.PENDING_SEND_COUNT] = CreateStringSample(
                CFGSTcpProtocolStatusKeys.PENDING_SEND_COUNT,
                Math.Max(0, Interlocked.Read(ref _cachedFrames)).ToString()),
        };

        try
        {
            await (_topicBus ?? TopicBus.Instance).PublishAsync(
                CFGSTcpProtocolBroadcasts.CFGTcpClientConnectionStatus,
                BroadcastTopicMessage.FromSampleDictionary(samples),
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                ex,
                "CFG TCP client failed to publish connection status for {Guid}: {Message}",
                _clientGuid, ex.Message);
        }
    }

    private void SetCurrentConnection(TcpSocket client, NetworkStream stream)
    {
        lock (_endpointGate)
        {
            _client = client;
            _stream = stream;
        }
    }

    private void DisposeCurrentConnection()
    {
        TcpSocket? client;
        lock (_endpointGate)
        {
            client = _client;
            _client = null;
            _stream = null;
        }

        try
        {
            client?.Dispose();
        }
        catch
        {
            // Socket disposal is best effort.
        }
    }

    private long NextSequence() => Interlocked.Increment(ref _nextSequence);

    private static async Task IgnoreTaskCompletionAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // The connection owner reports the first failure; secondary tasks are best effort.
            }
        }
    }

    private static bool TryParseEndpoint(string input, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var endpoint = input.Trim();
        var separatorIndex = endpoint.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == endpoint.Length - 1)
            return false;

        host = endpoint[..separatorIndex].Trim();
        var portText = endpoint[(separatorIndex + 1)..].Trim();
        if (host.Length == 0 || portText.Length == 0)
            return false;
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            host = host[1..^1].Trim();

        return host.Length > 0 &&
               int.TryParse(portText, out port) &&
               port is >= 1 and <= 65535;
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    private int ReadBoundedInt(INodeContext context, string parameterName, int fallback, int min, int max)
    {
        var value = ParameterValues.GetInt(Param(parameterName).Value, fallback);
        if (value < min || value > max)
        {
            context.Logger.LogWarning(
                "CFG TCP client parameter {Name}={Value} is outside [{Min}, {Max}]; clamping.",
                parameterName, value, min, max);
            value = Math.Clamp(value, min, max);
        }

        return value;
    }

    private static Channel<JsonObject> CreateSendChannel(int capacity) =>
        Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private static StringSample CreateStringSample(string key, string value)
    {
        var sample = StringSample.CreateSample();
        sample.SetValue(value, key);
        return sample;
    }
}





