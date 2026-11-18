// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CFGSTcpServerNode.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentFlow.Contracts;
using AgentFlow.Shared.RTS.CFGS.Broadcasts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;
using Node.CFGTcpProtocol;

namespace Node.CFGSTcpServer;

/// <summary>
/// CFGS TCP server session node. It owns authentication, GUID-to-session routing,
/// heartbeat handling, reconnect/offline queuing, and semicolon-framed envelope transport.
/// Business payloads remain opaque inside the top-level JSON envelope.
/// </summary>
public sealed class CFGSTcpServerNode : BaseNode
{
    private const int DefaultPort = 9000;
    private const int DefaultAuthTimeoutSeconds = 5;
    private const int DefaultHeartbeatTimeoutSeconds = 30;
    private const int DefaultMaxUnauthenticatedClients = 8;
    private const int DefaultMaxMessageBytes = 65536;
    private const int DefaultPendingQueueCapacity = 128;
    private const int DefaultPendingQueueTtlSeconds = 15;
    private const int DefaultSendQueueCapacity = 256;
    private const int DefaultWriteTimeoutSeconds = 10;
    private const int DefaultReceiveBufferSize = 4096;

    private readonly object _configGate = new();
    private readonly object _pendingGate = new();
    private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ClientSession> _sessionsByGuid =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedList<PendingEnvelope>> _pendingByGuid =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _stopLock = new(1, 1);

    private IReadOnlyDictionary<string, CfgClientDefinition> _clientDefinitions =
        new Dictionary<string, CfgClientDefinition>(StringComparer.Ordinal);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Channel<SendRequest>? _sendChannel;
    private Task? _acceptTask;
    private Task? _sendTask;
    private Task? _maintenanceTask;
    private ILogger? _logger;
    private ITopicBus? _topicBus;

    private string _host = "0.0.0.0";
    private int _port = DefaultPort;
    private string _protocolVersion = "1.0";
    private int _authTimeoutSeconds = DefaultAuthTimeoutSeconds;
    private int _heartbeatTimeoutSeconds = DefaultHeartbeatTimeoutSeconds;
    private int _maxUnauthenticatedClients = DefaultMaxUnauthenticatedClients;
    private int _maxMessageBytes = DefaultMaxMessageBytes;
    private int _pendingQueueCapacity = DefaultPendingQueueCapacity;
    private int _pendingQueueTtlSeconds = DefaultPendingQueueTtlSeconds;
    private int _sendQueueCapacity = DefaultSendQueueCapacity;
    private int _writeTimeoutSeconds = DefaultWriteTimeoutSeconds;
    private int _receiveBufferSize = DefaultReceiveBufferSize;
    private bool _configLoaded;
    private int _nextSessionId;

    private long _acceptedConnections;
    private long _rejectedConnections;
    private long _authenticatedConnections;
    private long _receivedFrames;
    private long _sentFrames;
    private long _droppedFrames;

    public CFGSTcpServerNode()
    {
        Uuid = "3C5449BA-CB48-47A8-8562-D75FCA5D1F71";
        AddParam();
        AddPins();
    }

    public override string Name => "CFGS TCP Server";
    public override string DisplayName { get; set; } = "CFGS TCP Server";
    public override string Category => "Network";
    public override bool RequiresPersistentHost => true;

    public BasePin ConfigInputPin { get; } =
        new("input_string_client_config", DataType.STRING, PinDirection.Input, required: false);

    public BasePin MessageInputPin { get; } =
        new("input_string_message", DataType.STRING, PinDirection.Input, required: false);

    public BasePin MessageOutputPin { get; } =
        new("output_string_message", DataType.STRING, PinDirection.Output);

