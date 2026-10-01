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
/// TCP multi-client server node, modelled on <c>TcpMultiServer</c>: bind to <c>Port</c>,
/// then a single background thread runs a <c>while</c> loop that accepts clients. Every
/// accepted client gets its own read task (tracked by id in a concurrent dictionary) that
/// reads raw bytes and emits each message on the <c>Received</c> output pin, replying with
/// the non-empty <c>Send1..Send4</c> input values (or the <c>Response</c> parameter).
/// </summary>
public sealed class SocketServerNode : BaseNode
{
    private readonly ConcurrentDictionary<Guid, TcpClient> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private const int BufferSize = 4096;

    private readonly object _sendLock = new();
    private string _send1 = "";
    private string _send2 = "";
    private string _send3 = "";
    private string _send4 = "";

    public override string FunctionName => "node.socket-server";
    public override string DisplayName => "Socket Server";
    public override string Category => "Network";

    /// <summary>Send1 input pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin Send1Pin { get; } = new("Send1", typeof(string), PinDirection.Input, required: false);

    /// <summary>Send2 input pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin Send2Pin { get; } = new("Send2", typeof(string), PinDirection.Input, required: false);

    /// <summary>Send3 input pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin Send3Pin { get; } = new("Send3", typeof(string), PinDirection.Input, required: false);

    /// <summary>Send4 input pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin Send4Pin { get; } = new("Send4", typeof(string), PinDirection.Input, required: false);

    /// <summary>Received output pin, kept as a property so its parameter value can be saved.</summary>
    public BasePin ReceivedPin { get; } = new("Received", typeof(string), PinDirection.Output);

    public SocketServerNode()
    {
        Uuid = "node.socket-server";
        AddInputPin(Send1Pin);
        AddInputPin(Send2Pin);
        AddInputPin(Send3Pin);
        AddInputPin(Send4Pin);
        AddOutputPin(ReceivedPin);
    }

    /// <summary>Declare the node's parameters.</summary>
    protected override void AddParam()
    {
        AddParameter(new NodeParameter("Host", typeof(string), "0.0.0.0", isEditable: true, group: "General"));
        AddParameter(new NodeParameter("Port", typeof(int), 9000, isEditable: true, group: "General"));
        AddParameter(new NodeParameter("Response", typeof(string), "pong", isEditable: true, group: "General"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Host", out var h) && h is not null) Param("Host").Value = h.ToString() ?? "";
        if (parameters.TryGetValue("Port", out var p) && p is not null) Param("Port").Value = Convert.ToInt32(p);
        if (parameters.TryGetValue("Response", out var r) && r is not null) Param("Response").Value = r.ToString() ?? "";
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    public override void Receive(INodeContext context, BasePin pin, object? value)
    {
        if (value is not string s) return;
        lock (_sendLock)
        {
            if (pin.Id == Send1Pin.Id) _send1 = s;
            else if (pin.Id == Send2Pin.Id) _send2 = s;
            else if (pin.Id == Send3Pin.Id) _send3 = s;
            else if (pin.Id == Send4Pin.Id) _send4 = s;
        }
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        // Bind to any address (as in TcpMultiServer); Host is kept as a node-standard param.
        _listener = new TcpListener(IPAddress.Any, Convert.ToInt32(Param("Port").Value));
        _listener.Start();
        context.Logger.LogInformation("SocketServer listening on port {Port}",
            ((IPEndPoint)_listener.LocalEndpoint).Port);
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(context, _cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>One thread, one <c>while</c> loop: keep accepting clients until stopped.</summary>
    private async Task AcceptLoopAsync(INodeContext context, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(context, client, ct)); // per-client task, non-blocking
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped / cancelled.
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            context.Logger.LogInformation("SocketServer stopped: {Message}", ex.Message);
        }
    }

    /// <summary>Read bytes from one client, emit each message, and reply to that client.</summary>
    private async Task HandleClientAsync(INodeContext context, TcpClient client, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients.TryAdd(id, client);
        var ep = (IPEndPoint?)client.Client.RemoteEndPoint;
        var address = ep?.Address.ToString() ?? "?";
        context.Logger.LogInformation("SocketServer client connected: {Id} ({Address})", id, address);

        try
        {
            using var stream = client.GetStream();
            var buffer = new byte[BufferSize];

            while (!ct.IsCancellationRequested && client.Connected)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                    break; // peer closed

                var msg = Encoding.UTF8.GetString(buffer, 0, read);
                if (msg != null)
                {
                    ReceivedPin.Send(msg);
                }
                context.SetOutput("Received", msg);
                context.Logger.LogInformation("SocketServer received from {Id}: {Msg}", id, msg);

                var reply = GetReply();
                if (!string.IsNullOrEmpty(reply))
                    await SendToAsync(id, msg);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning("SocketServer client {Id} error: {Message}", id, ex.Message);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
            context.Logger.LogInformation("SocketServer client disconnected: {Id}", id);
        }
    }

    /// <summary>Snapshot of the current reply payload (Send1..4, or Response when all empty).</summary>
    private string GetReply()
    {
        lock (_sendLock)
        {
            var sends = new[] { _send1, _send2, _send3, _send4 }
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (sends.Count == 0)
                return Param("Response").Value?.ToString() ?? "";
            return string.Join("\n", sends);
        }
    }

    /// <summary>Send a message to a specific client.</summary>
    private async Task SendToAsync(Guid clientId, string message)
    {
        if (!_clients.TryGetValue(clientId, out var client) || !client.Connected)
            return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await client.GetStream().WriteAsync(bytes);
        }
        catch
        {
            // Single failure does not affect others.
        }
    }

    /// <summary>Broadcast a message to all connected clients.</summary>
    private async Task BroadcastAsync(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        foreach (var client in _clients.Values)
        {
            try { await client.GetStream().WriteAsync(bytes); }
            catch { /* single failure does not affect others */ }
        }
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        _cts?.Cancel();
        _listener?.Stop();
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();

        if (_acceptTask is not null)
        {
            try { await _acceptTask; } catch { /* already terminated */ }
            _acceptTask = null;
        }
        _cts?.Dispose();
        _cts = null;
        Running = false;
    }
}
