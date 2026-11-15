// -----------------------------------------------------------------------
// <copyright company="Your Company" file="TcpServerNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.TcpServer;

/// <summary>
/// Raw TCP multi-client server node. It accepts any number of clients up to the configured
/// limit and treats every read as an opaque byte block. It does not add framing, delimiters,
/// protocol headers, or automatic replies. Input pin values are broadcast to all clients that
/// are connected at the time the block is processed.
/// </summary>
public sealed class TcpServerNode : BaseNode
{
    private const int DefaultMaxClients = 32;
    private const int DefaultReadTimeoutSeconds = 0;
    private const int DefaultWriteTimeoutSeconds = 10;
    private const int DefaultSendQueueCapacity = 256;
    private const int DefaultReceiveBufferSize = 4096;

    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private readonly ConcurrentDictionary<int, ClientSession> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _broadcastTask;
    private Channel<byte[]>? _broadcastChannel;
    private ILogger? _logger;

    private string _host = "0.0.0.0";
    private int _port = 9000;
    private int _maxClients = DefaultMaxClients;
    private int _readTimeoutSeconds = DefaultReadTimeoutSeconds;
    private int _writeTimeoutSeconds = DefaultWriteTimeoutSeconds;
    private int _sendQueueCapacity = DefaultSendQueueCapacity;
    private int _receiveBufferSize = DefaultReceiveBufferSize;
    private int _nextClientId;

    private long _acceptedClients;
    private long _rejectedClients;
    private long _sentBlocks;
    private long _receivedBlocks;
    private long _droppedBlocks;

    public TcpServerNode()
    {
        Uuid = "9A652C51-6A66-4C36-B7A1-DF1E9B38F36F";
        AddParam();
        AddPins();
    }

    public override string Name => "TCP Server";
    public override string DisplayName { get; set; } = "TCP Server";
    public override string Category => "Network";

    /// <summary>UTF-8 text input. The text is encoded as bytes before it is broadcast.</summary>
    public BasePin InputStringPin { get; } =
        new("input_string", DataType.STRING, PinDirection.Input, required: false);

    /// <summary>Raw byte input. The byte array is broadcast unchanged.</summary>
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
        AddParameter(new NodeParameter("Host", typeof(string), "0.0.0.0",
            isEditable: true, group: "Connection", zone: "TcpServerZone"));
        AddParameter(new NodeParameter("Port", typeof(int), 9000,
            isEditable: true, group: "Connection", zone: "TcpServerZone"));
        AddParameter(new NodeParameter("MaxClients", typeof(int), DefaultMaxClients,
            isEditable: true, group: "Connection", zone: "TcpServerZone"));
        AddParameter(new NodeParameter("SendQueueCapacity", typeof(int), DefaultSendQueueCapacity,
            isEditable: true, group: "Runtime", zone: "TcpServerZone"));
        AddParameter(new NodeParameter("ReadTimeoutSeconds", typeof(int), DefaultReadTimeoutSeconds,
            isEditable: true, group: "Runtime", zone: "TcpServerZone"));
        AddParameter(new NodeParameter("WriteTimeoutSeconds", typeof(int), DefaultWriteTimeoutSeconds,
            isEditable: true, group: "Runtime", zone: "TcpServerZone"));
        AddParameter(new NodeParameter("ReceiveBufferSize", typeof(int), DefaultReceiveBufferSize,
            isEditable: true, group: "Runtime", zone: "TcpServerZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Host", out var host) && host is not null)
            Param("Host").Value = ParameterValues.GetString(host, "0.0.0.0") ?? "0.0.0.0";
        if (parameters.TryGetValue("Port", out var port) && port is not null)
            Param("Port").Value = ParameterValues.GetInt(port, 9000);
        if (parameters.TryGetValue("MaxClients", out var maxClients) && maxClients is not null)
            Param("MaxClients").Value = ParameterValues.GetInt(maxClients, DefaultMaxClients);
        if (parameters.TryGetValue("SendQueueCapacity", out var queueCapacity) && queueCapacity is not null)
            Param("SendQueueCapacity").Value = ParameterValues.GetInt(queueCapacity, DefaultSendQueueCapacity);
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

        _host = (ParameterValues.GetString(Param("Host").Value, "0.0.0.0") ?? "0.0.0.0").Trim();
        _port = ReadBoundedInt(context, "Port", 9000, 1, 65535);
        _maxClients = ReadBoundedInt(context, "MaxClients", DefaultMaxClients, 1, 1024);
        _sendQueueCapacity = ReadBoundedInt(context, "SendQueueCapacity", DefaultSendQueueCapacity, 1, 65536);
        _readTimeoutSeconds = ReadBoundedInt(context, "ReadTimeoutSeconds", DefaultReadTimeoutSeconds, 0, 3600);
        _writeTimeoutSeconds = ReadBoundedInt(context, "WriteTimeoutSeconds", DefaultWriteTimeoutSeconds, 1, 3600);
        _receiveBufferSize = ReadBoundedInt(context, "ReceiveBufferSize", DefaultReceiveBufferSize, 64, 1_048_576);