    public override void AddPins()
    {
        AddInputPin(ConfigInputPin);
        AddInputPin(MessageInputPin);
        AddOutputPin(MessageOutputPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter("Host", typeof(string), "0.0.0.0",
            isEditable: true, group: "Connection", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("Port", typeof(int), DefaultPort,
            isEditable: true, group: "Connection", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("ProtocolVersion", typeof(string), "1.0",
            isEditable: true, group: "Protocol", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("AuthTimeoutSeconds", typeof(int), DefaultAuthTimeoutSeconds,
            isEditable: true, group: "Protocol", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("HeartbeatTimeoutSeconds", typeof(int), DefaultHeartbeatTimeoutSeconds,
            isEditable: true, group: "Protocol", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("MaxUnauthenticatedClients", typeof(int), DefaultMaxUnauthenticatedClients,
            isEditable: true, group: "Runtime", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("MaxMessageBytes", typeof(int), DefaultMaxMessageBytes,
            isEditable: true, group: "Runtime", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("PendingQueueCapacity", typeof(int), DefaultPendingQueueCapacity,
            isEditable: true, group: "Runtime", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("PendingQueueTtlSeconds", typeof(int), DefaultPendingQueueTtlSeconds,
            isEditable: true, group: "Runtime", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("SendQueueCapacity", typeof(int), DefaultSendQueueCapacity,
            isEditable: true, group: "Runtime", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("WriteTimeoutSeconds", typeof(int), DefaultWriteTimeoutSeconds,
            isEditable: true, group: "Runtime", zone: "CFGSTcpServerZone"));
        AddParameter(new NodeParameter("ReceiveBufferSize", typeof(int), DefaultReceiveBufferSize,
            isEditable: true, group: "Runtime", zone: "CFGSTcpServerZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        ApplyIfPresent(parameters, "Host", value => Param("Host").Value = ParameterValues.GetString(value, "0.0.0.0") ?? "0.0.0.0");
        ApplyIfPresent(parameters, "Port", value => Param("Port").Value = ParameterValues.GetInt(value, DefaultPort));
        ApplyIfPresent(parameters, "ProtocolVersion", value => Param("ProtocolVersion").Value = ParameterValues.GetString(value, "1.0") ?? "1.0");
        ApplyIfPresent(parameters, "AuthTimeoutSeconds", value => Param("AuthTimeoutSeconds").Value = ParameterValues.GetInt(value, DefaultAuthTimeoutSeconds));
        ApplyIfPresent(parameters, "HeartbeatTimeoutSeconds", value => Param("HeartbeatTimeoutSeconds").Value = ParameterValues.GetInt(value, DefaultHeartbeatTimeoutSeconds));
        ApplyIfPresent(parameters, "MaxUnauthenticatedClients", value => Param("MaxUnauthenticatedClients").Value = ParameterValues.GetInt(value, DefaultMaxUnauthenticatedClients));
        ApplyIfPresent(parameters, "MaxMessageBytes", value => Param("MaxMessageBytes").Value = ParameterValues.GetInt(value, DefaultMaxMessageBytes));
        ApplyIfPresent(parameters, "PendingQueueCapacity", value => Param("PendingQueueCapacity").Value = ParameterValues.GetInt(value, DefaultPendingQueueCapacity));
        ApplyIfPresent(parameters, "PendingQueueTtlSeconds", value => Param("PendingQueueTtlSeconds").Value = ParameterValues.GetInt(value, DefaultPendingQueueTtlSeconds));
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
        await Stop(context, ct).ConfigureAwait(false);

        _host = (ParameterValues.GetString(Param("Host").Value, "0.0.0.0") ?? "0.0.0.0").Trim();
        _port = ReadBoundedInt(context, "Port", DefaultPort, 1, 65535);
        _protocolVersion = (ParameterValues.GetString(Param("ProtocolVersion").Value, "1.0") ?? "1.0").Trim();
        _authTimeoutSeconds = ReadBoundedInt(context, "AuthTimeoutSeconds", DefaultAuthTimeoutSeconds, 1, 300);
        _heartbeatTimeoutSeconds = ReadBoundedInt(context, "HeartbeatTimeoutSeconds", DefaultHeartbeatTimeoutSeconds, 1, 3600);
        _maxUnauthenticatedClients = ReadBoundedInt(context, "MaxUnauthenticatedClients", DefaultMaxUnauthenticatedClients, 1, 1024);
        _maxMessageBytes = ReadBoundedInt(context, "MaxMessageBytes", DefaultMaxMessageBytes, 64, 16 * 1024 * 1024);
        _pendingQueueCapacity = ReadBoundedInt(context, "PendingQueueCapacity", DefaultPendingQueueCapacity, 1, 65536);
        _pendingQueueTtlSeconds = ReadBoundedInt(context, "PendingQueueTtlSeconds", DefaultPendingQueueTtlSeconds, 1, 3600);
        _sendQueueCapacity = ReadBoundedInt(context, "SendQueueCapacity", DefaultSendQueueCapacity, 1, 65536);
        _writeTimeoutSeconds = ReadBoundedInt(context, "WriteTimeoutSeconds", DefaultWriteTimeoutSeconds, 1, 3600);
        _receiveBufferSize = ReadBoundedInt(context, "ReceiveBufferSize", DefaultReceiveBufferSize, 64, 1_048_576);

        context.Logger.LogInformation(
            "CFGS TCP server initialized for {Host}:{Port}; protocol={Version}, authTimeout={AuthTimeout}s, heartbeatTimeout={HeartbeatTimeout}s",
            _host, _port, _protocolVersion, _authTimeoutSeconds, _heartbeatTimeoutSeconds);
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        if (!_configLoaded)
            throw new InvalidOperationException("CFGS TCP server requires input_string_client_config before Run.");

        if (_cts is not null)
            throw new InvalidOperationException("CFGS TCP server is already running.");

        var address = ResolveBindAddress(_host);
        var listener = new TcpListener(address, _port);
        try
        {
            listener.Start();
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            listener.Stop();
            SetFailed();
            context.Logger.LogError(ex, "CFGS TCP server could not bind to {Host}:{Port}.", _host, _port);
            throw new InvalidOperationException($"CFGS TCP server could not bind to {_host}:{_port}.", ex);
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        _sendChannel = CreateSendChannel(_sendQueueCapacity);
        Running = true;

        var token = _cts.Token;
        _acceptTask = Task.Run(() => AcceptLoopAsync(context, listener, token), CancellationToken.None);
        _sendTask = Task.Run(() => SendLoopAsync(context, _sendChannel.Reader, token), CancellationToken.None);
        _maintenanceTask = Task.Run(() => MaintenanceLoopAsync(context, token), CancellationToken.None);

        context.Logger.LogInformation(
            "CFGS TCP server is running on {Host}:{Port}; configured clients={Clients}.",
            _host, _port, _clientDefinitions.Count);
        return Task.CompletedTask;
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        await _stopLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cts = _cts;
            _cts = null;
            cts?.Cancel();

            var listener = _listener;
            _listener = null;
            listener?.Stop();

            var channel = _sendChannel;
            _sendChannel = null;
            channel?.Writer.TryComplete();

            foreach (var session in _sessions.Values.ToArray())
                session.Dispose();

            var tasks = new[] { _acceptTask, _sendTask, _maintenanceTask }
                .Where(task => task is not null)
                .Select(task => task!)
                .ToArray();
            _acceptTask = null;
            _sendTask = null;
            _maintenanceTask = null;

            if (tasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(tasks)
                        .WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    context.Logger.LogWarning("CFGS TCP server loops did not stop within 3s.");
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning(ex, "CFGS TCP server loop ended with an error while stopping.");
                }
            }

            _sessions.Clear();
            _sessionsByGuid.Clear();
            lock (_pendingGate)
                _pendingByGuid.Clear();

            Running = false;
            SetIdle();
            cts?.Dispose();
            context.Logger.LogInformation(
                "CFGS TCP server stopped: accepted={Accepted}, rejected={Rejected}, authenticated={Authenticated}, sent={Sent}, received={Received}, dropped={Dropped}",
                Interlocked.Read(ref _acceptedConnections),
                Interlocked.Read(ref _rejectedConnections),
                Interlocked.Read(ref _authenticatedConnections),
                Interlocked.Read(ref _sentFrames),
                Interlocked.Read(ref _receivedFrames),
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
        if (ReferenceEquals(pin, ConfigInputPin))
        {
            HandleClientConfig(value);
            return;
        }

        if (ReferenceEquals(pin, MessageInputPin))
            HandleOutgoingMessage(value);
    }

    private void HandleClientConfig(Sample? value)
    {
        if (_cts is not null || Running)
        {
            _logger?.LogWarning(
                "CFGS TCP server ignored a client configuration replacement while running; restart the node to apply it.");
            return;
        }

        if (value is not StringSample text || text.StringValue is null)
        {
            _configLoaded = false;
            _logger?.LogError("CFGS TCP server ignored client configuration: expected StringSample.");
            return;
        }

        if (!ProtocolMessage.TryParseClientConfig(text.StringValue, out var config, out var error))
        {
            _configLoaded = false;
            _logger?.LogError("CFGS TCP server rejected client configuration: {Error}.", error);
            return;
        }

        lock (_configGate)
        {
            _clientDefinitions = config.Clients;
            _configLoaded = true;
        }

        _logger?.LogInformation(
            "CFGS TCP server loaded {Count} configured client GUIDs: {Clients}.",
            config.Count,
            string.Join(", ", config.Clients.Values.Select(item => $"{item.Guid}:{item.Location}")));
    }

    private void HandleOutgoingMessage(Sample? value)
    {
        if (value is not StringSample text || text.StringValue is null)
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFGS TCP server ignored outgoing message: expected StringSample.");
            return;
        }

        if (!ProtocolMessage.TryParseObject(text.StringValue, out var envelope, out var error) ||
            !ProtocolMessage.TryValidateEnvelope(envelope, out error))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFGS TCP server rejected outgoing message: {Error}.", error);
            return;
        }

        if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Guid, out var guidText) ||
            !ProtocolMessage.IsGuid(guidText, out var targetGuid))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFGS TCP server rejected outgoing message: GUID is invalid.");
            return;
        }

        if (!TryGetClientDefinition(targetGuid, out var definition))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError(
                "CFGS TCP server rejected outgoing message: target GUID {Guid} is not configured.",
                targetGuid);
            return;
        }

        if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Type, out var type) ||
            string.IsNullOrWhiteSpace(type))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError("CFGS TCP server rejected outgoing message: TYPE is required.");
            return;
        }

        var requiresLocation =
            string.Equals(type, ProtocolConstants.Business, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, ProtocolConstants.Ping, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, ProtocolConstants.Pong, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, ProtocolConstants.AuthOk, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, ProtocolConstants.Disconnect, StringComparison.OrdinalIgnoreCase);
        if (requiresLocation)
        {
            if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Location, out var location) ||
                !ProtocolMessage.IsLocation(location) ||
                !string.Equals(ProtocolMessage.NormalizeLocation(location), definition.Location, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _droppedFrames);
                _logger?.LogError(
                    "CFGS TCP server rejected outgoing {Type} message: LOCATION must match target GUID {Guid} LOCATION {Expected}.",
                    type, targetGuid, definition.Location);
                return;
            }
        }
        else if (ProtocolMessage.TryGetString(envelope, ProtocolConstants.Location, out var optionalLocation) &&
                 !string.IsNullOrWhiteSpace(optionalLocation) &&
                 (!ProtocolMessage.IsLocation(optionalLocation) ||
                  !string.Equals(ProtocolMessage.NormalizeLocation(optionalLocation), definition.Location, StringComparison.Ordinal)))
        {
            Interlocked.Increment(ref _droppedFrames);
            _logger?.LogError(
                "CFGS TCP server rejected outgoing {Type} message: optional LOCATION does not match target GUID {Guid} LOCATION {Expected}.",
                type, targetGuid, definition.Location);
            return;
        }

        if (string.Equals(type, ProtocolConstants.Business, StringComparison.OrdinalIgnoreCase))
        {
            if (ProtocolMessage.Get(envelope, ProtocolConstants.Data) is not JsonObject data)
            {
                Interlocked.Increment(ref _droppedFrames);
                _logger?.LogError(
                    "CFGS TCP server rejected outgoing BUSINESS message: DATA must be a JSON object. Target GUID: {Guid}.",
                    targetGuid);
                return;
            }

            if (!ProtocolMessage.TryValidateBusinessData(data, definition.Location, out error))
            {
                Interlocked.Increment(ref _droppedFrames);
                _logger?.LogError(
                    "CFGS TCP server rejected outgoing BUSINESS message: {Error}. Target GUID: {Guid}.",
                    error, targetGuid);
                return;
            }
        }

        if (_sessionsByGuid.TryGetValue(targetGuid, out var session) && session.IsAuthenticated)
        {
            QueueSend(session, envelope, targetGuid);
            return;
        }

        QueuePending(targetGuid, envelope);
    }

    private bool TryGetClientDefinition(string guid, out CfgClientDefinition definition)
    {
        lock (_configGate)
            return _clientDefinitions.TryGetValue(guid, out definition!);
    }

    private async Task AcceptLoopAsync(INodeContext context, TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var unauthenticated = _sessions.Values.Count(item => !item.IsAuthenticated);
                if (unauthenticated >= _maxUnauthenticatedClients)
                {
                    Interlocked.Increment(ref _rejectedConnections);
                    context.Logger.LogWarning(
                        "CFGS TCP server rejected a connection: unauthenticated limit {Limit} reached.",
                        _maxUnauthenticatedClients);
                    client.Dispose();
                    continue;
                }

                var session = new ClientSession(
                    Interlocked.Increment(ref _nextSessionId),
                    client,
                    client.Client.RemoteEndPoint?.ToString() ?? "?");
                _sessions[session.Id] = session;
                Interlocked.Increment(ref _acceptedConnections);
                session.ReadTask = Task.Run(() => ReadLoopAsync(session, context, ct), CancellationToken.None);
                context.Logger.LogTrace(
                    "CFGS TCP server accepted connection {Id} from {Endpoint}; waiting for AUTH.",
                    session.Id, session.Endpoint);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during Stop.
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            // Expected during Stop.
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "CFGS TCP server accept loop failed.");
            Running = false;
        }
    }

