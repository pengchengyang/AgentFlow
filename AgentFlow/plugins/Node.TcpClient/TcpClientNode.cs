// -----------------------------------------------------------------------
// <copyright company="Your Company" file="TcpClientNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;
using TcpSocket = System.Net.Sockets.TcpClient;

namespace Node.TcpClient;

/// <summary>
/// Raw TCP client node. It connects to a remote TCP endpoint and sends exactly the bytes
/// supplied by its input pins. It does not add framing, delimiters, protocol headers, or
/// automatic payloads. Each successful network read is published as one binary sample and
/// optionally decoded as a UTF-8 debug string.
/// </summary>
public sealed class TcpClientNode : BaseNode
{
    private const int DefaultQueueCapacity = 256;
    private const int DefaultConnectTimeoutSeconds = 5;
    private const int DefaultMaxConnectAttempts = 5;
    private const int DefaultReconnectDelaySeconds = 1;
    private const int DefaultReconnectMaxDelaySeconds = 30;
    private const int DefaultReadTimeoutSeconds = 0;
    private const int DefaultWriteTimeoutSeconds = 10;
    private const int DefaultReceiveBufferSize = 4096;
    private const string ImmediateConnectMode = "Immediate";
    private const string WaitForEndpointConnectMode = "WaitForEndpoint";
    private static readonly string[] ConnectModeOptions = { ImmediateConnectMode, WaitForEndpointConnectMode };

    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private readonly object _connectionGate = new();
    private readonly object _supervisorStartGate = new();
    private TcpSocket? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _supervisorTask;
    private Task? _readTask;
    private Task? _writeTask;
    private Channel<byte[]>? _sendChannel;
    private Channel<bool>? _reconnectRequests;
    private ILogger? _logger;
    private INodeContext? _context;

    private string _host = "127.0.0.1";
    private int _port = 9000;
    private string _connectMode = ImmediateConnectMode;
    private volatile bool _endpointInputReceived;
    private int _connectTimeoutSeconds = DefaultConnectTimeoutSeconds;
    private int _maxConnectAttempts = DefaultMaxConnectAttempts;
    private bool _autoReconnect = true;
    private int _reconnectDelaySeconds = DefaultReconnectDelaySeconds;
    private int _reconnectMaxDelaySeconds = DefaultReconnectMaxDelaySeconds;
    private int _sendQueueCapacity = DefaultQueueCapacity;
    private int _readTimeoutSeconds = DefaultReadTimeoutSeconds;
    private int _writeTimeoutSeconds = DefaultWriteTimeoutSeconds;
    private int _receiveBufferSize = DefaultReceiveBufferSize;

    private long _sentBlocks;
    private long _receivedBlocks;
    private long _droppedBlocks;

    public TcpClientNode()
    {
        Uuid = "DCB15DED-9BDF-4C44-858F-560261B685D7";
        AddParam();
        AddPins();
    }

    public override string Name => "TCP Client";
    public override string DisplayName { get; set; } = "TCP Client";
    public override string Category => "Network";

    /// <summary>
    /// Runtime endpoint input in host:port format. It changes only the active connection target;
    /// it does not modify persisted Host or Port parameters.
    /// </summary>
    public BasePin InputEndpointPin { get; } =
        new("input_string_endpoint", DataType.STRING, PinDirection.Input, required: false);

    /// <summary>UTF-8 text input. The text is encoded as bytes before it is sent.</summary>
    public BasePin InputStringPin { get; } =
        new("input_string", DataType.STRING, PinDirection.Input, required: false);

    /// <summary>Raw byte input. The byte array is sent unchanged.</summary>
    public BasePin InputBinaryPin { get; } =
        new("input_binary", DataType.BIN, PinDirection.Input, required: false);

    /// <summary>UTF-8 debug view of each received byte block.</summary>
    public BasePin OutputStringPin { get; } =
        new("output_string", DataType.STRING, PinDirection.Output);

    /// <summary>Raw received byte block for downstream processing.</summary>
    public BasePin OutputBinaryPin { get; } =
        new("output_binary", DataType.BIN, PinDirection.Output);