        var address = ResolveBindAddress(_host);
        _listener = new TcpListener(address, _port);
        try
        {
            _listener.Start();
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            _listener = null;
            SetFailed();
            context.Logger.LogError(ex, "TCP server could not bind to {Host}:{Port}.", _host, _port);
            throw new InvalidOperationException($"TCP server could not bind to {_host}:{_port}.", ex);
        }

        _cts = new CancellationTokenSource();
        _broadcastChannel = CreateBroadcastChannel(_sendQueueCapacity);
        SetIdle();

        context.Logger.LogInformation(
            "TCP server listening on {Host}:{Port}; maxClients={MaxClients}, readTimeout={ReadTimeout}s, writeTimeout={WriteTimeout}s",
            _host, _port, _maxClients, _readTimeoutSeconds, _writeTimeoutSeconds);
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        if (_cts is null || _broadcastChannel is null || _listener is null)
            throw new InvalidOperationException("TCP server must be initialized before Run.");

        Running = true;
        var listener = _listener;
        var cts = _cts;
        _acceptTask = Task.Run(() => AcceptLoopAsync(listener, context, cts.Token), CancellationToken.None);
        _broadcastTask = Task.Run(() => BroadcastLoopAsync(context, cts.Token), CancellationToken.None);
        context.Logger.LogInformation("TCP server is running on {Host}:{Port}.", _host, _port);
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

            var listener = _listener;
            _listener = null;
            listener?.Stop();

            var channel = _broadcastChannel;
            _broadcastChannel = null;
            channel?.Writer.TryComplete();

            var acceptTask = _acceptTask;
            var broadcastTask = _broadcastTask;
            _acceptTask = null;
            _broadcastTask = null;
            if (acceptTask is not null || broadcastTask is not null)
            {
                try
                {
                    var tasks = new[] { acceptTask, broadcastTask }.Where(t => t is not null).Select(t => t!).ToArray();
                    await Task.WhenAll(tasks)
                        .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    context.Logger.LogWarning("TCP server loops did not stop within 2s.");
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning(ex, "TCP server loop ended with an error while stopping.");
                }
            }

            var sessions = _clients.Values.ToArray();
            foreach (var session in sessions)
                session.Dispose();

            if (sessions.Length > 0)
            {
                try
                {
                    await Task.WhenAll(sessions.Select(s => s.Completion.Task))
                        .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    context.Logger.LogWarning("TCP server client tasks did not stop within 2s.");
                }
            }

            _clients.Clear();
            cts?.Dispose();
            Running = false;
            SetIdle();
            context.Logger.LogInformation(
                "TCP server stopped: accepted={Accepted}, rejected={Rejected}, sent={Sent}, received={Received}, dropped={Dropped}",
                Interlocked.Read(ref _acceptedClients),
                Interlocked.Read(ref _rejectedClients),
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
                _logger?.LogError("TCP server ignored a null value on {Pin}.", pin.Name);
                return;
            }

            if (value.Type != pin.DataType)
            {
                Interlocked.Increment(ref _droppedBlocks);
                _logger?.LogError(
                    "TCP server rejected {ActualType} on {Pin}; expected {ExpectedType}.",
                    value.Type, pin.Name, pin.DataType);
                return;
            }

            byte[] bytes;
            if (ReferenceEquals(pin, InputStringPin))
            {
                if (value is not StringSample text || text.StringValue is null)
                {
                    Interlocked.Increment(ref _droppedBlocks);
                    _logger?.LogError("TCP server ignored a null string on {Pin}.", pin.Name);
                    return;
                }

                bytes = Encoding.UTF8.GetBytes(text.StringValue);
            }
            else
            {
                if (value is not BinSample binary || binary.BinValue is null)
                {
                    Interlocked.Increment(ref _droppedBlocks);
                    _logger?.LogError("TCP server ignored a null binary value on {Pin}.", pin.Name);
                    return;
                }

                bytes = binary.BinValue.ToArray();
            }

            if (_clients.IsEmpty)
            {
                Interlocked.Increment(ref _droppedBlocks);
                _logger?.LogWarning(
                    "TCP server dropped {Length} bytes from {Pin}: no clients are connected.",
                    bytes.Length, pin.Name);
                return;
            }

            QueueBroadcast(bytes, pin.Name);

        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, INodeContext context, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (_clients.Count >= _maxClients)
                {
                    Interlocked.Increment(ref _rejectedClients);
                    context.Logger.LogWarning(
                        "TCP server rejected a client: MaxClients ({MaxClients}) reached.",
                        _maxClients);
                    client.Dispose();
                    continue;
                }

                var id = Interlocked.Increment(ref _nextClientId);
                var session = new ClientSession(id, client);
                if (!_clients.TryAdd(id, session))
                {
                    client.Dispose();
                    continue;
                }