    private async Task ReadLoopAsync(ClientSession session, INodeContext context, CancellationToken ct)
    {
        var accumulator = new CfgFrameAccumulator();
        var buffer = new byte[_receiveBufferSize];
        try
        {
            session.Stream.ReadTimeout = _writeTimeoutSeconds * 1000;
            while (!ct.IsCancellationRequested)
            {
                var count = await session.Stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (count == 0)
                    break;

                if (!accumulator.TryAppend(buffer.AsSpan(0, count), _maxMessageBytes, out var frames, out var error))
                {
                    Interlocked.Increment(ref _droppedFrames);
                    context.Logger.LogError(
                        "CFGS TCP server rejected a frame from session {Id}: {Error}.",
                        session.Id, error);
                    break;
                }

                foreach (var frame in frames)
                {
                    Interlocked.Increment(ref _receivedFrames);
                    var keepOpen = await ProcessFrameAsync(session, frame, context, ct).ConfigureAwait(false);
                    if (!keepOpen)
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during Stop.
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            // Expected during Stop.
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                "CFGS TCP server session {Id} read loop ended: {Message}",
                session.Id, ex.Message);
        }
        finally
        {
            DisconnectSession(session, context);
        }
    }

    private async Task<bool> ProcessFrameAsync(
        ClientSession session,
        string frame,
        INodeContext context,
        CancellationToken ct)
    {
        if (!ProtocolMessage.TryParseObject(frame, out var envelope, out var error) ||
            !ProtocolMessage.TryValidateEnvelope(envelope, out error))
        {
            Interlocked.Increment(ref _droppedFrames);
            context.Logger.LogError(
                "CFGS TCP server rejected envelope from session {Id}: {Error}.",
                session.Id, error);
            return false;
        }

        session.LastSeenUtc = DateTimeOffset.UtcNow;

        if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Version, out var version) ||
            !string.Equals(version, _protocolVersion, StringComparison.Ordinal))
        {
            context.Logger.LogError(
                "CFGS TCP server rejected session {Id}: VERSION {Version} does not match {Expected}.",
                session.Id, version, _protocolVersion);
            await TrySendFailureAsync(session, "VERSION_MISMATCH", ct).ConfigureAwait(false);
            return false;
        }

