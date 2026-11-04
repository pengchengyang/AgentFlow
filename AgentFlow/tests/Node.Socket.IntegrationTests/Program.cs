using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;
using Node.SocketClient;
using Node.SocketServer;

try
{
    await RunTestsAsync();
    Console.WriteLine("ALL PASS");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex.Message}");
    return 1;
}

static async Task RunTestsAsync()
{
    var logs = new ConcurrentQueue<string>();
    var loggerFactory = new CaptureLoggerFactory(logs);

    // 1) Newline protocol, client Send input, server Send1 reply, and stop summaries.
    var port = GetFreePort();
    var server = new SocketServerNode();
    server.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = port,
        ["Response"] = "pong",
        ["MaxClients"] = 4,
        ["ReadTimeoutSeconds"] = 60,
        ["WriteTimeoutSeconds"] = 10,
    });
    var serverContext = new TestContext(loggerFactory.CreateLogger("server"));
    await server.Initialize(serverContext);
    await server.Run(serverContext);

    var client = new SocketClientNode();
    client.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = port,
        ["Payload"] = "ping",
        ["ConnectTimeoutSeconds"] = 1,
        ["MaxConnectAttempts"] = 3,
        ["SendQueueCapacity"] = 16,
        ["ReadTimeoutSeconds"] = 0,
        ["WriteTimeoutSeconds"] = 10,
    });
    var clientContext = new TestContext(loggerFactory.CreateLogger("client"));
    await client.Initialize(clientContext);
    await client.Run(clientContext);

    await WaitUntil(() => HasLine(logs, "server", "SocketServer received", "ping"),
        "server did not receive initial payload");
    await WaitUntil(() => clientContext.Outputs.Contains("pong"),
        "client did not receive server reply");

    var hello = StringSample.CreateSample();
    hello.SetValue("hello", "Send");
    client.ReceiveSample(client._inPin, hello);
    await WaitUntil(() => HasLine(logs, "server", "SocketServer received", "hello"),
        "Send input did not reach the server");
    Assert(!clientContext.Outputs.Contains("hello"), "Send input was echoed to Received");

    var customReply = StringSample.CreateSample();
    customReply.SetValue("custom-reply", "Send1");
    server.ReceiveSample(server.Send1Pin, customReply);
    var again = StringSample.CreateSample();
    again.SetValue("again", "Send");
    client.ReceiveSample(client._inPin, again);
    await WaitUntil(() => clientContext.Outputs.Contains("custom-reply"),
        "SocketServer ignored Send1 reply");
    Check("protocol / Send / reply");

    // 2) Duplicate port fails while the first listener is alive.
    var duplicate = new SocketServerNode();
    duplicate.Configure(new Dictionary<string, object?> { ["Host"] = "127.0.0.1", ["Port"] = port });
    await AssertThrowsAsync<InvalidOperationException>(
        () => duplicate.Initialize(new TestContext(loggerFactory.CreateLogger("duplicate"))),
        "duplicate port was accepted");
    Check("duplicate port rejection");

    // 3) MaxClients rejects a second connection.
    var capPort = GetFreePort();
    var capServer = new SocketServerNode();
    capServer.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = capPort,
        ["MaxClients"] = 1,
    });
    var capContext = new TestContext(loggerFactory.CreateLogger("cap"));
    await capServer.Initialize(capContext);
    await capServer.Run(capContext);

    var capClient = new SocketClientNode();
    capClient.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = capPort,
        ["Payload"] = "cap-ping",
    });
    var capClientContext = new TestContext(loggerFactory.CreateLogger("cap-client"));
    await capClient.Initialize(capClientContext);
    await capClient.Run(capClientContext);
    await WaitUntil(() => HasLine(logs, "cap", "SocketServer received", "cap-ping"),
        "capacity test client did not connect");

    using (var second = new TcpClient())
    {
        await second.ConnectAsync(IPAddress.Loopback, capPort);
        await WaitUntil(() => HasLine(logs, "cap", "rejected"),
            "MaxClients rejection was not logged");
    }
    await capClient.Stop(capClientContext);
    await capServer.Stop(capContext);
    Check("MaxClients");

    // 4) Invalid Host reports a startup error.
    var invalidHost = new SocketServerNode();
    invalidHost.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "not-a-local-ip",
        ["Port"] = GetFreePort(),
    });
    await AssertThrowsAsync<InvalidOperationException>(
        () => invalidHost.Initialize(new TestContext(loggerFactory.CreateLogger("invalid-host"))),
        "invalid Host was accepted");
    Check("invalid Host");

    // 5) Server read timeout closes an idle client; the client reports Failed.
    var timeoutPort = GetFreePort();
    var timeoutServer = new SocketServerNode();
    timeoutServer.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = timeoutPort,
        ["ReadTimeoutSeconds"] = 1,
        ["Response"] = "timeout-test",
    });
    var timeoutContext = new TestContext(loggerFactory.CreateLogger("timeout-server"));
    await timeoutServer.Initialize(timeoutContext);
    await timeoutServer.Run(timeoutContext);

    var timeoutClient = new SocketClientNode();
    timeoutClient.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = timeoutPort,
        ["Payload"] = "timeout-ping",
        ["ReadTimeoutSeconds"] = 0,
    });
    var timeoutClientContext = new TestContext(loggerFactory.CreateLogger("timeout-client"));
    await timeoutClient.Initialize(timeoutClientContext);
    await timeoutClient.Run(timeoutClientContext);
    await WaitUntil(() => timeoutClient.Failed, "client did not observe server idle timeout", 5);
    await timeoutClient.Stop(timeoutClientContext);
    await timeoutServer.Stop(timeoutContext);
    Check("read timeout");

    // 6) Stop is idempotent and reports a summary.
    await server.Stop(serverContext);
    await server.Stop(serverContext);
    await client.Stop(clientContext);
    await client.Stop(clientContext);
    await WaitUntil(() => logs.Any(line => line.Contains("[server] SocketServer stopped:", StringComparison.Ordinal)),
        "server stop summary was not logged");
    await WaitUntil(() => logs.Any(line => line.Contains("[client] SocketClient stopped:", StringComparison.Ordinal)),
        "client stop summary was not logged");
    Check("idempotent Stop and summary");

    // 7) Stop during a bounded connect retry returns promptly.
    var deadPort = GetFreePort();
    var connectingClient = new SocketClientNode();
    connectingClient.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = deadPort,
        ["ConnectTimeoutSeconds"] = 1,
        ["MaxConnectAttempts"] = 100,
    });
    var connectingContext = new TestContext(loggerFactory.CreateLogger("connecting"));
    await connectingClient.Initialize(connectingContext);
    var runTask = Task.Run(() => connectingClient.Run(connectingContext));
    await Task.Delay(150);
    await connectingClient.Stop(connectingContext);
    var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(3)));
    Assert(completed == runTask, "Stop during connect did not complete Run");
    await runTask;
    Check("Stop during connect");
}

