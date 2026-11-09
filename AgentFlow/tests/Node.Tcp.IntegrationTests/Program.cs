using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;
using Node.TcpClient;
using Node.TcpServer;

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

    var serverPort = GetFreePort();
    var server = new TcpServerNode();
    AssertPinLayout(server, "TCP Server");
    server.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = serverPort,
        ["MaxClients"] = 4,
        ["SendQueueCapacity"] = 16,
        ["ReadTimeoutSeconds"] = 0,
        ["WriteTimeoutSeconds"] = 2,
        ["ReceiveBufferSize"] = 128,
    });

    var serverContext = new TestContext(loggerFactory.CreateLogger("server"));
    await server.Initialize(serverContext);
    await server.Run(serverContext);

    var serverCapture = new CaptureNode();
    var serverStringInput = serverCapture.InputStringPin;
    var serverBinaryInput = serverCapture.InputBinaryPin;
    ConnectForTest(server.OutputStringPin, serverStringInput);
    ConnectForTest(server.OutputBinaryPin, serverBinaryInput);

    using var serverPeer = new TcpClient();
    await serverPeer.ConnectAsync(IPAddress.Loopback, serverPort);
    var serverPeerStream = serverPeer.GetStream();
    using var serverPeer2 = new TcpClient();
    await serverPeer2.ConnectAsync(IPAddress.Loopback, serverPort);
    var serverPeerStream2 = serverPeer2.GetStream();
    await Task.Delay(150);

    server.ReceiveSample(server.InputStringPin, StringSampleWith("hello utf8"));
    await ReadAtLeastAsync(serverPeerStream, "hello utf8"u8.ToArray(), TimeSpan.FromSeconds(3));
    await ReadAtLeastAsync(serverPeerStream2, "hello utf8"u8.ToArray(), TimeSpan.FromSeconds(3));
    server.ReceiveSample(server.InputBinaryPin, BinSampleWith(new byte[] { 0x01, 0x02, 0xFF }));
    await ReadAtLeastAsync(serverPeerStream, new byte[] { 0x01, 0x02, 0xFF }, TimeSpan.FromSeconds(3));
    await ReadAtLeastAsync(serverPeerStream2, new byte[] { 0x01, 0x02, 0xFF }, TimeSpan.FromSeconds(3));
    Check("server input_string/input_binary broadcast to all clients");

    await serverPeerStream.WriteAsync(new byte[] { 0x41, 0x42, 0x43 });
    await WaitUntil(() => serverCapture.BinaryValues.Count == 1, "server output_binary did not receive bytes");
    await WaitUntil(() => serverCapture.StringValues.Count == 1, "server output_string did not receive text");
    Assert(serverCapture.StringValues.TryPeek(out var observedText) && observedText == "ABC",
        "server output_string must decode UTF-8");
    Assert(serverCapture.BinaryValues.TryPeek(out var observedBytes) &&
        observedBytes.SequenceEqual(new byte[] { 0x41, 0x42, 0x43 }),
        "server output_binary must preserve bytes");
    Check("server received bytes -> output pins");

    server.ReceiveSample(server.InputBinaryPin, StringSampleWith("wrong-type"));
    await Task.Delay(50);
    Check("type mismatch is rejected");

    await server.Stop(serverContext);
    await server.Stop(serverContext);
    Check("server Stop is idempotent");

    var clientPort = GetFreePort();
    using var listener = new TcpListener(IPAddress.Loopback, clientPort);
    listener.Start();
    var client = new TcpClientNode();
    AssertPinLayout(client, "TCP Client");
    client.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = clientPort,
        ["ConnectTimeoutSeconds"] = 1,
        ["MaxConnectAttempts"] = 3,
        ["AutoReconnect"] = true,
        ["ReconnectDelaySeconds"] = 1,
        ["ReconnectMaxDelaySeconds"] = 3,
        ["SendQueueCapacity"] = 16,
        ["ReadTimeoutSeconds"] = 0,
        ["WriteTimeoutSeconds"] = 2,
        ["ReceiveBufferSize"] = 128,
    });

    var clientContext = new TestContext(loggerFactory.CreateLogger("client"));
    await client.Initialize(clientContext);
    await client.Run(clientContext);
    using var clientPeer = await listener.AcceptTcpClientAsync();
    var clientPeerStream = clientPeer.GetStream();
    await Task.Delay(150);

    client.ReceiveSample(client.InputStringPin, StringSampleWith("hello client"));
    await ReadAtLeastAsync(clientPeerStream, "hello client"u8.ToArray(), TimeSpan.FromSeconds(3));
    client.ReceiveSample(client.InputBinaryPin, BinSampleWith(new byte[] { 0x10, 0x20, 0x30 }));
    await ReadAtLeastAsync(clientPeerStream, new byte[] { 0x10, 0x20, 0x30 }, TimeSpan.FromSeconds(3));
    Check("client input_string/input_binary send");

    await clientPeerStream.WriteAsync(new byte[] { 0x7A, 0x7B });
    var clientCapture = new CaptureNode();
    await Task.Delay(100);
    Assert(clientCapture.BinaryValues.IsEmpty && clientCapture.StringValues.IsEmpty,
        "outputs must not receive data without a downstream connection");

    ConnectForTest(client.OutputBinaryPin, clientCapture.InputBinaryPin);
    ConnectForTest(client.OutputStringPin, clientCapture.InputStringPin);
    await clientPeerStream.WriteAsync(new byte[] { 0x78, 0x79 });
    await WaitUntil(() => clientCapture.BinaryValues.Count == 1, "client output_binary did not receive bytes");
    await WaitUntil(() => clientCapture.StringValues.Count == 1, "client output_string did not receive text");
    Assert(clientCapture.StringValues.TryPeek(out var clientText) && clientText == "xy",
        "client output_string must decode UTF-8");
    Check("client received bytes -> output pins");

    clientPeerStream.Dispose();
    clientPeer.Dispose();

    using var reconnectPeer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
    var reconnectStream = reconnectPeer.GetStream();
    client.ReceiveSample(client.InputStringPin, StringSampleWith("after reconnect"));
    await ReadAtLeastAsync(reconnectStream, "after reconnect"u8.ToArray(), TimeSpan.FromSeconds(3));
    Assert(client.Running, "client must remain running across reconnects");
    Check("client reconnects after the remote endpoint closes");

    var runtimePort = GetFreePort();
    using var runtimeListener = new TcpListener(IPAddress.Loopback, runtimePort);
    runtimeListener.Start();
    client.ReceiveSample(
        client.InputEndpointPin,
        StringSampleWith($"127.0.0.1:{runtimePort}"));
    using var runtimePeer = await runtimeListener
        .AcceptTcpClientAsync()
        .WaitAsync(TimeSpan.FromSeconds(5));
    var runtimeStream = runtimePeer.GetStream();
    client.ReceiveSample(client.InputStringPin, StringSampleWith("after endpoint change"));
    await ReadAtLeastAsync(runtimeStream, "after endpoint change"u8.ToArray(), TimeSpan.FromSeconds(3));
    Assert(client.Running, "client must remain running after a runtime endpoint change");
    Assert(
        client.NodeParameters.First(p => p.Name == "Port").Value is int persistedPort &&
        persistedPort == clientPort,
        "runtime endpoint changes must not overwrite the persisted Port parameter");
    Check("client dynamic endpoint reconnects without modifying workflow parameters");

    await client.Stop(clientContext);
    await client.Stop(clientContext);
    Check("client Stop is idempotent");

    var waitPort = GetFreePort();
    using var waitListener = new TcpListener(IPAddress.Loopback, waitPort);
    waitListener.Start();
    var waitClient = new TcpClientNode();
    waitClient.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = waitPort,
        ["ConnectMode"] = "WaitForEndpoint",
        ["ConnectTimeoutSeconds"] = 1,
        ["MaxConnectAttempts"] = 1,
        ["AutoReconnect"] = true,
        ["ReconnectDelaySeconds"] = 1,
        ["ReconnectMaxDelaySeconds"] = 3,
        ["SendQueueCapacity"] = 16,
        ["ReadTimeoutSeconds"] = 0,
        ["WriteTimeoutSeconds"] = 2,
        ["ReceiveBufferSize"] = 128,
    });

    var waitContext = new TestContext(loggerFactory.CreateLogger("wait-client"));
    await waitClient.Initialize(waitContext);
    await waitClient.Run(waitContext);
    await Task.Delay(200);
    Assert(!waitListener.Pending(), "WaitForEndpoint must not connect before endpoint input");
    Check("client WaitForEndpoint waits before connecting");

    waitClient.ReceiveSample(
        waitClient.InputEndpointPin,
        StringSampleWith($"127.0.0.1:{waitPort}"));
    using var waitPeer = await waitListener
        .AcceptTcpClientAsync()
        .WaitAsync(TimeSpan.FromSeconds(5));
    var waitPeerStream = waitPeer.GetStream();
    waitClient.ReceiveSample(waitClient.InputStringPin, StringSampleWith("after endpoint input"));
    await ReadAtLeastAsync(waitPeerStream, "after endpoint input"u8.ToArray(), TimeSpan.FromSeconds(3));
    Check("client WaitForEndpoint connects after endpoint input");

    await waitClient.Stop(waitContext);
    await waitClient.Stop(waitContext);
    Check("WaitForEndpoint client Stop is idempotent");

    var retryPort = GetFreePort();
    var retryClient = new TcpClientNode();
    retryClient.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "127.0.0.1",
        ["Port"] = retryPort,
        ["ConnectMode"] = "WaitForEndpoint",
        ["ConnectTimeoutSeconds"] = 1,
        ["MaxConnectAttempts"] = 1,
        ["AutoReconnect"] = true,
        ["ReconnectDelaySeconds"] = 1,
        ["ReconnectMaxDelaySeconds"] = 3,
        ["SendQueueCapacity"] = 16,
        ["ReadTimeoutSeconds"] = 0,
        ["WriteTimeoutSeconds"] = 2,
        ["ReceiveBufferSize"] = 128,
    });

    var retryContext = new TestContext(loggerFactory.CreateLogger("retry-client"));
    await retryClient.Initialize(retryContext);
    await retryClient.Run(retryContext);
    retryClient.ReceiveSample(
        retryClient.InputEndpointPin,
        StringSampleWith($"127.0.0.1:{retryPort}"));
    await WaitUntil(
        () => logs.Any(line =>
            line.StartsWith("[retry-client]", StringComparison.Ordinal) &&
            line.Contains("will reconnect", StringComparison.Ordinal)),
        "initial connection failure did not enter auto-reconnect");
    Assert(retryClient.Running, "AutoReconnect must keep the node running after an initial connection failure");

    using var retryListener = new TcpListener(IPAddress.Loopback, retryPort);
    retryListener.Start();
    using var retryPeer = await retryListener
        .AcceptTcpClientAsync()
        .WaitAsync(TimeSpan.FromSeconds(5));
    var retryPeerStream = retryPeer.GetStream();
    retryClient.ReceiveSample(retryClient.InputStringPin, StringSampleWith("after server startup"));
    await ReadAtLeastAsync(retryPeerStream, "after server startup"u8.ToArray(), TimeSpan.FromSeconds(3));
    Assert(retryClient.Running, "AutoReconnect must keep the node running after recovering");
    Check("client WaitForEndpoint retries initial connection after server starts later");

    await retryClient.Stop(retryContext);
    Check("retry client Stop succeeds");

    var invalid = new TcpServerNode();
    invalid.Configure(new Dictionary<string, object?>
    {
        ["Host"] = "not-a-local-ip",
        ["Port"] = GetFreePort(),
    });
    try
    {
        await invalid.Initialize(new TestContext(loggerFactory.CreateLogger("invalid")));
        throw new Exception("invalid host was accepted");
    }
    catch (InvalidOperationException)
    {
        Check("invalid host rejected");
    }
}