    public override void AddPins()
    {
        AddInputPin(InputEndpointPin);
        AddInputPin(InputStringPin);
        AddInputPin(InputBinaryPin);
        AddOutputPin(OutputStringPin);
        AddOutputPin(OutputBinaryPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter("Host", typeof(string), "127.0.0.1",
            isEditable: true, group: "Connection", zone: "TcpClientZone"));
        AddParameter(new NodeParameter("Port", typeof(int), 9000,
            isEditable: true, group: "Connection", zone: "TcpClientZone"));
        AddParameter(new NodeParameter(
            "ConnectMode",
            typeof(string),
            ImmediateConnectMode,
            isEditable: true,
            group: "Connection",
            zone: "TcpClientZone",
            editor: ParameterEditorKind.SingleSelect,
            options: ConnectModeOptions));
        AddParameter(new NodeParameter("ConnectTimeoutSeconds", typeof(int), DefaultConnectTimeoutSeconds,
            isEditable: true, group: "Connection", zone: "TcpClientZone"));
        AddParameter(new NodeParameter("MaxConnectAttempts", typeof(int), DefaultMaxConnectAttempts,
            isEditable: true, group: "Connection", zone: "TcpClientZone"));
        AddParameter(new NodeParameter("AutoReconnect", typeof(bool), true,
            isEditable: true, group: "Connection", zone: "TcpClientZone"));
        AddParameter(new NodeParameter("ReconnectDelaySeconds", typeof(int), DefaultReconnectDelaySeconds,
            isEditable: true, group: "Connection", zone: "TcpClientZone"));
        AddParameter(new NodeParameter("ReconnectMaxDelaySeconds", typeof(int), DefaultReconnectMaxDelaySeconds,
            isEditable: true, group: "Connection", zone: "TcpClientZone"));
        AddParameter(new NodeParameter("SendQueueCapacity", typeof(int), DefaultQueueCapacity,
            isEditable: true, group: "Runtime", zone: "TcpClientZone"));
        AddParameter(new NodeParameter("ReadTimeoutSeconds", typeof(int), DefaultReadTimeoutSeconds,
            isEditable: true, group: "Runtime", zone: "TcpClientZone"));
        AddParameter(new NodeParameter("WriteTimeoutSeconds", typeof(int), DefaultWriteTimeoutSeconds,
            isEditable: true, group: "Runtime", zone: "TcpClientZone"));
        AddParameter(new NodeParameter("ReceiveBufferSize", typeof(int), DefaultReceiveBufferSize,
            isEditable: true, group: "Runtime", zone: "TcpClientZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Host", out var host) && host is not null)
            Param("Host").Value = ParameterValues.GetString(host, "127.0.0.1") ?? "127.0.0.1";
        if (parameters.TryGetValue("Port", out var port) && port is not null)
            Param("Port").Value = ParameterValues.GetInt(port, 9000);
        if (parameters.TryGetValue("ConnectMode", out var connectMode) && connectMode is not null)
            Param("ConnectMode").Value = ParameterValues.GetString(connectMode, ImmediateConnectMode) ?? ImmediateConnectMode;
        if (parameters.TryGetValue("ConnectTimeoutSeconds", out var connectTimeout) && connectTimeout is not null)
            Param("ConnectTimeoutSeconds").Value = ParameterValues.GetInt(connectTimeout, DefaultConnectTimeoutSeconds);
        if (parameters.TryGetValue("MaxConnectAttempts", out var attempts) && attempts is not null)
            Param("MaxConnectAttempts").Value = ParameterValues.GetInt(attempts, DefaultMaxConnectAttempts);
        if (parameters.TryGetValue("AutoReconnect", out var autoReconnect) && autoReconnect is not null)
            Param("AutoReconnect").Value = ParameterValues.GetBool(autoReconnect, true);
        if (parameters.TryGetValue("ReconnectDelaySeconds", out var reconnectDelay) && reconnectDelay is not null)
            Param("ReconnectDelaySeconds").Value = ParameterValues.GetInt(reconnectDelay, DefaultReconnectDelaySeconds);
        if (parameters.TryGetValue("ReconnectMaxDelaySeconds", out var reconnectMaxDelay) && reconnectMaxDelay is not null)
            Param("ReconnectMaxDelaySeconds").Value = ParameterValues.GetInt(reconnectMaxDelay, DefaultReconnectMaxDelaySeconds);
        if (parameters.TryGetValue("SendQueueCapacity", out var queueCapacity) && queueCapacity is not null)
            Param("SendQueueCapacity").Value = ParameterValues.GetInt(queueCapacity, DefaultQueueCapacity);
        if (parameters.TryGetValue("ReadTimeoutSeconds", out var readTimeout) && readTimeout is not null)
            Param("ReadTimeoutSeconds").Value = ParameterValues.GetInt(readTimeout, DefaultReadTimeoutSeconds);
        if (parameters.TryGetValue("WriteTimeoutSeconds", out var writeTimeout) && writeTimeout is not null)
            Param("WriteTimeoutSeconds").Value = ParameterValues.GetInt(writeTimeout, DefaultWriteTimeoutSeconds);
        if (parameters.TryGetValue("ReceiveBufferSize", out var receiveBuffer) && receiveBuffer is not null)
            Param("ReceiveBufferSize").Value = ParameterValues.GetInt(receiveBuffer, DefaultReceiveBufferSize);
    }

