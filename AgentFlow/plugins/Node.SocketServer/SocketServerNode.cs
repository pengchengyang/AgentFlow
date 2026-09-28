// -----------------------------------------------------------------------
// <copyright company="Your Company" file="SocketServerNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.SocketServer;

/// <summary>
/// TCP socket server node. One-shot request/response flow:
/// listen on <c>Host:Port</c>, accept a client, read one line and emit it on the
/// <c>Received</c> output pin, then reply with the non-empty <c>Send1..Send4</c>
/// input values (one per line; falls back to the <c>Response</c> parameter) before closing.
/// </summary>
public sealed class SocketServerNode : BaseNode
{
    private string _host = "127.0.0.1";
    private int _port = 9000;
    private string _response = "pong";
    private TcpListener? _listener;
    private TcpClient? _client;

    public override string TypeId => "node.socket-server";
    public override string DisplayName => "Socket Server";
    public override string Category => "Network";

    public SocketServerNode()
    {
        Uuid = "node.socket-server";
        AddInputPin(new("Send1", typeof(string), PinDirection.Input, required: false));
        AddInputPin(new("Send2", typeof(string), PinDirection.Input, required: false));
        AddInputPin(new("Send3", typeof(string), PinDirection.Input, required: false));
        AddInputPin(new("Send4", typeof(string), PinDirection.Input, required: false));
        AddOutputPin(new("Received", typeof(string), PinDirection.Output));
    }

    public override IReadOnlyList<ParameterDefinition> Parameters =>
    [
        new("Host", typeof(string), "Host", "127.0.0.1", "Interface / host address to bind"),
        new("Port", typeof(int), "Port", 9000, "TCP port to listen on"),
        new("Response", typeof(string), "Response", "pong", "Reply sent back to the client when no Send input is provided")
    ];

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Host", out var h) && h is not null) _host = h.ToString() ?? _host;
        if (parameters.TryGetValue("Port", out var p) && p is not null) _port = Convert.ToInt32(p);
        if (parameters.TryGetValue("Response", out var r) && r is not null) _response = r.ToString() ?? _response;
    }

    protected override void OnSerializeParameters(JsonObject json)
    {
        json["host"] = _host;
        json["port"] = _port;
        json["response"] = _response;
    }

    protected override void OnDeserializeParameters(JsonObject json)
    {
        _host = json["host"]?.GetValue<string>() ?? _host;
        _port = json["port"]?.GetValue<int>() ?? _port;
        _response = json["response"]?.GetValue<string>() ?? _response;
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _listener = new TcpListener(IPAddress.Parse(_host), _port);
        _listener.Start();
        context.Logger.LogInformation("SocketServer listening on {Host}:{Port}", _host, _port);
        return Task.CompletedTask;
    }

    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        try
        {
            using var client = await _listener!.AcceptTcpClientAsync();
            _client = client;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = await reader.ReadLineAsync(ct) ?? "";
            context.SetOutput("Received", line);
            context.Logger.LogInformation("SocketServer received: {Line}", line);

            var sends = new[]
            {
                context.GetInput<string>("Send1"),
                context.GetInput<string>("Send2"),
                context.GetInput<string>("Send3"),
                context.GetInput<string>("Send4"),
            }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            if (sends.Count == 0)
                sends.Add(_response);

            var payload = string.Join("\n", sends) + "\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            await stream.WriteAsync(bytes, ct);
            context.Logger.LogInformation("SocketServer replied: {Reply}", string.Join("|", sends));
        }
        finally
        {
            Running = false;
        }
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _client?.Dispose();
        _client = null;
        _listener?.Stop();
        _listener = null;
        return Task.CompletedTask;
    }
}
