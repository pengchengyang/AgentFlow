// -----------------------------------------------------------------------
// <copyright company="Your Company" file="SocketClientNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.SocketClient;

/// <summary>
/// TCP socket client node. Persistent, continuous two-way communication: connect to
/// <c>Host:Port</c>, then one read loop and one write loop keep talking to the server.
/// Messages are newline-delimited UTF-8 text (one message per line).
/// </summary>
public sealed class SocketClientNode : BaseNode
{
    private const string ProtocolVersion = "1";
    private const int MaxMessageLength = 8192;
    private const int DefaultQueueCapacity = 256;
    private const int DefaultConnectTimeoutSeconds = 5;
    private const int DefaultMaxConnectAttempts = 5;
    private const int DefaultReadTimeoutSeconds = 0;
    private const int DefaultWriteTimeoutSeconds = 10;

    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _commTask;
    private Channel<string> _sendChannel = CreateSendChannel(DefaultQueueCapacity);
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private long _sentMessages;
    private long _receivedMessages;
    private long _droppedMessages;

    public override string Name => "Socket Client";
    public override string DisplayName { get; set; } = string.Empty;
    public override string Category => "Network";

    /// <summary>Send input pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin _inPin { get; } = new("Send", DataType.STRING, PinDirection.Input, required: false);

    /// <summary>Received output pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin _outPin { get; } = new("Received", DataType.STRING, PinDirection.Output);

    public SocketClientNode()
    {
        Uuid = "DCB15DED-9BDF-4C44-858F-560261B685D7";
        AddParam();
        AddPins();
    }

    /// <summary>Create the node's input / output pins (required by <see cref="BaseNode"/>).</summary>
    public override void AddPins()
    {
        AddInputPin(_inPin);
        AddOutputPin(_outPin);
    }