static StringSample StringSampleWith(string value)
{
    var sample = StringSample.CreateSample();
    sample.SetValue(value, "test");
    return sample;
}

static BinSample BinSampleWith(byte[] value)
{
    var sample = BinSample.CreateSample();
    sample.SetValue(value, "test");
    return sample;
}

static void AssertPinLayout(BaseNode node, string label)
{
    var expectedInputPins = label == "TCP Client" ? 3 : 2;
    Assert(node.InputPins.Count == expectedInputPins,
        $"{label} must expose exactly {expectedInputPins} input pins");
    Assert(node.InputPins.Any(p => p.Name == "input_string" && p.DataType == DataType.STRING),
        $"{label} must expose input_string");
    Assert(node.InputPins.Any(p => p.Name == "input_binary" && p.DataType == DataType.BIN),
        $"{label} must expose input_binary");
    if (label == "TCP Client")
    {
        Assert(node.InputPins.Any(p => p.Name == "input_string_endpoint" && p.DataType == DataType.STRING),
            $"{label} must expose input_string_endpoint");
    }
    Assert(node.OutputPins.Count == 2, $"{label} must expose exactly two output pins");
    Assert(node.OutputPins.Any(p => p.Name == "output_string" && p.DataType == DataType.STRING),
        $"{label} must expose output_string");
    Assert(node.OutputPins.Any(p => p.Name == "output_binary" && p.DataType == DataType.BIN),
        $"{label} must expose output_binary");
}

