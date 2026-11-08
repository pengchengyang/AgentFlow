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

    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private readonly object _connectionGate = new();
    private TcpSocket? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _supervisorTask;
    private Task? _readTask;
    private Task? _writeTask;
    private Channel<byte[]>? _sendChannel;
    private ILogger? _logger;

    private string _host = "127.0.0.1";
    private int _port = 9000;
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

        _host = (ParameterValues.GetString(Param("Host").Value, "127.0.0.1") ?? "127.0.0.1").Trim();
        if (_host.Length == 0)
            throw new InvalidOperationException("TCP client Host must not be empty.");

        _port = ReadBoundedInt(context, "Port", 9000, 1, 65535);
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
        SetIdle();

        context.Logger.LogInformation(
            "TCP client initialized for {Host}:{Port}; queue={Queue}, autoReconnect={AutoReconnect}, reconnectDelay={ReconnectDelay}s, reconnectMaxDelay={ReconnectMaxDelay}s, readTimeout={ReadTimeout}s, writeTimeout={WriteTimeout}s",
            _host, _port, _sendQueueCapacity, _autoReconnect, _reconnectDelaySeconds, _reconnectMaxDelaySeconds,
            _readTimeoutSeconds, _writeTimeoutSeconds);
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        var rootCts = _cts;
        if (rootCts is null || _sendChannel is null)
            throw new InvalidOperationException("TCP client must be initialized before Run.");
        if (_supervisorTask is not null)
            throw new InvalidOperationException("TCP client is already running.");

        Running = true;
        _supervisorTask = Task.Run(
            () => ConnectionSupervisorAsync(context, rootCts.Token),
            CancellationToken.None);
        context.Logger.LogInformation(
            "TCP client supervisor started for {Host}:{Port}; autoReconnect={AutoReconnect}.",
            _host, _port, _autoReconnect);
        return Task.CompletedTask;
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        await _stopLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var cts = _cts;
            _cts = null;
            cts?.Cancel();

            var channel = _sendChannel;
            _sendChannel = null;
            channel?.Writer.TryComplete();

            DisposeCurrentConnection();

            var supervisorTask = _supervisorTask;
            _supervisorTask = null;
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

            Running = false;
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

    private async Task ConnectionSupervisorAsync(INodeContext context, CancellationToken ct)
    {
        var reconnectAttempt = 0;
        while (!ct.IsCancellationRequested)
        {
            TcpSocket? client = null;
            NetworkStream? stream = null;
            Task? readTask = null;
            Task? writeTask = null;
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                client = await ConnectWithRetriesAsync(ct).ConfigureAwait(false);
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
                context.Logger.LogInformation(
                    "TCP client connected to {Host}:{Port}.", _host, _port);

                await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                    context.Logger.LogWarning(
                        "TCP client connection to {Host}:{Port} ended.", _host, _port);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected during Stop.
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(ex,
                    "TCP client connection to {Host}:{Port} failed or disconnected.", _host, _port);
            }
            finally
            {
                connectionCts.Cancel();
                ClearCurrentConnection(client, stream);
                stream?.Dispose();
                client?.Dispose();

                await WaitForCommunicationLoopsAsync(readTask, writeTask, context).ConfigureAwait(false);
                if (ReferenceEquals(_readTask, readTask))
                    _readTask = null;
                if (ReferenceEquals(_writeTask, writeTask))
                    _writeTask = null;
            }

            if (ct.IsCancellationRequested)
                break;

            if (!_autoReconnect)
            {
                Running = false;
                SetFailed();
                context.Logger.LogWarning(
                    "TCP client stopped because AutoReconnect is disabled and the connection ended.");
                break;
            }

            var delay = GetReconnectDelay(reconnectAttempt++);
            context.Logger.LogWarning(
                "TCP client will reconnect to {Host}:{Port} in {DelaySeconds}s.",
                _host, _port, (int)delay.TotalSeconds);

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

    private async Task<TcpSocket> ConnectWithRetriesAsync(CancellationToken ct)
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
                await client.ConnectAsync(_host, _port, linkedCts.Token).ConfigureAwait(false);
                return client;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException(
                    $"TCP client connect to {_host}:{_port} timed out after {_connectTimeoutSeconds}s.");
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
                _host, _port, attempt, _maxConnectAttempts, lastError?.Message ?? "unknown error");

            if (attempt < _maxConnectAttempts)
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"TCP client could not connect to {_host}:{_port} after {_maxConnectAttempts} attempt(s).",
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
                context.Logger.LogTrace(
                    "TCP client sent {Length} bytes to {Host}:{Port}: {Preview}",
                    bytes.Length, _host, _port, FormatBytes(bytes));
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
        context.Logger.LogTrace(
            "TCP client received {Length} bytes from {Host}:{Port}: {Preview}",
            bytes.Length, _host, _port, FormatBytes(bytes));

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