static bool HasLine(ConcurrentQueue<string> logs, string category, params string[] fragments)
    => logs.Any(line =>
        line.StartsWith($"[{category}]", StringComparison.Ordinal) &&
        fragments.All(fragment => line.Contains(fragment, StringComparison.Ordinal)));

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new Exception(message);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static void Check(string name) => Console.WriteLine($"PASS {name}");

static async Task WaitUntil(Func<bool> condition, string message, int seconds = 8)
{
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (!condition())
    {
        if (DateTime.UtcNow > deadline)
            throw new TimeoutException(message);
        await Task.Delay(10);
    }
}

static int GetFreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

internal sealed class TestContext(ILogger logger) : INodeContext
{
    public ILogger Logger { get; } = logger;
    public ConcurrentQueue<string> Outputs { get; } = new();

    public T? GetInput<T>(string pinName) => default;
    public bool HasInput(string pinName) => false;
    public bool TryGetInput<T>(string pinName, out T? value)
    {
        value = default;
        return false;
    }
    public Task<Sample?> WaitForInputAsync(string pinName, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Task.FromResult<Sample?>(null);
    public void SetOutput(string pinName, Sample? value)
    {
        if (value is StringSample ss && ss.StringValue is { } text)
            Outputs.Enqueue(text);
    }
    public T? GetService<T>() => default;
}

internal sealed class CaptureLoggerFactory(ConcurrentQueue<string> sink) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, sink);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}

internal sealed class CaptureLogger(string categoryName, ConcurrentQueue<string> sink) : ILogger
{
    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        sink.Enqueue($"[{categoryName}] {formatter(state, exception)}");
    }
}