static void ConnectForTest(BasePin output, BasePin input)
{
    var method = typeof(BasePin).GetMethod("ConnectTo", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("BasePin.ConnectTo was not found");
    method.Invoke(output, new object[] { input });
}

static async Task ReadAtLeastAsync(NetworkStream stream, byte[] expected, TimeSpan timeout)
{
    var buffer = new byte[expected.Length];
    using var cts = new CancellationTokenSource(timeout);
    var offset = 0;
    try
    {
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cts.Token);
            if (read == 0)
                throw new IOException("Connection closed before expected bytes arrived");
            offset += read;
        }
    }
    catch (OperationCanceledException)
    {
        throw new TimeoutException("Expected bytes were not received in time");
    }

    Assert(buffer.SequenceEqual(expected), "Received bytes did not match the expected value");
}

static void Check(string name) => Console.WriteLine($"PASS {name}");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static async Task WaitUntil(Func<bool> condition, string message, int seconds = 4)
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

    public T? GetInput<T>(string pinName) => default;
    public bool HasInput(string pinName) => false;
    public bool TryGetInput<T>(string pinName, out T? value)
    {
        value = default;
        return false;
    }

    public Task<Sample?> WaitForInputAsync(string pinName, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Task.FromResult<Sample?>(null);

    public void SetOutput(string pinName, Sample? value) { }
    public T? GetService<T>() => default;
}

internal sealed class CaptureNode : BaseNode
{
    private readonly BasePin _stringInput = new("input_string", DataType.STRING, PinDirection.Input, required: false);
    private readonly BasePin _binaryInput = new("input_binary", DataType.BIN, PinDirection.Input, required: false);

    public CaptureNode()
    {
        Uuid = "TEST-CAPTURE";
        AddPins();
    }

    public override string Name => "Capture";
    public override string DisplayName { get; set; } = "Capture";
    public override string Category => "Test";

    public BasePin InputStringPin => _stringInput;
    public BasePin InputBinaryPin => _binaryInput;
    public ConcurrentQueue<string> StringValues { get; } = new();
    public ConcurrentQueue<byte[]> BinaryValues { get; } = new();

    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }

    public override void AddPins()
    {
        AddInputPin(_stringInput);
        AddInputPin(_binaryInput);
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;
    public override Task Run(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;
    public override Task Stop(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _stringInput) && value is StringSample text && text.StringValue is not null)
            StringValues.Enqueue(text.StringValue);
        else if (ReferenceEquals(pin, _binaryInput) && value is BinSample binary && binary.BinValue is not null)
            BinaryValues.Enqueue(binary.BinValue.ToArray());
    }
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