        if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Type, out var type))
            return false;

        if (string.Equals(type, ProtocolConstants.Auth, StringComparison.OrdinalIgnoreCase))
        {
            return await AuthenticateAsync(session, envelope, context, ct).ConfigureAwait(false);
        }

        if (!session.IsAuthenticated || session.Guid is null)
        {
            context.Logger.LogError(
                "CFGS TCP server rejected session {Id} message before AUTH.", session.Id);
            await TrySendFailureAsync(session, "AUTH_REQUIRED", ct).ConfigureAwait(false);
            return false;
        }

        if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Guid, out var guidText) ||
            !ProtocolMessage.IsGuid(guidText, out var sourceGuid) ||
            !string.Equals(sourceGuid, session.Guid, StringComparison.Ordinal))
        {
            context.Logger.LogError(
                "CFGS TCP server rejected session {Id}: envelope GUID does not match authenticated GUID.",
                session.Id);
            return false;
        }

        var authenticatedLocation = session.Location;
        var location = string.Empty;
        if (string.IsNullOrWhiteSpace(authenticatedLocation) ||
            !ProtocolMessage.TryGetString(envelope, ProtocolConstants.Location, out location) ||
            !string.Equals(
                ProtocolMessage.NormalizeLocation(location),
                authenticatedLocation,
                StringComparison.Ordinal))
        {
            context.Logger.LogError(
                "CFGS TCP server rejected session {Id}: LOCATION {Location} does not match authenticated LOCATION {Expected}.",
                session.Id,
                location,
                authenticatedLocation);
            return false;
        }

        if (string.Equals(type, ProtocolConstants.Ping, StringComparison.OrdinalIgnoreCase))
        {
            var pong = ProtocolMessage.CreateEnvelope(
                session.Guid,
                authenticatedLocation,
                ProtocolConstants.Pong,
                _protocolVersion,
                1,
                ProtocolMessage.FormatTimestamp(DateTimeOffset.UtcNow),
                new JsonObject
                {
                    ["ROLE"] = authenticatedLocation,
                    [ProtocolConstants.Location] = authenticatedLocation,
                });
            await SendEnvelopeAsync(session, pong, ct).ConfigureAwait(false);
            return true;
        }

        if (IsControlEnvelopeType(type))
        {
            _logger?.LogTrace(
                "CFGS TCP server ignored control envelope TYPE {Type} from session {Id}.",
                type,
                session.Id);
            return true;
        }

        PublishMessageEnvelope(frame, type, session);
        return !string.Equals(type, ProtocolConstants.Disconnect, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> AuthenticateAsync(
        ClientSession session,
        JsonObject envelope,
        INodeContext context,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, CfgClientDefinition> definitions;
        lock (_configGate)
            definitions = _clientDefinitions;

        if (!_configLoaded)
        {
            await TrySendFailureAsync(session, "CONFIG_NOT_READY", ct).ConfigureAwait(false);
            return false;
        }

        if (!ProtocolMessage.TryGetString(envelope, ProtocolConstants.Guid, out var guidText) ||
            !ProtocolMessage.IsGuid(guidText, out var guid) ||
            !definitions.TryGetValue(guid, out var definition))
        {
            Interlocked.Increment(ref _rejectedConnections);
            context.Logger.LogError(
                "CFGS TCP server rejected AUTH from session {Id}: GUID {Guid} is not configured.",
                session.Id, guidText);
            await TrySendFailureAsync(session, "GUID_NOT_ALLOWED", ct).ConfigureAwait(false);
            return false;
        }

        if (!_sessionsByGuid.TryAdd(guid, session))
        {
            Interlocked.Increment(ref _rejectedConnections);
            context.Logger.LogError(
                "CFGS TCP server rejected duplicate GUID {Guid} from session {Id}; existing session was kept.",
                guid, session.Id);
            await TrySendFailureAsync(session, "GUID_ALREADY_CONNECTED", ct).ConfigureAwait(false);
            return false;
        }

        session.Guid = guid;
        session.Location = definition.Location;
        session.IsAuthenticated = true;
        session.LastSeenUtc = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _authenticatedConnections);

        var authOk = ProtocolMessage.CreateEnvelope(
            guid,
            definition.Location,
            ProtocolConstants.AuthOk,
            _protocolVersion,
            1,
            ProtocolMessage.FormatTimestamp(DateTimeOffset.UtcNow),
            new JsonObject
            {
                ["ROLE"] = definition.Location,
                [ProtocolConstants.Location] = definition.Location,
            });
        await SendEnvelopeAsync(session, authOk, ct).ConfigureAwait(false);
        await PublishStatusAsync(session, CFGSTcpProtocolConnectionStates.AuthenticatedClient, context)
            .ConfigureAwait(false);

        context.Logger.LogInformation(
            "CFGS TCP server authenticated session {Id}: GUID={Guid}, LOCATION={Location}, endpoint={Endpoint}.",
            session.Id, guid, definition.Location, session.Endpoint);

        DrainPending(guid, session);
        return true;
    }

    private async Task TrySendFailureAsync(ClientSession session, string reason, CancellationToken ct)
    {
        try
        {
            var failure = ProtocolMessage.CreateEnvelope(
                session.Guid ?? "00000000-0000-0000-0000-000000000000",
                session.Location ?? string.Empty,
                ProtocolConstants.AuthFail,
                _protocolVersion,
                1,
                ProtocolMessage.FormatTimestamp(DateTimeOffset.UtcNow),
                new JsonObject { ["REASON"] = reason });
            await SendEnvelopeAsync(session, failure, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException)
        {
            _logger?.LogTrace("CFGS TCP server could not send AUTH_FAIL to session {Id}: {Message}", session.Id, ex.Message);
        }
    }

    private async Task SendLoopAsync(
        INodeContext context,
        ChannelReader<SendRequest> reader,
        CancellationToken ct)
    {
        try
        {
            await foreach (var request in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var session = request.Session;
                if (!session.IsAuthenticated || session.Guid is null)
                {
                    QueuePending(request.TargetGuid, request.Envelope);
                    continue;
                }

                try
                {
                    await SendEnvelopeAsync(session, request.Envelope, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _sentFrames);
                    context.Logger.LogTrace(
                        "CFGS TCP server routed {TargetGuid} SEQ={Sequence} to session {Id}.",
                        request.TargetGuid,
                        ProtocolMessage.Get(request.Envelope, ProtocolConstants.Sequence)?.ToJsonString() ?? "?",
                        session.Id);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException)
                {
                    Interlocked.Increment(ref _droppedFrames);
                    QueuePending(request.TargetGuid, request.Envelope);
                    DisconnectSession(session, context);
                    context.Logger.LogWarning(
                        "CFGS TCP server could not send to {Guid}; frame was kept for reconnect: {Message}",
                        request.TargetGuid, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during Stop.
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            // Expected during Stop.
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "CFGS TCP server send loop failed.");
            Running = false;
        }
    }

    private async Task SendEnvelopeAsync(ClientSession session, JsonObject envelope, CancellationToken ct)
    {
        var bytes = ProtocolMessage.EncodeFrame(envelope);
        await session.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var writeTask = session.Stream.WriteAsync(bytes.AsMemory(), ct).AsTask();
            await writeTask.WaitAsync(TimeSpan.FromSeconds(_writeTimeoutSeconds), ct).ConfigureAwait(false);
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    private void QueueSend(ClientSession session, JsonObject envelope, string targetGuid)
    {
        var channel = _sendChannel;
        if (channel is null || !channel.Writer.TryWrite(new SendRequest(session, targetGuid, envelope.DeepClone().AsObject())))
        {
            Interlocked.Increment(ref _droppedFrames);
            QueuePending(targetGuid, envelope);
            _logger?.LogError(
                "CFGS TCP server send queue is full or unavailable; target {Guid} was moved to the pending queue.",
                targetGuid);
        }
    }

    private void QueuePending(string targetGuid, JsonObject envelope)
    {
        var pending = new PendingEnvelope(envelope.DeepClone().AsObject(), DateTimeOffset.UtcNow.AddSeconds(_pendingQueueTtlSeconds));
        lock (_pendingGate)
        {
            if (!_pendingByGuid.TryGetValue(targetGuid, out var queue))
            {
                queue = new LinkedList<PendingEnvelope>();
                _pendingByGuid[targetGuid] = queue;
            }

            while (queue.Count >= _pendingQueueCapacity)
                queue.RemoveFirst();

            queue.AddLast(pending);
        }

        _logger?.LogError(
            "CFGS TCP server target GUID {Guid} is offline; BUSINESS frame queued for reconnect.",
            targetGuid);
    }

    private void DrainPending(string guid, ClientSession session)
    {
        LinkedList<PendingEnvelope>? pending = null;
        lock (_pendingGate)
        {
            if (_pendingByGuid.Remove(guid, out var queue))
                pending = queue;
        }

        if (pending is null)
            return;

        foreach (var item in pending)
        {
            if (item.ExpiresUtc <= DateTimeOffset.UtcNow)
                continue;
            QueueSend(session, item.Envelope, guid);
        }

        if (pending.Count > 0)
        {
            _logger?.LogInformation(
                "CFGS TCP server queued {Count} pending frames for reconnect of {Guid}.",
                pending.Count, guid);
        }
    }

    private async Task MaintenanceLoopAsync(INodeContext context, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var session in _sessions.Values.ToArray())
                {
                    var timeoutSeconds = session.IsAuthenticated ? _heartbeatTimeoutSeconds : _authTimeoutSeconds;
                    if ((now - session.LastSeenUtc).TotalSeconds <= timeoutSeconds)
                        continue;

                    context.Logger.LogWarning(
                        "CFGS TCP server session {Id} timed out after {Seconds}s ({State}).",
                        session.Id, timeoutSeconds, session.IsAuthenticated ? "authenticated" : "unauthenticated");
                    DisconnectSession(session, context);
                }

                lock (_pendingGate)
                {
                    foreach (var pair in _pendingByGuid.ToArray())
                    {
                        var queue = pair.Value;
                        while (queue.First is { } first && first.Value.ExpiresUtc <= now)
                            queue.RemoveFirst();
                        if (queue.Count == 0)
                            _pendingByGuid.Remove(pair.Key);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during Stop.
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "CFGS TCP server maintenance loop failed.");
        }
    }

    private void PublishMessageEnvelope(string frame, string type, ClientSession session)
    {
        var sample = StringSample.CreateSample();
        sample.SetValue(frame, MessageOutputPin.Name);
        MessageOutputPin.SendSampleWithDispatchLog(
            sample,
            $"{type} from {session.Guid}",
            LogLevel.Information);
        session.LastSeenUtc = DateTimeOffset.UtcNow;
    }

    private static bool IsControlEnvelopeType(string type) =>
        string.Equals(type, ProtocolConstants.Auth, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ProtocolConstants.AuthOk, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ProtocolConstants.AuthFail, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ProtocolConstants.Ping, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ProtocolConstants.Pong, StringComparison.OrdinalIgnoreCase);

    private async Task PublishStatusAsync(ClientSession session, string state, INodeContext context)
    {
        var samples = new Dictionary<string, Sample>(StringComparer.Ordinal)
        {
            [CFGSTcpProtocolStatusKeys.GUID] = CreateStringSample(CFGSTcpProtocolStatusKeys.GUID, session.Guid ?? string.Empty),
            [CFGSTcpProtocolStatusKeys.ROLE] = CreateStringSample(CFGSTcpProtocolStatusKeys.ROLE, session.Location ?? string.Empty),
            [CFGSTcpProtocolStatusKeys.STATE] = CreateStringSample(CFGSTcpProtocolStatusKeys.STATE, state),
            [CFGSTcpProtocolStatusKeys.ENDPOINT] = CreateStringSample(CFGSTcpProtocolStatusKeys.ENDPOINT, session.Endpoint),
            [CFGSTcpProtocolStatusKeys.LAST_SEEN_UTC] = CreateStringSample(
                CFGSTcpProtocolStatusKeys.LAST_SEEN_UTC,
                ProtocolMessage.FormatTimestamp(session.LastSeenUtc)),
            [CFGSTcpProtocolStatusKeys.PENDING_SEND_COUNT] = CreateStringSample(
                CFGSTcpProtocolStatusKeys.PENDING_SEND_COUNT,
                PendingCount(session.Guid)),
        };

        try
        {
            await (_topicBus ?? TopicBus.Instance).PublishAsync(
                CFGSTcpProtocolBroadcasts.CFGSServerConnectionStatus,
                BroadcastTopicMessage.FromSampleDictionary(samples),
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                ex,
                "CFGS TCP server failed to publish connection status for {Guid}: {Message}",
                session.Guid, ex.Message);
        }
    }

    private string PendingCount(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return "0";
        lock (_pendingGate)
            return _pendingByGuid.TryGetValue(guid, out var queue) ? queue.Count.ToString() : "0";
    }

    private void DisconnectSession(ClientSession session, INodeContext context)
    {
        if (!session.TryMarkDisconnected())
            return;

        _sessions.TryRemove(session.Id, out _);

        if (session.Guid is not null)
            _sessionsByGuid.TryRemove(new KeyValuePair<string, ClientSession>(session.Guid, session));

        session.Dispose();
        if (!session.IsAuthenticated || session.Guid is null)
            return;

        context.Logger.LogInformation(
            "CFGS TCP server disconnected session {Id}: GUID={Guid}.", session.Id, session.Guid);
        _ = PublishStatusAsync(session, CFGSTcpProtocolConnectionStates.Disconnected, context);
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    private int ReadBoundedInt(INodeContext context, string parameterName, int fallback, int min, int max)
    {
        var value = ParameterValues.GetInt(Param(parameterName).Value, fallback);
        if (value < min || value > max)
        {
            context.Logger.LogWarning(
                "CFGS TCP server parameter {Name}={Value} is outside [{Min}, {Max}]; clamping.",
                parameterName, value, min, max);
            value = Math.Clamp(value, min, max);
        }

        return value;
    }

    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host == "*" || host == "0.0.0.0")
            return IPAddress.Any;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (IPAddress.TryParse(host, out var address))
            return address;
        throw new InvalidOperationException(
            $"CFGS TCP server Host '{host}' is not a local IP address. Use 0.0.0.0, 127.0.0.1 or a local IP.");
    }

    private static Channel<SendRequest> CreateSendChannel(int capacity) =>
        Channel.CreateBounded<SendRequest>(new BoundedChannelOptions(capacity)
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

    private sealed record PendingEnvelope(JsonObject Envelope, DateTimeOffset ExpiresUtc);

    private sealed record SendRequest(ClientSession Session, string TargetGuid, JsonObject Envelope);

    private sealed class ClientSession : IDisposable
    {
        private int _disposed;
        private int _disconnected;

        public ClientSession(int id, TcpClient client, string endpoint)
        {
            Id = id;
            Client = client;
            Endpoint = endpoint;
            Stream = client.GetStream();
            LastSeenUtc = DateTimeOffset.UtcNow;
        }

        public int Id { get; }
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public string Endpoint { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public DateTimeOffset LastSeenUtc { get; set; }
        public string? Guid { get; set; }
        public string? Location { get; set; }
        public bool IsAuthenticated { get; set; }
        public Task? ReadTask { get; set; }

        public bool TryMarkDisconnected() => Interlocked.Exchange(ref _disconnected, 1) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                Client.Dispose();
            }
            catch
            {
                // Socket disposal is best effort during shutdown.
            }
        }
    }
}

