// -----------------------------------------------------------------------
// <copyright company="Your Company" file="SocketServerNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.SocketServer;

/// <summary>
/// TCP multi-client server node. Messages are newline-delimited UTF-8 text (one message per
/// line). Each accepted client is tracked until its read loop ends, so Stop can wait for all
/// client work to finish before the node is torn down.
/// </summary>
public sealed class SocketServerNode : BaseNode
{
    private const string ProtocolVersion = "1";
    private const int MaxMessageLength = 8192;
    private const int MaxClientsLimit = 1024;
    private const int DefaultMaxClients = 32;
    private const int DefaultReadTimeoutSeconds = 60;
    private const int DefaultWriteTimeoutSeconds = 10;

    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private int _readTimeoutSeconds = DefaultReadTimeoutSeconds;
    private int _writeTimeoutSeconds = DefaultWriteTimeoutSeconds;
    private long _acceptedClients;
    private long _rejectedClients;
    private long _receivedMessages;
    private long _sentMessages;
    private long _droppedMessages;
    private long _clientErrors;

    private readonly object _sendLock = new();
    private string _send1 = "";
    private string _send2 = "";
    private string _send3 = "";
    private string _send4 = "";

    public override string Name => "Socket Server";
    public override string DisplayName { get; set; } = string.Empty;
    public override string Category => "Network";

    /// <summary>Send1 input pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin Send1Pin { get; } = new("Send1", DataType.STRING, PinDirection.Input, required: false);

    /// <summary>Send2 input pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin Send2Pin { get; } = new("Send2", DataType.STRING, PinDirection.Input, required: false);

    /// <summary>Send3 input pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin Send3Pin { get; } = new("Send3", DataType.STRING, PinDirection.Input, required: false);

    /// <summary>Send4 input pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin Send4Pin { get; } = new("Send4", DataType.STRING, PinDirection.Input, required: false);

    /// <summary>Received output pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin ReceivedPin { get; } = new("Received", DataType.STRING, PinDirection.Output);

    public SocketServerNode()
    {
        Uuid = "9A652C51-6A66-4C36-B7A1-DF1E9B38F36F";
        AddParam();
        AddPins();
    }

    /// <summary>Create the node's input / output pins (required by <see cref="BaseNode"/>).</summary>
    public override void AddPins()
    {
        AddInputPin(Send1Pin);
        AddInputPin(Send2Pin);
        AddInputPin(Send3Pin);
        AddInputPin(Send4Pin);
        AddOutputPin(ReceivedPin);
    }

