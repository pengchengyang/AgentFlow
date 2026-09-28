// -----------------------------------------------------------------------
// <copyright company="Your Company" file="SocketClientNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.SocketClient;

/// <summary>
/// TCP socket client node. One-shot request/response flow:
/// connect to <c>Host:Port</c>, send one line (the <c>Send</c> input value or the
/// <c>Payload</c> parameter), read one line and emit it on the <c>Received</c> output
/// pin, then close the connection.
/// </summary>
public sealed class SocketClientNode : BaseNode
{
    private string _host = "127.0.0.1";
    private int _port = 9000;
    private string _payload = "ping";
    private TcpClient? _client;

    public override string TypeId => "node.socket-client";
    public override string DisplayName => "Socket Client";
    public override string Category => "Network";

    public SocketClientNode()
    {
        Uuid = "node.socket-client";
        AddInputPin(new("Send", typeof(string), PinDirection.Input, required: false));
        AddOutputPin(new("Received", typeof(string), PinDirection.Output));
    }

    public override IReadOnlyList<ParameterDefinition> Parameters =>
    [
        new("Host", typeof(string), "Host", "127.0.0.1", "Server host to connect to"),
        new("Port", typeof(int), "Port", 9000, "Server TCP port"),
        new("Payload", typeof(string), "Payload", "ping", "Message sent to the server when no Send input is provided")
    ];

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Host", out var h) && h is not null) _host = h.ToString() ?? _host;
        if (parameters.TryGetValue("Port", out var p) && p is not null) _port = Convert.ToInt32(p);
        if (parameters.TryGetValue("Payload", out var pl) && pl is not null) _payload = pl.ToString() ?? _payload;
    }

    protected override void OnSerializeParameters(JsonObject json)
    {
        json["host"] = _host;
        json["port"] = _port;
        json["payload"] = _payload;
    }

    protected override void OnDeserializeParameters(JsonObject json)
    {
        _host = json["host"]?.GetValue<string>() ?? _host;
        _port = json["port"]?.GetValue<int>() ?? _port;
        _payload = json["payload"]?.GetValue<string>() ?? _payload;
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        try
        {
            using var client = new TcpClient();
            _client = client;
            await client.ConnectAsync(_host, _port, ct);

            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true, NewLine = "\n" };
            var payload = context.GetInput<string>("Send") ?? _payload;
            await writer.WriteLineAsync(payload.AsMemory(), ct);
            context.Logger.LogInformation("SocketClient sent: {Payload}", payload);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = await reader.ReadLineAsync(ct) ?? "";
            context.SetOutput("Received", line);
            context.Logger.LogInformation("SocketClient received: {Line}", line);
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
        return Task.CompletedTask;
    }
}