    public override async Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        await Stop(context, ct).ConfigureAwait(false);
        _context = context;
        _connectMode = NormalizeConnectMode(context);
        _endpointInputReceived = false;

        var configuredHost = (ParameterValues.GetString(Param("Host").Value, "127.0.0.1") ?? "127.0.0.1").Trim();
        if (configuredHost.Length == 0)
            throw new InvalidOperationException("TCP client Host must not be empty.");

        var configuredPort = ReadBoundedInt(context, "Port", 9000, 1, 65535);
        lock (_connectionGate)
        {
            _host = configuredHost;
            _port = configuredPort;
        }
        _connectTimeoutSeconds = ReadBoundedInt(context, "ConnectTimeoutSeconds", DefaultConnectTimeoutSeconds, 1, 60);
        _maxConnectAttempts = ReadBoundedInt(context, "MaxConnectAttempts", DefaultMaxConnectAttempts, 1, 100);
        _autoReconnect = ParameterValues.GetBool(Param("AutoReconnect").Value, true);
        _reconnectDelaySeconds = ReadBoundedInt(context, "ReconnectDelaySeconds", DefaultReconnectDelaySeconds, 1, 60);
        _reconnectMaxDelaySeconds = ReadBoundedInt(context, "ReconnectMaxDelaySeconds", DefaultReconnectMaxDelaySeconds, 1, 3600);
        _reconnectMaxDelaySeconds = Math.Max(_reconnectMaxDelaySeconds, _reconnectDelaySeconds);
        _sendQueueCapacity = ReadBoundedInt(context, "SendQueueCapacity", DefaultQueueCapacity, 1, 65536);
        _readTimeoutSeconds = ReadBoundedInt(context, "ReadTimeoutSeconds", DefaultReadTimeoutSeconds, 0, 3600);
        _writeTimeoutSeconds = ReadBoundedInt(context, "WriteTimeoutSeconds", DefaultWriteTimeoutSeconds, 1, 3600);
        _receiveBufferSize = ReadBoundedInt(context, "ReceiveBufferSize", DefaultReceiveBufferSize, 64, 1_048_576);

        _cts = new CancellationTokenSource();
        _sendChannel = CreateSendChannel(_sendQueueCapacity);
        _reconnectRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        SetIdle();

        var endpoint = GetEndpoint();
        context.Logger.LogInformation(
            "TCP client initialized for {Host}:{Port}; connectMode={ConnectMode}, queue={Queue}, autoReconnect={AutoReconnect}, reconnectDelay={ReconnectDelay}s, reconnectMaxDelay={ReconnectMaxDelay}s, readTimeout={ReadTimeout}s, writeTimeout={WriteTimeout}s",
            endpoint.Host, endpoint.Port, _connectMode, _sendQueueCapacity, _autoReconnect, _reconnectDelaySeconds,
            _reconnectMaxDelaySeconds, _readTimeoutSeconds, _writeTimeoutSeconds);