    /// <summary>Declare the node's parameters.</summary>
    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter("Host", typeof(string), "127.0.0.1", isEditable: true, group: "General", zone: "ClientZone"));
        AddParameter(new NodeParameter("Port", typeof(int), 9000, isEditable: true, group: "General", zone: "ClientZone"));
        AddParameter(new NodeParameter("Payload", typeof(string), "ping", isEditable: true, group: "General", zone: "ClientZone"));
        AddParameter(new NodeParameter("ConnectTimeoutSeconds", typeof(int), DefaultConnectTimeoutSeconds, isEditable: true, group: "Settings", zone: "ClientZone"));
        AddParameter(new NodeParameter("MaxConnectAttempts", typeof(int), DefaultMaxConnectAttempts, isEditable: true, group: "Settings", zone: "ClientZone"));
        AddParameter(new NodeParameter("SendQueueCapacity", typeof(int), DefaultQueueCapacity, isEditable: true, group: "Settings", zone: "ClientZone"));
        AddParameter(new NodeParameter("ReadTimeoutSeconds", typeof(int), DefaultReadTimeoutSeconds, isEditable: true, group: "Settings", zone: "ClientZone"));
        AddParameter(new NodeParameter("WriteTimeoutSeconds", typeof(int), DefaultWriteTimeoutSeconds, isEditable: true, group: "Settings", zone: "ClientZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Host", out var h) && h is not null) Param("Host").Value = h.ToString() ?? "";
        if (parameters.TryGetValue("Port", out var p) && p is not null) Param("Port").Value = ParameterValues.GetInt(p, 9000);
        if (parameters.TryGetValue("Payload", out var pl) && pl is not null) Param("Payload").Value = pl.ToString() ?? "";
        if (parameters.TryGetValue("ConnectTimeoutSeconds", out var t) && t is not null)
            Param("ConnectTimeoutSeconds").Value = ParameterValues.GetInt(t, DefaultConnectTimeoutSeconds);
        if (parameters.TryGetValue("MaxConnectAttempts", out var a) && a is not null)
            Param("MaxConnectAttempts").Value = ParameterValues.GetInt(a, DefaultMaxConnectAttempts);
        if (parameters.TryGetValue("SendQueueCapacity", out var q) && q is not null)
            Param("SendQueueCapacity").Value = ParameterValues.GetInt(q, DefaultQueueCapacity);
        if (parameters.TryGetValue("ReadTimeoutSeconds", out var rt) && rt is not null)
            Param("ReadTimeoutSeconds").Value = ParameterValues.GetInt(rt, DefaultReadTimeoutSeconds);
        if (parameters.TryGetValue("WriteTimeoutSeconds", out var wt) && wt is not null)
            Param("WriteTimeoutSeconds").Value = ParameterValues.GetInt(wt, DefaultWriteTimeoutSeconds);
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    /// <summary>
    /// Called when the Send input pin receives a value from an upstream node. The value is
    /// queued for the write loop; it is not echoed onto the Received output pin.
    /// </summary>
    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (pin.Name != _inPin.Name || value is not StringSample ss || ss.StringValue is not { } s)
            return;

        var line = NormalizeLine(s);
        if (line.Length == 0 || line.Length > MaxMessageLength)
        {
            Interlocked.Increment(ref _droppedMessages);
            return;
        }

        if (!_sendChannel.Writer.TryWrite(line))
            Interlocked.Increment(ref _droppedMessages);
    }

    /// <summary>
    /// Node-specific right-click commands. "Send Payload" is enabled only while the node is
    /// running and enqueues the configured <c>Payload</c> through the same send channel used
    /// by the write loop.
    /// </summary>
    protected override void AddCommands()
    {
        AddCommand(new NodeCommand
        {
            Id = "send-payload",
            Title = "Send Payload",
            Order = 10,
            CanExecute = () => Running,
            Execute = (ctx, _) =>
            {
                var payload = NormalizeLine(Param("Payload").Value?.ToString() ?? string.Empty);
                if (payload.Length == 0)
                {
                    ctx.Logger.LogWarning("SocketClient ignored an empty payload from the context menu.");
                    return Task.CompletedTask;
                }

                if (payload.Length > MaxMessageLength)
                {
                    ctx.Logger.LogWarning("SocketClient ignored an oversized payload from the context menu.");
                    return Task.CompletedTask;
                }

                var queued = _sendChannel.Writer.TryWrite(payload);
                if (queued)
                    ctx.Logger.LogInformation("SocketClient queued payload from context menu: {Payload}", payload);
                else
                {
                    Interlocked.Increment(ref _droppedMessages);
                    ctx.Logger.LogWarning("SocketClient could not queue payload from context menu: queue is full or closed.");
                }
                return Task.CompletedTask;
            }
        });
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Connect with bounded retries, then start the read and write loops. Returns once the
    /// connect attempt has succeeded (the loops keep running until <see cref="Stop"/>).
    /// </summary>
    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        SetConnecting();

        // Stop() cancels this source too, so it can interrupt a connect attempt / retry delay.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var runToken = runCts.Token;

        var queueCapacity = ReadBoundedInt(context, "SendQueueCapacity", DefaultQueueCapacity, 1, 4096);
        _sendChannel = CreateSendChannel(queueCapacity);
        Interlocked.Exchange(ref _sentMessages, 0);
        Interlocked.Exchange(ref _receivedMessages, 0);
        Interlocked.Exchange(ref _droppedMessages, 0);

        var host = Param("Host").Value?.ToString()?.Trim() ?? "";
        var port = ParameterValues.GetInt(Param("Port").Value, 0);
        if (host.Length == 0)
        {
            SetFailed();
            throw new InvalidOperationException("SocketClient Host must not be empty.");
        }
        if (port is < 1 or > 65535)
        {
            SetFailed();
            throw new InvalidOperationException($"SocketClient Port must be between 1 and 65535 (got {port}).");
        }

        var timeoutSeconds = ReadBoundedInt(
            context, "ConnectTimeoutSeconds", DefaultConnectTimeoutSeconds, 1, 60);
        var maxAttempts = ReadBoundedInt(
            context, "MaxConnectAttempts", DefaultMaxConnectAttempts, 1, 100);
        var readTimeoutSeconds = ReadBoundedInt(
            context, "ReadTimeoutSeconds", DefaultReadTimeoutSeconds, 0, 3600);
        var writeTimeoutSeconds = ReadBoundedInt(
            context, "WriteTimeoutSeconds", DefaultWriteTimeoutSeconds, 1, 120);

        TcpClient? connected = null;
        for (var attempt = 1; attempt <= maxAttempts && !runToken.IsCancellationRequested; attempt++)
        {
            // A failed TcpClient is not reused; each attempt gets a fresh instance.
            var client = new TcpClient();
            _client = client;

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(runToken, timeoutCts.Token);
                await client.ConnectAsync(host, port, attemptCts.Token);
                connected = client;
                break;
            }
            catch (Exception) when (runToken.IsCancellationRequested)
            {
                // Stopped while connecting or during a retry delay.
                client.Dispose();
                _client = null;
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException or ArgumentException)
            {
                client.Dispose();
                _client = null;

                var isLastAttempt = attempt >= maxAttempts;
                context.Logger.LogWarning(
                    "SocketClient could not connect to {Host}:{Port} (attempt {Attempt}/{Attempts}): {Message}{Retry}",
                    host, port, attempt, maxAttempts, ex.Message,
                    isLastAttempt ? "." : "; retrying in 1s");

                if (isLastAttempt)
                {
                    SetFailed();
                    throw new InvalidOperationException(
                        $"SocketClient could not connect to {host}:{port} after {maxAttempts} attempt(s).", ex);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), runToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        if (connected is null)
        {
            if (!runToken.IsCancellationRequested)
            {
                SetFailed();
                throw new InvalidOperationException($"SocketClient could not connect to {host}:{port}.");
            }
            return;
        }

        _client = connected;
        Running = true;
        context.Logger.LogInformation(
            "SocketClient connected to {Host}:{Port} (protocol v{Version}, readTimeout={ReadTimeout}s, writeTimeout={WriteTimeout}s)",
            host, port, ProtocolVersion, readTimeoutSeconds, writeTimeoutSeconds);

        // Read and write are independent loops on the duplex stream; this avoids leaving a
        // pending WaitToReadAsync task behind on every read/send iteration.
        _commTask = Task.WhenAll(
            ReadLoopAsync(context, connected, readTimeoutSeconds, runToken),
            WriteLoopAsync(context, connected, writeTimeoutSeconds, runToken));

        var initialPayload = NormalizeLine(Param("Payload").Value?.ToString() ?? string.Empty);
        if (initialPayload.Length == 0)
        {
            context.Logger.LogWarning("SocketClient initial Payload is empty; nothing was queued.");
        }
        else if (initialPayload.Length > MaxMessageLength)
        {
            context.Logger.LogWarning("SocketClient initial Payload is longer than {Max} characters; nothing was queued.", MaxMessageLength);
        }
        else if (!_sendChannel.Writer.TryWrite(initialPayload))
        {
            Interlocked.Increment(ref _droppedMessages);
            context.Logger.LogWarning("SocketClient could not queue the initial Payload: queue is full or closed.");
        }
        else
        {
            context.Logger.LogTrace("SocketClient queued payload: {Payload}", initialPayload);
        }
    }

    private async Task ReadLoopAsync(
        INodeContext context, TcpClient client, int readTimeoutSeconds, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!ct.IsCancellationRequested)
            {
                var line = await ReadLineWithTimeoutAsync(reader, readTimeoutSeconds, ct);
                if (line is null)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        context.Logger.LogWarning("SocketClient server closed the connection.");
                        SetFailed();
                    }
                    break;
                }

                if (line.Length > MaxMessageLength)
                {
                    Interlocked.Increment(ref _droppedMessages);
                    context.Logger.LogWarning(
                        "SocketClient dropped an oversized line ({Length} characters; max {Max}).",
                        line.Length, MaxMessageLength);
                    continue;
                }

                var recvSample = StringSample.CreateSample();
                recvSample.SetValue(line, "Received");
                context.SetOutput("Received", recvSample);
                Interlocked.Increment(ref _receivedMessages);
                context.Logger.LogTrace("SocketClient received: {Line}", line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal stop.
        }
        catch (TimeoutException ex)
        {
            if (!ct.IsCancellationRequested)
            {
                context.Logger.LogWarning("SocketClient read timeout: {Message}", ex.Message);
                SetFailed();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested)
            {
                context.Logger.LogWarning("SocketClient connection ended: {Message}", ex.Message);
                SetFailed();
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                context.Logger.LogError(ex, "SocketClient read loop failed.");
                SetFailed();
            }
        }
        finally
        {
            // Ends the write loop as well once the connection is gone or the node is stopped.
            _sendChannel.Writer.TryComplete();
        }
    }

    private async Task WriteLoopAsync(
        INodeContext context, TcpClient client, int writeTimeoutSeconds, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true, NewLine = "\n" };

            await foreach (var msg in _sendChannel.Reader.ReadAllAsync(ct))
            {
                var line = NormalizeLine(msg);
                if (line.Length == 0 || line.Length > MaxMessageLength)
                {
                    Interlocked.Increment(ref _droppedMessages);
                    continue;
                }

                await WriteLineWithTimeoutAsync(writer, line, writeTimeoutSeconds, ct);
                Interlocked.Increment(ref _sentMessages);
                context.Logger.LogTrace("SocketClient sent: {Msg}", line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal stop.
        }
        catch (TimeoutException ex)
        {
            if (!ct.IsCancellationRequested)
            {
                context.Logger.LogWarning("SocketClient write timeout: {Message}", ex.Message);
                SetFailed();
                TryCancel();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested)
            {
                context.Logger.LogWarning("SocketClient write failed: {Message}", ex.Message);
                SetFailed();
                TryCancel();
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                context.Logger.LogError(ex, "SocketClient write loop failed.");
                SetFailed();
                TryCancel();
            }
        }
        finally
        {
            _sendChannel.Writer.TryComplete();
        }
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        await _stopLock.WaitAsync();
        try
        {
            var cts = _cts;
            _cts = null;
            TryCancel(cts);
            _sendChannel.Writer.TryComplete();
            _client?.Dispose();
            _client = null;

            var comm = _commTask;
            _commTask = null;
            if (comm is not null)
            {
                try
                {
                    await comm.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    context.Logger.LogWarning("SocketClient did not stop its communication loops within 2s.");
                }
                catch
                {
                    // Loops already terminated / cancellation is expected.
                }
            }

            cts?.Dispose();
            SetIdle();
            context.Logger.LogInformation(
                "SocketClient stopped: sent={Sent} received={Received} dropped={Dropped}",
                Interlocked.Read(ref _sentMessages),
                Interlocked.Read(ref _receivedMessages),
                Interlocked.Read(ref _droppedMessages));
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
                "SocketClient parameter {Name}={Value} is outside [{Min}, {Max}]; using the nearest valid value.",
                name, value, min, max);
            value = Math.Clamp(value, min, max);
        }
        return value;
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(
        StreamReader reader, int timeoutSeconds, CancellationToken ct)
    {
        if (timeoutSeconds <= 0)
            return await reader.ReadLineAsync(ct).AsTask();

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

    private static async Task WriteLineWithTimeoutAsync(
        StreamWriter writer, string line, int timeoutSeconds, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), linkedCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"write did not complete within {timeoutSeconds}s");
        }
    }

    private void TryCancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* Stop already disposed it. */ }
    }

    private static void TryCancel(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* Already disposed by a concurrent Stop. */ }
    }

    private static Channel<string> CreateSendChannel(int capacity) =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    /// <summary>The protocol is one UTF-8 message per line; embedded line breaks are flattened.</summary>
    private static string NormalizeLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');
}