    /// <summary>Declare the node's parameters.</summary>
    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter("Host", typeof(string), "0.0.0.0", isEditable: true, group: "Settings", zone: "General"));
        AddParameter(new NodeParameter("Port", typeof(int), 9000, isEditable: true, group: "Settings", zone: "General"));
        AddParameter(new NodeParameter("Response", typeof(string), "pong", isEditable: true, group: "Settings", zone: "General"));
        AddParameter(new NodeParameter("MaxClients", typeof(int), DefaultMaxClients, isEditable: true, group: "Settings", zone: "General"));
        AddParameter(new NodeParameter("ReadTimeoutSeconds", typeof(int), DefaultReadTimeoutSeconds, isEditable: true, group: "Settings", zone: "General"));
        AddParameter(new NodeParameter("WriteTimeoutSeconds", typeof(int), DefaultWriteTimeoutSeconds, isEditable: true, group: "Settings", zone: "General"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Host", out var h) && h is not null) Param("Host").Value = h.ToString() ?? "";
        if (parameters.TryGetValue("Port", out var p) && p is not null) Param("Port").Value = ParameterValues.GetInt(p, 9000);
        if (parameters.TryGetValue("Response", out var r) && r is not null) Param("Response").Value = r.ToString() ?? "";
        if (parameters.TryGetValue("MaxClients", out var m) && m is not null)
            Param("MaxClients").Value = ParameterValues.GetInt(m, DefaultMaxClients);
        if (parameters.TryGetValue("ReadTimeoutSeconds", out var rt) && rt is not null)
            Param("ReadTimeoutSeconds").Value = ParameterValues.GetInt(rt, DefaultReadTimeoutSeconds);
        if (parameters.TryGetValue("WriteTimeoutSeconds", out var wt) && wt is not null)
            Param("WriteTimeoutSeconds").Value = ParameterValues.GetInt(wt, DefaultWriteTimeoutSeconds);
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (value is not StringSample ss || ss.StringValue is not { } s) return;
        lock (_sendLock)
        {
            if (pin.Name == Send1Pin.Name) _send1 = s;
            else if (pin.Name == Send2Pin.Name) _send2 = s;
            else if (pin.Name == Send3Pin.Name) _send3 = s;
            else if (pin.Name == Send4Pin.Name) _send4 = s;
        }
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        if (_listener is not null)
            return Task.CompletedTask;

        SetConnecting();

        var port = ParameterValues.GetInt(Param("Port").Value, 9000);
        if (port is < 1 or > 65535)
        {
            SetFailed();
            throw new InvalidOperationException($"SocketServer Port must be between 1 and 65535 (got {port}).");
        }

        var host = Param("Host").Value?.ToString()?.Trim() ?? "";
        IPAddress address;
        try
        {
            address = ResolveBindAddress(host);
        }
        catch (Exception ex)
        {
            SetFailed();
            context.Logger.LogError(ex, "SocketServer has an invalid Host value: {Host}", host);
            throw;
        }

        try
        {
            _listener = new TcpListener(address, port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            _listener?.Stop();
            _listener = null;
            SetFailed();
            context.Logger.LogError(ex, "SocketServer could not bind to {Host}:{Port}.", host, port);
            throw new InvalidOperationException($"SocketServer could not bind to {host}:{port}.", ex);
        }

        context.Logger.LogInformation(
            "SocketServer listening on {Endpoint} (protocol v{Version})",
            ((IPEndPoint)_listener.LocalEndpoint).ToString(), ProtocolVersion);
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        if (_listener is null)
        {
            SetFailed();
            throw new InvalidOperationException("SocketServer must be initialized before Run.");
        }

        SetConnecting();
        var maxClients = ReadBoundedInt(context, "MaxClients", DefaultMaxClients, 1, MaxClientsLimit);
        _readTimeoutSeconds = ReadBoundedInt(
            context, "ReadTimeoutSeconds", DefaultReadTimeoutSeconds, 1, 3600);
        _writeTimeoutSeconds = ReadBoundedInt(
            context, "WriteTimeoutSeconds", DefaultWriteTimeoutSeconds, 1, 120);
        Interlocked.Exchange(ref _acceptedClients, 0);
        Interlocked.Exchange(ref _rejectedClients, 0);
        Interlocked.Exchange(ref _receivedMessages, 0);
        Interlocked.Exchange(ref _sentMessages, 0);
        Interlocked.Exchange(ref _droppedMessages, 0);
        Interlocked.Exchange(ref _clientErrors, 0);

        Running = true;
        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(context, _cts.Token, maxClients));
        context.Logger.LogInformation(
            "SocketServer protocol v{Version}: maxClients={MaxClients}, readTimeout={ReadTimeout}s, writeTimeout={WriteTimeout}s",
            ProtocolVersion, maxClients, _readTimeoutSeconds, _writeTimeoutSeconds);
        return Task.CompletedTask;
    }

    /// <summary>One thread, one <c>while</c> loop: keep accepting clients until stopped.</summary>
    private async Task AcceptLoopAsync(INodeContext context, CancellationToken ct, int maxClients)
    {
        var listener = _listener;
        if (listener is null)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                if (_clients.Count >= maxClients)
                {
                    Interlocked.Increment(ref _rejectedClients);
                    context.Logger.LogWarning(
                        "SocketServer rejected a client: MaxClients ({MaxClients}) reached.", maxClients);
                    client.Dispose();
                    continue;
                }

                var id = Guid.NewGuid();
                var session = new ClientSession(client);
                if (!_clients.TryAdd(id, session))
                {
                    client.Dispose();
                    session.WriteLock.Dispose();
                    session.Completion.TrySetResult(true);
                    context.Logger.LogWarning("SocketServer could not track a newly accepted client.");
                    continue;
                }

                _ = Task.Run(() => RunClientAsync(context, id, session, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal stop.
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            if (ct.IsCancellationRequested)
                context.Logger.LogTrace("SocketServer listener stopped: {Message}", ex.Message);
            else
            {
                context.Logger.LogError(ex, "SocketServer accept loop stopped unexpectedly.");
                SetFailed();
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                context.Logger.LogError(ex, "SocketServer accept loop failed.");
                SetFailed();
            }
        }
    }

    private async Task RunClientAsync(INodeContext context, Guid id, ClientSession session, CancellationToken ct)
    {
        Interlocked.Increment(ref _acceptedClients);
        try
        {
            await HandleClientAsync(context, id, session, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _clientErrors);
                context.Logger.LogWarning("SocketServer client {Id} failed: {Message}", id, ex.Message);
            }
        }
        finally
        {
            _clients.TryRemove(id, out _);
            session.Client.Dispose();
            session.WriteLock.Dispose();
            session.Completion.TrySetResult(true);
            context.Logger.LogTrace("SocketServer client disconnected: {Id}", id);
        }
    }

    /// <summary>Read newline-delimited messages from one client and reply on the same client.</summary>
    private async Task HandleClientAsync(INodeContext context, Guid id, ClientSession session, CancellationToken ct)
    {
        var ep = (IPEndPoint?)session.Client.Client.RemoteEndPoint;
        var address = ep?.Address.ToString() ?? "?";
        context.Logger.LogTrace("SocketServer client connected: {Id} ({Address})", id, address);

        using var stream = session.Client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await ReadLineWithTimeoutAsync(reader, _readTimeoutSeconds, ct);
            }
            catch (TimeoutException ex)
            {
                context.Logger.LogWarning(
                    "SocketServer client {Id} read timeout: {Message}", id, ex.Message);
                break;
            }

            if (line is null)
                break; // peer closed

            if (line.Length == 0)
            {
                context.Logger.LogTrace("SocketServer ignored an empty line from {Id}.", id);
                continue;
            }

            if (line.Length > MaxMessageLength)
            {
                Interlocked.Increment(ref _droppedMessages);
                context.Logger.LogWarning(
                    "SocketServer dropped an oversized line from {Id} ({Length} characters; max {Max}).",
                    id, line.Length, MaxMessageLength);
                continue;
            }

            var received = StringSample.CreateSample();
            received.SetValue(line, "Received");
            ReceivedPin.SendSample(received);
            Interlocked.Increment(ref _receivedMessages);
            context.Logger.LogTrace("SocketServer received from {Id}: {Msg}", id, line);

            foreach (var reply in GetReplies())
                await SendToAsync(context, id, reply, ct);
        }
    }

    /// <summary>Snapshot of the current reply payloads (Send1..4, or Response when all empty).</summary>
    private IReadOnlyList<string> GetReplies()
    {
        lock (_sendLock)
        {
            var sends = new[] { _send1, _send2, _send3, _send4 }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (sends.Length > 0)
                return sends;

            var response = Param("Response").Value?.ToString() ?? "";
            return string.IsNullOrWhiteSpace(response) ? Array.Empty<string>() : new[] { response };
        }
    }

    /// <summary>Send one line to a specific client, serialized per client.</summary>
    private async Task SendToAsync(INodeContext context, Guid clientId, string message, CancellationToken ct)
    {
        if (!_clients.TryGetValue(clientId, out var session))
            return;

        var line = NormalizeLine(message);
        if (line.Length == 0 || line.Length > MaxMessageLength)
            return;

        var acquired = false;
        try
        {
            await session.WriteLock.WaitAsync(ct);
            acquired = true;

            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await WriteWithTimeoutAsync(session.Client.GetStream(), bytes, _writeTimeoutSeconds, ct);
            Interlocked.Increment(ref _sentMessages);
            context.Logger.LogTrace("SocketServer sent to {Id}: {Msg}", clientId, line);
        }
        catch (TimeoutException ex)
        {
            context.Logger.LogDebug("SocketServer send timeout for {Id}: {Message}", clientId, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
                context.Logger.LogDebug("SocketServer could not send to {Id}: {Message}", clientId, ex.Message);
        }
        finally
        {
            if (acquired)
            {
                try { session.WriteLock.Release(); }
                catch (ObjectDisposedException) { /* The session was torn down while sending. */ }
            }
        }
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        await _stopLock.WaitAsync();
        try
        {
            var cts = _cts;
            _cts = null;
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }

            _listener?.Stop();
            _listener = null;

            // Stop accepting first, so no new client session can be added after the snapshot below.
            var accept = _acceptTask;
            _acceptTask = null;
            if (accept is not null)
            {
                try
                {
                    await accept.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    context.Logger.LogWarning("SocketServer accept loop did not stop within 2s.");
                }
                catch
                {
                    // Already terminated.
                }
            }

            var sessions = _clients.Values.ToArray();
            foreach (var session in sessions)
                session.Client.Dispose();

            if (sessions.Length > 0)
            {
                try
                {
                    await Task.WhenAll(sessions.Select(s => s.Completion.Task))
                        .WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    context.Logger.LogWarning("SocketServer client tasks did not stop within 2s.");
                }
                catch
                {
                    // Client tasks catch their own failures; the completion source only reports done.
                }
            }

            _clients.Clear();
            cts?.Dispose();
            SetIdle();
            context.Logger.LogInformation(
                "SocketServer stopped: accepted={Accepted} rejected={Rejected} sent={Sent} received={Received} dropped={Dropped} errors={Errors}",
                Interlocked.Read(ref _acceptedClients),
                Interlocked.Read(ref _rejectedClients),
                Interlocked.Read(ref _sentMessages),
                Interlocked.Read(ref _receivedMessages),
                Interlocked.Read(ref _droppedMessages),
                Interlocked.Read(ref _clientErrors));
        }
        finally
        {
            _stopLock.Release();
        }
    }

    private int ReadBoundedInt(INodeContext context, string name, int fallback, int min, int max)
    {
        var value = ParameterValues.GetInt(Param(name).Value, fallback);
        if (value < min || value > max)
        {
            context.Logger.LogWarning(
                "SocketServer parameter {Name}={Value} is outside [{Min}, {Max}]; using the nearest valid value.",
                name, value, min, max);
            value = Math.Clamp(value, min, max);
        }
        return value;
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(
        StreamReader reader, int timeoutSeconds, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await reader.ReadLineAsync(linkedCts.Token).AsTask();
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
            await stream.WriteAsync(bytes, linkedCts.Token);
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
            $"SocketServer Host '{host}' is not a local IP address. Use 0.0.0.0, 127.0.0.1 or a local IP.");
    }

    private static string NormalizeLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');

    private sealed class ClientSession
    {
        public ClientSession(TcpClient client) => Client = client;

        public TcpClient Client { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
