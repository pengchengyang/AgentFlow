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
/// TCP socket client node. Persistent, continuous two-way communication (the C# analogue of
/// the C++ WSAEventSelect client): connect to <c>Host:Port</c>, then a single background
/// thread runs one <c>while</c> loop that keeps talking to the server — it reads lines coming
/// back (emitting each on the <c>Received</c> output pin) and writes outbound lines queued on
/// the <c>Send</c> input pin. The connection stays open until the node is stopped.
/// </summary>
public sealed class SocketClientNode : BaseNode
{
    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _commTask;

    // Outbound messages, written by Receive() (input pin) or the initial payload and drained
    // by the communication loop. Thread-safe for any producer thread.
    private readonly Channel<string> _sendChannel = Channel.CreateUnbounded<string>();
    private string _sendValue = "";

    public override string Name => "node.socket-client";
    public override string DisplayName { get; set; } = "Socket Client";
    public override string Category => "Network";

    /// <summary>Send input pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin _inPin { get; } = new("Send", typeof(string), PinDirection.Input, required: false);

    /// <summary>Received output pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin _outPin { get; } = new("Received", typeof(string), PinDirection.Output);

    public SocketClientNode()
    {
        Uuid = "DCB15DED-9BDF-4C44-858F-560261B685D7";
        AddInputPin(_inPin);
        AddOutputPin(_outPin);
    }

    /// <summary>
    /// Declare the node's parameters. Called by the base constructor; each declared
    /// parameter is automatically surfaced in the property panel and serialized /
    /// deserialized to JSON by <see cref="BaseNode"/>.
    /// </summary>
    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter("Host", typeof(string), "127.0.0.1", isEditable: true, group: "Settings"));
        AddParameter(new NodeParameter("Port", typeof(int), 9000, isEditable: true, group: "Settings"));
        AddParameter(new NodeParameter("Payload", typeof(string), "ping", isEditable: true, group: "Settings"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Host", out var h) && h is not null) Param("Host").Value = h.ToString() ?? "";
        if (parameters.TryGetValue("Port", out var p) && p is not null) Param("Port").Value = Convert.ToInt32(p);
        if (parameters.TryGetValue("Payload", out var pl) && pl is not null) Param("Payload").Value = pl.ToString() ?? "";
    }

    /// <summary>Look up a declared parameter by name.</summary>
    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    /// <summary>
    /// Called when the Send input pin receives a value from an upstream node: enqueue it so the
    /// communication loop writes it to the server (the C# analogue of <c>sendData</c>).
    /// </summary>
    public override void Receive(INodeContext context, BasePin pin, object? value)
    {
        if (pin.Id == _inPin.Id && value is string s)
        {
            _sendValue = s;
        }
        _outPin.Send(_sendValue);
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Connect to the server, then start a single background thread whose <c>while</c> loop
    /// continuously communicates with the server. Returns once connected (the loop keeps
    /// running until <see cref="Stop"/>).
    /// </summary>
    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        try
        {
            var client = new TcpClient();
            _client = client;
            await client.ConnectAsync(
                Param("Host").Value?.ToString() ?? "127.0.0.1",
                Convert.ToInt32(Param("Port").Value), ct);
            context.Logger.LogInformation("SocketClient connected to {Host}:{Port}",
                Param("Host").Value?.ToString(), Param("Port").Value);

            _cts = new CancellationTokenSource();
            _commTask = Task.Run(() => CommLoopAsync(context, _cts.Token));

            // Queue the initial payload; the communication loop sends it (and keeps talking).
            var payload = !string.IsNullOrEmpty(_sendValue)
                ? _sendValue
                : Param("Payload").Value?.ToString() ?? "ping";
            _sendChannel.Writer.TryWrite(payload);
            context.Logger.LogInformation("SocketClient queued payload: {Payload}", payload);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            context.Logger.LogError("SocketClient error: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// One thread, one <c>while</c> loop: keep one pending read from the server, and whenever
    /// either a server line arrives or an outbound message is queued, handle it — so receive
    /// and send happen continuously on the same loop without blocking each other.
    /// </summary>
    private async Task CommLoopAsync(INodeContext context, CancellationToken ct)
    {
        var client = _client;
        if (client is null) return;

        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true, NewLine = "\n" };

            Task<string?> readTask = reader.ReadLineAsync(ct).AsTask();

            while (!ct.IsCancellationRequested)
            {
                // Wait for either the next server line or a queued outbound message.
                var pendingSend = _sendChannel.Reader.WaitToReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(readTask, pendingSend);

                if (completed == readTask)
                {
                    var line = await readTask;
                    if (line is null)
                        break; // server closed the connection

                    context.SetOutput("Received", line);
                    context.Logger.LogInformation("SocketClient received: {Line}", line);
                    readTask = reader.ReadLineAsync(ct).AsTask(); // re-arm the pending read
                }
                else
                {
                    // Drain everything queued for sending.
                    while (_sendChannel.Reader.TryRead(out var msg))
                    {
                        await writer.WriteLineAsync(msg.AsMemory(), ct);
                        context.Logger.LogInformation("SocketClient sent: {Msg}", msg);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException or OperationCanceledException)
        {
            // Connection closed, or the node was stopped / cancelled.
            context.Logger.LogInformation("SocketClient connection ended: {Message}", ex.Message);
        }
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        _cts?.Cancel();
        _sendChannel.Writer.TryComplete();
        _client?.Dispose();
        _client = null;
        if (_commTask is not null)
        {
            try { await _commTask; } catch { /* already terminated */ }
            _commTask = null;
        }
        _cts?.Dispose();
        _cts = null;
        Running = false;
    }
}