        if (_connectMode == WaitForEndpointConnectMode && InputEndpointPin.ConnectedPin is null)
        {
            context.Logger.LogWarning(
                "TCP client ConnectMode=WaitForEndpoint but input_string_endpoint is not connected; Run will wait indefinitely for an endpoint input.");
        }
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        var rootCts = _cts;
        var reconnectRequests = _reconnectRequests;
        if (rootCts is null || _sendChannel is null || reconnectRequests is null)
            throw new InvalidOperationException("TCP client must be initialized before Run.");
        if (_supervisorTask is not null)
            throw new InvalidOperationException("TCP client is already running.");

        Running = true;

        if (_connectMode == WaitForEndpointConnectMode && !_endpointInputReceived)
        {
            var endpoint = GetEndpoint();
            context.Logger.LogInformation(
                "TCP client is waiting for the first input_string_endpoint value before connecting; configured fallback is {Host}:{Port}.",
                endpoint.Host, endpoint.Port);
            return Task.CompletedTask;
        }

        TryStartConnectionSupervisor(context);
        return Task.CompletedTask;
    }

    private string NormalizeConnectMode(INodeContext context)
    {
        var configured = (ParameterValues.GetString(Param("ConnectMode").Value, ImmediateConnectMode) ?? ImmediateConnectMode).Trim();
        if (string.Equals(configured, ImmediateConnectMode, StringComparison.OrdinalIgnoreCase))
            return ImmediateConnectMode;
        if (string.Equals(configured, WaitForEndpointConnectMode, StringComparison.OrdinalIgnoreCase))
            return WaitForEndpointConnectMode;

        context.Logger.LogWarning(
            "TCP client parameter ConnectMode={ConnectMode} is invalid; using {Fallback}.",
            configured, ImmediateConnectMode);
        Param("ConnectMode").Value = ImmediateConnectMode;
        return ImmediateConnectMode;
    }

    private bool TryStartConnectionSupervisor(INodeContext context)
    {
        lock (_supervisorStartGate)
        {
            if (!Running || _supervisorTask is not null)
                return false;

            var rootCts = _cts;
            var reconnectRequests = _reconnectRequests;
            if (rootCts is null || _sendChannel is null || reconnectRequests is null)
                throw new InvalidOperationException("TCP client must be initialized before starting its connection supervisor.");

            _supervisorTask = Task.Run(
                () => ConnectionSupervisorAsync(context, rootCts.Token, reconnectRequests.Reader),
                CancellationToken.None);
            var endpoint = GetEndpoint();
            context.Logger.LogInformation(
                "TCP client supervisor started for {Host}:{Port}; connectMode={ConnectMode}, autoReconnect={AutoReconnect}.",
                endpoint.Host, endpoint.Port, _connectMode, _autoReconnect);
            return true;
        }
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        await _stopLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationTokenSource? cts;
            Channel<byte[]>? channel;
            Channel<bool>? reconnectRequests;
            Task? supervisorTask;
            lock (_supervisorStartGate)
            {
                cts = _cts;
                _cts = null;
                Running = false;
                channel = _sendChannel;
                _sendChannel = null;
                reconnectRequests = _reconnectRequests;
                _reconnectRequests = null;
                supervisorTask = _supervisorTask;
                _supervisorTask = null;
            }

            cts?.Cancel();
            channel?.Writer.TryComplete();
            reconnectRequests?.Writer.TryComplete();

            DisposeCurrentConnection();
            var supervisorStopped = true;
            if (supervisorTask is not null)
            {
                try
                {
                    await supervisorTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    supervisorStopped = false;
                    context.Logger.LogWarning("TCP client supervisor did not stop within 3s.");
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning(ex, "TCP client supervisor ended with an error while stopping.");
                }
            }

            DisposeCurrentConnection();
            _readTask = null;
            _writeTask = null;

            if (supervisorStopped)
                cts?.Dispose();

            _endpointInputReceived = false;
            SetIdle();
            context.Logger.LogInformation(
                "TCP client stopped: sent={Sent}, received={Received}, dropped={Dropped}",
                Interlocked.Read(ref _sentBlocks),
                Interlocked.Read(ref _receivedBlocks),
                Interlocked.Read(ref _droppedBlocks));
        }
        finally
        {
            _stopLock.Release();
        }
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, InputEndpointPin))
        {
            HandleEndpointInput(pin, value);
            return;
        }

        if (ReferenceEquals(pin, InputStringPin) || ReferenceEquals(pin, InputBinaryPin))
        {
            if (value is null)
            {
                Interlocked.Increment(ref _droppedBlocks);
                _logger?.LogError("TCP client ignored a null value on {Pin}.", pin.Name);
                return;
            }

            if (value.Type != pin.DataType)
            {
                Interlocked.Increment(ref _droppedBlocks);
                _logger?.LogError(
                    "TCP client rejected {ActualType} on {Pin}; expected {ExpectedType}.",
                    value.Type, pin.Name, pin.DataType);
                return;
            }

            byte[] bytes;
            if (ReferenceEquals(pin, InputStringPin))
            {
                if (value is not StringSample text || text.StringValue is null)
                {
                    Interlocked.Increment(ref _droppedBlocks);
                    _logger?.LogError("TCP client ignored a null string on {Pin}.", pin.Name);
                    return;
                }

                bytes = Encoding.UTF8.GetBytes(text.StringValue);
            }
            else
            {
                if (value is not BinSample binary || binary.BinValue is null)
                {
                    Interlocked.Increment(ref _droppedBlocks);
                    _logger?.LogError("TCP client ignored a null binary value on {Pin}.", pin.Name);
                    return;
                }

                bytes = binary.BinValue.ToArray();
            }

            QueueSend(bytes, pin.Name);

        }
    }

    private void HandleEndpointInput(BasePin pin, Sample? value)
    {
        if (value is null)
        {
            _logger?.LogError("TCP client ignored a null endpoint value on {Pin}.", pin.Name);
            return;
        }

        if (value.Type != pin.DataType)
        {
            _logger?.LogError(
                "TCP client rejected {ActualType} on {Pin}; expected {ExpectedType}.",
                value.Type, pin.Name, pin.DataType);
            return;
        }

        if (value is not StringSample { StringValue: { } inputText })
        {
            _logger?.LogError(
                "TCP client ignored an endpoint update on {Pin} because the value is not a non-null StringSample.",
                pin.Name);
            return;
        }

        if (!TryParseEndpoint(inputText, out var host, out var port))
        {
            _logger?.LogError(
                "TCP client ignored invalid endpoint {Endpoint}; expected host:port.",
                inputText);
            return;
        }

        var firstEndpoint = !_endpointInputReceived;
        var changed = false;
        lock (_connectionGate)
        {
            if (!string.Equals(_host, host, StringComparison.OrdinalIgnoreCase) || _port != port)
            {
                _host = host;
                _port = port;
                changed = true;
            }
        }

        _endpointInputReceived = true;

        if (changed)
        {
            _logger?.LogInformation(
                "TCP client runtime endpoint changed to {Host}:{Port}; persisted Host and Port parameters are unchanged.",
                host, port);
        }
        else if (firstEndpoint)
        {
            _logger?.LogInformation(
                "TCP client received the first runtime endpoint {Host}:{Port}.", host, port);
        }

        if (!Running)
        {
            _logger?.LogInformation(
                "TCP client cached runtime endpoint {Host}:{Port}; it will connect when Run starts.",
                host, port);
            return;
        }

        var context = _context ?? pin.Context;
        if (context is null)
        {
            _logger?.LogError(
                "TCP client could not start its connection supervisor after receiving endpoint {Host}:{Port} because no node context is available.",
                host, port);
            return;
        }

        if (TryStartConnectionSupervisor(context))
        {
            _logger?.LogInformation(
                "TCP client started its connection supervisor after receiving runtime endpoint {Host}:{Port}.",
                host, port);
            return;
        }

        if (changed)
            RequestReconnect();
        else
            _logger?.LogTrace(
                "TCP client dynamic endpoint is already {Host}:{Port} and a connection supervisor is already running.",
                host, port);
    }

    private void RequestReconnect()
    {
        var reconnectRequests = _reconnectRequests;
        if (reconnectRequests is null || !reconnectRequests.Writer.TryWrite(true))
        {
            _logger?.LogError(
                "TCP client could not schedule a reconnect after the runtime endpoint changed.");
        }
        else
        {
            _logger?.LogTrace("TCP client scheduled a reconnect after the runtime endpoint changed.");
        }
    }

    private (string Host, int Port) GetEndpoint()
    {
        lock (_connectionGate)
            return (_host, _port);
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

    private async Task ConnectionSupervisorAsync(
        INodeContext context,
        CancellationToken ct,
        ChannelReader<bool> reconnectRequests)
    {
        var reconnectAttempt = 0;
        while (!ct.IsCancellationRequested)
        {
            while (reconnectRequests.TryRead(out _))
            {
                // Discard stale wake-up signals before starting a connection cycle.
            }

            TcpSocket? client = null;
            NetworkStream? stream = null;
            Task? readTask = null;
            Task? writeTask = null;
            Task<bool>? reconnectTask = null;
            var endpointChangeRequested = false;
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                var endpoint = GetEndpoint();
                var connectTask = ConnectWithRetriesAsync(
                    endpoint.Host,
                    endpoint.Port,
                    connectionCts.Token);
                reconnectTask = reconnectRequests.ReadAsync(connectionCts.Token).AsTask();

                var firstCompleted = await Task.WhenAny(connectTask, reconnectTask).ConfigureAwait(false);
                if (ReferenceEquals(firstCompleted, reconnectTask) &&
                    reconnectTask.Status == TaskStatus.RanToCompletion)
                {
                    endpointChangeRequested = true;
                    connectionCts.Cancel();
                    try
                    {
                        await connectTask.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // The cancelled connect attempt is expected during an endpoint change.
                    }

                    continue;
                }

                client = await connectTask.ConfigureAwait(false);
                stream = client.GetStream();
                SetCurrentConnection(client, stream);

                readTask = Task.Run(
                    () => ReadLoopAsync(stream, context, connectionCts.Token),
                    CancellationToken.None);
                writeTask = Task.Run(
                    () => WriteLoopAsync(stream, context, connectionCts.Token),
                    CancellationToken.None);
                _readTask = readTask;
                _writeTask = writeTask;

                reconnectAttempt = 0;
                endpoint = GetEndpoint();
                context.Logger.LogInformation(
                    "TCP client connected to {Host}:{Port}.", endpoint.Host, endpoint.Port);

                await Task.WhenAny(readTask, writeTask, reconnectTask).ConfigureAwait(false);
                if (reconnectTask.Status == TaskStatus.RanToCompletion)
                {
                    endpointChangeRequested = true;
                    endpoint = GetEndpoint();
                    context.Logger.LogInformation(
                        "TCP client endpoint changed; reconnecting to {Host}:{Port}. Runtime parameters were not modified.",
                        endpoint.Host, endpoint.Port);
                }
                else if (!ct.IsCancellationRequested)
                {
                    endpoint = GetEndpoint();
                    context.Logger.LogWarning(
                        "TCP client connection to {Host}:{Port} ended.", endpoint.Host, endpoint.Port);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected during Stop.
            }
            catch (Exception ex)
            {
                var endpoint = GetEndpoint();
                context.Logger.LogWarning(ex,
                    "TCP client connection to {Host}:{Port} failed or disconnected.",
                    endpoint.Host, endpoint.Port);
            }
            finally
            {
                connectionCts.Cancel();
                ClearCurrentConnection(client, stream);
                stream?.Dispose();
                client?.Dispose();

                if (reconnectTask is not null)
                {
                    try
                    {
                        await reconnectTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when the connection ends or the node stops.
                    }
                    catch (ChannelClosedException)
                    {
                        // Expected when Stop completes the request channel.
                    }
                }

                await WaitForCommunicationLoopsAsync(readTask, writeTask, context).ConfigureAwait(false);
                if (ReferenceEquals(_readTask, readTask))
                    _readTask = null;
                if (ReferenceEquals(_writeTask, writeTask))
                    _writeTask = null;
            }

            if (ct.IsCancellationRequested)
                break;

            if (endpointChangeRequested)
            {
                reconnectAttempt = 0;
                continue;
            }

            if (!_autoReconnect)
            {
                Running = false;
                SetFailed();
                context.Logger.LogWarning(
                    "TCP client stopped because AutoReconnect is disabled and the connection ended.");
                break;
            }

            var delay = GetReconnectDelay(reconnectAttempt++);
            var currentEndpoint = GetEndpoint();
            context.Logger.LogWarning(
                "TCP client will reconnect to {Host}:{Port} in {DelaySeconds}s.",
                currentEndpoint.Host, currentEndpoint.Port, (int)delay.TotalSeconds);

            try
            {
                using var reconnectWaitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delayTask = Task.Delay(delay, ct);
                var reconnectDuringDelayTask = reconnectRequests.ReadAsync(reconnectWaitCts.Token).AsTask();
                try
                {
                    var delayCompleted = await Task.WhenAny(delayTask, reconnectDuringDelayTask).ConfigureAwait(false);
                    if (ReferenceEquals(delayCompleted, reconnectDuringDelayTask) &&
                        reconnectDuringDelayTask.Status == TaskStatus.RanToCompletion)
                    {
                        reconnectAttempt = 0;
                        currentEndpoint = GetEndpoint();
                        context.Logger.LogInformation(
                            "TCP client endpoint changed while waiting to reconnect; reconnecting immediately to {Host}:{Port}.",
                            currentEndpoint.Host, currentEndpoint.Port);
                        continue;
                    }

                    await delayTask.ConfigureAwait(false);
                }
                finally
                {
                    reconnectWaitCts.Cancel();
                    try
                    {
                        await reconnectDuringDelayTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when the delay completes before an endpoint change arrives.
                    }
                    catch (ChannelClosedException)
                    {
                        // Expected when Stop completes the request channel.
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
        }
    }

    private void SetCurrentConnection(TcpSocket client, NetworkStream stream)
    {
        lock (_connectionGate)
        {
            _client = client;
            _stream = stream;
        }
    }

    private void ClearCurrentConnection(TcpSocket? client, NetworkStream? stream)
    {
        lock (_connectionGate)
        {
            if (ReferenceEquals(_client, client))
                _client = null;
            if (ReferenceEquals(_stream, stream))
                _stream = null;
        }
    }

    private void DisposeCurrentConnection()
    {
        TcpSocket? client;
        NetworkStream? stream;
        lock (_connectionGate)
        {
            client = _client;
            stream = _stream;
            _client = null;
            _stream = null;
        }

        stream?.Dispose();
        client?.Dispose();
    }

    private static async Task WaitForCommunicationLoopsAsync(
        Task? readTask, Task? writeTask, INodeContext context)
    {
        if (readTask is null && writeTask is null)
            return;

        try
        {
            var tasks = new[] { readTask, writeTask }
                .Where(task => task is not null)
                .Select(task => task!)
                .ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when reconnecting or stopping.
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "TCP client communication loop ended with an error.");
        }
    }

    private TimeSpan GetReconnectDelay(int reconnectAttempt)
    {
        var multiplier = 1 << Math.Min(reconnectAttempt, 5);
        var seconds = Math.Min(_reconnectMaxDelaySeconds, _reconnectDelaySeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task<TcpSocket> ConnectWithRetriesAsync(
        string host,
        int port,
        CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= _maxConnectAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var client = new TcpSocket();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_connectTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await client.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
                return client;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException(
                    $"TCP client connect to {host}:{port} timed out after {_connectTimeoutSeconds}s.");
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
            {
                lastError = ex;
            }
            finally
            {
                if (client.Connected is false)
                    client.Dispose();
            }

            _logger?.LogWarning(
                "TCP client could not connect to {Host}:{Port} (attempt {Attempt}/{Attempts}): {Message}",
                host, port, attempt, _maxConnectAttempts, lastError?.Message ?? "unknown error");

            if (attempt < _maxConnectAttempts)
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"TCP client could not connect to {host}:{port} after {_maxConnectAttempts} attempt(s).",
            lastError);
    }

    private async Task ReadLoopAsync(NetworkStream stream, INodeContext context, CancellationToken ct)
    {
        var buffer = new byte[_receiveBufferSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = await ReadWithTimeoutAsync(stream, buffer, _readTimeoutSeconds, ct).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    context.Logger.LogWarning("TCP client read timeout: {Message}", ex.Message);
                    break;
                }

                if (count == 0)
                {
                    context.Logger.LogWarning("TCP client remote endpoint closed the connection.");
                    break;
                }

                var bytes = new byte[count];
                Buffer.BlockCopy(buffer, 0, bytes, 0, count);
                Interlocked.Increment(ref _receivedBlocks);
                PublishReceived(bytes, context);
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
            context.Logger.LogWarning(ex, "TCP client read loop failed.");
        }
    }

    private async Task WriteLoopAsync(NetworkStream stream, INodeContext context, CancellationToken ct)
    {
        var channel = _sendChannel;
        if (channel is null)
            return;

        try
        {
            await foreach (var bytes in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await WriteWithTimeoutAsync(stream, bytes, _writeTimeoutSeconds, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _sentBlocks);
                var endpoint = GetEndpoint();
                context.Logger.LogTrace(
                    "TCP client sent {Length} bytes to {Host}:{Port}: {Preview}",
                    bytes.Length, endpoint.Host, endpoint.Port, FormatBytes(bytes));
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
            context.Logger.LogWarning(ex, "TCP client write loop failed.");
        }
    }

    private void PublishReceived(byte[] bytes, INodeContext context)
    {
        var endpoint = GetEndpoint();
        context.Logger.LogTrace(
            "TCP client received {Length} bytes from {Host}:{Port}: {Preview}",
            bytes.Length, endpoint.Host, endpoint.Port, FormatBytes(bytes));

        var binary = BinSample.CreateSample();
        binary.SetValue(bytes, OutputBinaryPin.Name);
        OutputBinaryPin.SendSample(binary);

        var text = StringSample.CreateSample();
        text.SetValue(Encoding.UTF8.GetString(bytes), OutputStringPin.Name);
        OutputStringPin.SendSample(text);
    }

    private void QueueSend(byte[] bytes, string pinName)
    {
        var channel = _sendChannel;
        if (channel is null || !channel.Writer.TryWrite(bytes))
        {
            Interlocked.Increment(ref _droppedBlocks);
            _logger?.LogWarning(
                "TCP client dropped {Length} bytes from {Pin}: send queue is full or unavailable.",
                bytes.Length, pinName);
            return;
        }

        _logger?.LogTrace("TCP client queued {Length} bytes from {Pin}.", bytes.Length, pinName);
    }

    private int ReadBoundedInt(INodeContext context, string parameterName, int fallback, int min, int max)
    {
        var value = ParameterValues.GetInt(Param(parameterName).Value, fallback);
        if (value < min || value > max)
        {
            context.Logger.LogWarning(
                "TCP client parameter {Name}={Value} is outside [{Min}, {Max}]; using the nearest valid value.",
                parameterName, value, min, max);
            value = Math.Clamp(value, min, max);
        }

        return value;
    }

    private static Channel<byte[]> CreateSendChannel(int capacity) =>
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private static async Task<int> ReadWithTimeoutAsync(
        NetworkStream stream, byte[] buffer, int timeoutSeconds, CancellationToken ct)
    {
        if (timeoutSeconds <= 0)
            return await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(), linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no data received for {timeoutSeconds}s");
        }
    }

    private static async Task WriteWithTimeoutAsync(
        NetworkStream stream, byte[] bytes, int timeoutSeconds, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await stream.WriteAsync(bytes.AsMemory(), linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"write did not complete within {timeoutSeconds}s");
        }
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    private static string FormatBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "(empty)";

        var length = Math.Min(bytes.Length, 64);
        var preview = string.Join(' ', bytes.Take(length).Select(b => b.ToString("X2")));
        return bytes.Length > length ? $"{preview} ..." : preview;
    }
    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}