                Interlocked.Increment(ref _acceptedClients);
                var address = client.Client.RemoteEndPoint?.ToString() ?? "?";
                context.Logger.LogTrace(
                    "TCP server client connected: {Id} ({Address})",
                    id, address);
                session.ReadTask = Task.Run(
                    () => ClientReadLoopAsync(session, context, ct),
                    CancellationToken.None);
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
            context.Logger.LogError(ex, "TCP server accept loop failed.");
            Running = false;
        }
    }

    private async Task BroadcastLoopAsync(INodeContext context, CancellationToken ct)
    {
        var channel = _broadcastChannel;
        if (channel is null)
            return;

        try
        {
            await foreach (var bytes in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var sessions = _clients.Values.ToArray();
                if (sessions.Length == 0)
                {
                    Interlocked.Increment(ref _droppedBlocks);
                    context.Logger.LogWarning(
                        "TCP server dropped {Length} broadcast bytes: no clients are connected.",
                        bytes.Length);
                    continue;
                }

                foreach (var session in sessions)
                {
                    try
                    {
                        await WriteToClientAsync(session, bytes, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref _sentBlocks);
                        context.Logger.LogTrace(
                            "TCP server sent {Length} bytes to client {Id}: {Preview}",
                            bytes.Length, session.Id, FormatBytes(bytes));
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException)
                    {
                        DisconnectSession(session);
                        context.Logger.LogWarning(
                            "TCP server could not send to client {Id}: {Message}",
                            session.Id, ex.Message);
                    }
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
            context.Logger.LogError(ex, "TCP server broadcast loop failed.");
            Running = false;
        }
    }

    private async Task ClientReadLoopAsync(ClientSession session, INodeContext context, CancellationToken ct)
    {
        var buffer = new byte[_receiveBufferSize];
        try
        {
            var stream = session.Client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = await ReadWithTimeoutAsync(stream, buffer, _readTimeoutSeconds, ct).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    context.Logger.LogWarning(
                        "TCP server client {Id} read timeout: {Message}",
                        session.Id, ex.Message);
                    break;
                }

                if (count == 0)
                    break;

                var bytes = new byte[count];
                Buffer.BlockCopy(buffer, 0, bytes, 0, count);
                Interlocked.Increment(ref _receivedBlocks);
                PublishReceived(bytes, session.Id, context);
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
                "TCP server client {Id} read loop ended: {Message}",
                session.Id, ex.Message);
        }
        finally
        {
            DisconnectSession(session);
            context.Logger.LogTrace("TCP server client disconnected: {Id}", session.Id);
        }
    }

    private void PublishReceived(byte[] bytes, int clientId, INodeContext context)
    {
        context.Logger.LogTrace(
            "TCP server received {Length} bytes from client {Id}: {Preview}",
            bytes.Length, clientId, FormatBytes(bytes));

        var binary = BinSample.CreateSample();
        binary.SetValue(bytes, OutputBinaryPin.Name);
        OutputBinaryPin.SendSampleWithDispatchLog(binary, $"received binary from client {clientId}");

        var text = StringSample.CreateSample();
        text.SetValue(Encoding.UTF8.GetString(bytes), OutputStringPin.Name);
        OutputStringPin.SendSampleWithDispatchLog(text, $"received text from client {clientId}");
    }

    private async Task WriteToClientAsync(ClientSession session, byte[] bytes, CancellationToken ct)
    {
        await session.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = session.Client.GetStream();
            await WriteWithTimeoutAsync(stream, bytes, _writeTimeoutSeconds, ct).ConfigureAwait(false);
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    private void QueueBroadcast(byte[] bytes, string pinName)
    {
        var channel = _broadcastChannel;
        if (channel is null || !channel.Writer.TryWrite(bytes))
        {
            Interlocked.Increment(ref _droppedBlocks);
            _logger?.LogWarning(
                "TCP server dropped {Length} bytes from {Pin}: broadcast queue is full or unavailable.",
                bytes.Length, pinName);
            return;
        }

        _logger?.LogTrace("TCP server queued {Length} bytes from {Pin}.", bytes.Length, pinName);
    }

    private void DisconnectSession(ClientSession session)
    {
        if (_clients.TryRemove(session.Id, out _))
            session.Dispose();
    }

    private int ReadBoundedInt(INodeContext context, string parameterName, int fallback, int min, int max)
    {
        var value = ParameterValues.GetInt(Param(parameterName).Value, fallback);
        if (value < min || value > max)
        {
            context.Logger.LogWarning(
                "TCP server parameter {Name}={Value} is outside [{Min}, {Max}]; using the nearest valid value.",
                parameterName, value, min, max);
            value = Math.Clamp(value, min, max);
        }

        return value;
    }

    private static Channel<byte[]> CreateBroadcastChannel(int capacity) =>
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

    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host is "0.0.0.0" or "*")
            return IPAddress.Any;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (IPAddress.TryParse(host, out var address))
            return address;

        throw new InvalidOperationException(
            $"TCP server Host '{host}' is not a local IP address. Use 0.0.0.0, 127.0.0.1 or a local IP.");
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

    private sealed class ClientSession
    {
        private int _disposed;

        public ClientSession(int id, TcpClient client)
        {
            Id = id;
            Client = client;
        }

        public int Id { get; }
        public TcpClient Client { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? ReadTask { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Client.Dispose();
            WriteLock.Dispose();
            Completion.TrySetResult(true);
        }
    }
    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}
