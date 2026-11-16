// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalWorkerProcess.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>One supervised Worker process for one external Package.</summary>
public sealed class ExternalWorkerProcess : IAsyncDisposable
{
    private readonly ExternalNodeDefinition _definition;
    private readonly ExternalPackageManifest _manifest;
    private readonly ILogger _logger;
    private readonly ExternalNodeRuntime _runtime;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<int, ExternalNodeProxy> _nodes = new();
    private readonly Channel<JsonObject> _events = Channel.CreateBounded<JsonObject>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly CancellationTokenSource _pumpCts = new();
    private IExternalEndpoint? _endpoint;
    private IExternalDuplexConnection? _connection;
    private Process? _process;
    private Task? _pumpTask;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _heartbeatTask;
    private string _workerId = "";
    private string _authToken = "";
    private int _nextMessageId;
    private int _ready;
    private int _closed;
    private long _lastHeartbeatTimestamp;

    public ExternalWorkerProcess(
        ExternalNodeDefinition definition,
        ILogger logger,
        ExternalNodeRuntime runtime)
    {
        _definition = definition;
        _manifest = definition.Manifest;
        _logger = logger;
        _runtime = runtime;
    }

    public string PackageId => _manifest.PackageId;
    public string WorkerId => _workerId;
    public bool IsReady => Volatile.Read(ref _ready) != 0;
    public int NodeCount => _nodes.Count;

    /// <summary>Live Worker process id, or null when the process has already exited.</summary>
    public int? ProcessId
    {
        get
        {
            try { return _process is { HasExited: false } ? _process.Id : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public void RegisterNode(ExternalNodeProxy proxy)
    {
        _nodes[proxy.InstanceId] = proxy;
        _runtime.RegisterNode(proxy.InstanceId, this);
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (IsReady)
            return;

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
                return;
            if (Volatile.Read(ref _closed) != 0)
                throw new InvalidOperationException($"Worker '{PackageId}' is already closed.");

            var timeout = TimeSpan.FromMilliseconds(_manifest.Worker.StartTimeoutMs);
            _endpoint = ExternalEndpointFactory.Create();
            _authToken = Guid.NewGuid().ToString("N");
            StartProcess();
            _connection = await _endpoint.AcceptAsync(cancellationToken)
                .WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            _pumpTask = PumpMessagesAsync(_pumpCts.Token);

            using var startupCts = new CancellationTokenSource(timeout);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupCts.Token);

            var hello = await WaitForEventAsync("worker.hello", linked.Token).ConfigureAwait(false);
            ValidateProtocolVersion(hello, "worker.hello");
            var packageId = ExternalProtocol.GetString(hello, "packageId");
            if (!string.Equals(packageId, PackageId, StringComparison.OrdinalIgnoreCase))
                throw new ExternalWorkerException(
                    "WORKER_PACKAGE_MISMATCH",
                    $"Worker hello packageId '{packageId}' does not match '{PackageId}'.");
            var token = ExternalProtocol.GetString(hello, "authToken");
            if (token is not null && !string.Equals(token, _authToken, StringComparison.Ordinal))
                throw new ExternalWorkerException("WORKER_AUTH_FAILED", "Worker authentication token mismatch.");

            _workerId = $"{PackageId}-{Guid.NewGuid():N}";
            var welcome = ExternalProtocol.Create("worker.welcome", NextMessageId());
            welcome["correlationId"] = ExternalProtocol.GetString(hello, "messageId");
            welcome["workerId"] = _workerId;
            welcome["heartbeatIntervalMs"] = _manifest.Worker.HeartbeatIntervalMs;
            welcome["heartbeatTimeoutMs"] = _manifest.Worker.HeartbeatTimeoutMs;
            welcome["commandTimeoutMs"] = _manifest.Worker.CommandTimeoutMs;
            welcome["maxLineBytes"] = _manifest.Worker.MaxLineBytes;
            await SendAsync(welcome, linked.Token).ConfigureAwait(false);

            var register = await WaitForEventAsync("worker.register", linked.Token).ConfigureAwait(false);
            ValidateProtocolVersion(register, "worker.register");
            if (!string.Equals(ExternalProtocol.GetString(register, "packageId"), PackageId, StringComparison.OrdinalIgnoreCase))
                throw new ExternalWorkerException(
                    "WORKER_PACKAGE_MISMATCH", $"Worker register packageId does not match '{PackageId}'.");
            ValidateRegisteredCapabilities(register);
            ValidateWorkerRegistration(register);

            var registered = ExternalProtocol.Create("worker.registered", NextMessageId());
            registered["correlationId"] = ExternalProtocol.GetString(register, "messageId");
            registered["workerId"] = _workerId;
            await SendAsync(registered, linked.Token).ConfigureAwait(false);

            var ready = await WaitForEventAsync("worker.ready", linked.Token).ConfigureAwait(false);
            ValidateProtocolVersion(ready, "worker.ready");
            Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref _ready, 1);
            _heartbeatTask = MonitorHeartbeatAsync(_pumpCts.Token);
            _logger.LogInformation(
                "External Worker ready: {PackageId} pid={Pid} workerId={WorkerId}",
                PackageId, _process?.Id, _workerId);
        }
        catch
        {
            KillProcess();
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task CreateNodeAsync(
        ExternalNodeProxy proxy,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var create = ExternalProtocol.Create("node.create", NextMessageId());
        create["workerId"] = _workerId;
        create["nodeInstanceId"] = proxy.InstanceId;
        create["nodeType"] = proxy.Name;
        create["parameters"] = ExternalSampleCodec.ParametersToProtocol(parameters);
        var response = await RequestAsync(create, cancellationToken).ConfigureAwait(false);
        EnsureStatusOk(response, "node.create");
        RegisterNode(proxy);
        foreach (var topic in _definition.Subscriptions)
            await SubscribeAsync(proxy.InstanceId, topic, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfigureNodeAsync(
        int nodeInstanceId,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var configure = ExternalProtocol.Create("node.configure", NextMessageId());
        configure["workerId"] = _workerId;
        configure["nodeInstanceId"] = nodeInstanceId;
        configure["parameters"] = ExternalSampleCodec.ParametersToProtocol(parameters);
        configure["replaceAll"] = true;
        var response = await RequestAsync(configure, cancellationToken).ConfigureAwait(false);
        EnsureStatusOk(response, "node.configure");
    }

    public async Task CommandAsync(
        int nodeInstanceId,
        string command,
        CancellationToken cancellationToken,
        string? runId = null)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var message = ExternalProtocol.Create("node.command", NextMessageId());
        message["workerId"] = _workerId;
        message["nodeInstanceId"] = nodeInstanceId;
        message["command"] = command;
        if (runId is not null)
            message["runId"] = runId;
        var response = await RequestAsync(message, cancellationToken).ConfigureAwait(false);
        EnsureStatusOk(response, $"node.command({command})");
    }

    public async Task SendPinReceiveAsync(
        int nodeInstanceId,
        string targetPin,
        long sequence,
        string? runId,
        JsonObject? sample,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var message = ExternalProtocol.Create("pin.receive", NextMessageId());
        message["workerId"] = _workerId;
        message["nodeInstanceId"] = nodeInstanceId;
        message["targetPin"] = targetPin;
        message["sequence"] = sequence;
        if (runId is not null)
            message["runId"] = runId;
        message["sample"] = sample;
        await SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task SubscribeAsync(int nodeInstanceId, string topic, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var message = ExternalProtocol.Create("broadcast.subscribe", NextMessageId());
        message["workerId"] = _workerId;
        message["nodeInstanceId"] = nodeInstanceId;
        message["topic"] = topic;
        var response = await RequestAsync(message, cancellationToken).ConfigureAwait(false);
        _runtime.Subscribe(nodeInstanceId, topic);
    }

    public async Task UnsubscribeAsync(int nodeInstanceId, string topic, CancellationToken cancellationToken)
    {
        if (!IsReady)
            return;
        var message = ExternalProtocol.Create("broadcast.unsubscribe", NextMessageId());
        message["workerId"] = _workerId;
        message["nodeInstanceId"] = nodeInstanceId;
        message["topic"] = topic;
        var response = await RequestAsync(message, cancellationToken).ConfigureAwait(false);
        EnsureStatusOk(response, "broadcast.unsubscribe");
        _runtime.Unsubscribe(nodeInstanceId, topic);
    }

    public async Task DeliverBroadcastAsync(
        int targetNodeInstanceId,
        int sourceNodeInstanceId,
        string topic,
        string broadcastId,
        string payloadKind,
        JsonNode? payload,
        string? runId,
        CancellationToken cancellationToken)
    {
        if (!IsReady)
            return;
        var message = ExternalProtocol.Create("broadcast.receive", NextMessageId());
        message["workerId"] = _workerId;
        message["targetNodeInstanceId"] = targetNodeInstanceId;
        message["sourceNodeInstanceId"] = sourceNodeInstanceId;
        message["topic"] = topic;
        message["broadcastId"] = broadcastId;
        message["payloadKind"] = payloadKind;
        message["payload"] = payload;
        if (runId is not null)
            message["runId"] = runId;
        await SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveNodeAsync(int nodeInstanceId, CancellationToken cancellationToken)
    {
        _nodes.TryRemove(nodeInstanceId, out _);
        _runtime.UnregisterNode(nodeInstanceId);
        if (_nodes.IsEmpty)
            await _runtime.ShutdownWorkerIfUnusedAsync(this, cancellationToken).ConfigureAwait(false);
    }

    public void MarkNodeFailed(int nodeInstanceId, string message, bool fatal)
    {
        if (!_nodes.TryGetValue(nodeInstanceId, out var proxy))
            return;
        _logger.LogError("External node #{NodeId} error: {Message}", nodeInstanceId, message);
        if (fatal)
            proxy.SetFailed();
    }

    public async Task ShutdownProcessAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _pumpCts.Cancel();
        if (_heartbeatTask is not null)
        {
            try { await _heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        try
        {
            if (_connection is not null)
                await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch { }

        // Closing the transport gives a cooperative Worker time to exit normally. Only force
        // termination when it fails to do so within the bounded grace period.
        await WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (_process is { HasExited: false })
            KillProcess();
        await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (_endpoint is not null)
            await _endpoint.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Force-terminate the Worker process tree. Returns true when a kill was issued.</summary>
    public bool KillProcess()
    {
        try
        {
            if (_process is not { HasExited: false })
                return false;

            _process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill external Worker process for {PackageId}.", PackageId);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownProcessAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void StartProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _definition.WorkerExecutablePath,
            WorkingDirectory = _definition.WorkerWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var arguments = _manifest.Worker.Arguments.Count > 0
            ? _manifest.Worker.Arguments
            : new List<string> { "--endpoint", "${WORKER_ENDPOINT}", "--token", "${WORKER_TOKEN}" };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument
                .Replace("${WORKER_ENDPOINT}", _endpoint!.Address, StringComparison.Ordinal)
                .Replace("${WORKER_TOKEN}", _authToken, StringComparison.Ordinal)
                .Replace("${PACKAGE_ROOT}", _definition.PackageDirectory, StringComparison.Ordinal));
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start external Worker executable: {startInfo.FileName}");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            var id = _process?.Id;
            var exitCode = _process?.ExitCode;
            Volatile.Write(ref _ready, 0);
            if (Volatile.Read(ref _closed) != 0)
            {
                if (exitCode == 0)
                    _logger.LogInformation(
                        "External Worker exited during shutdown: {PackageId} pid={Pid} exitCode={ExitCode}",
                        PackageId, id, exitCode);
                else
                    _logger.LogWarning(
                        "External Worker exited during shutdown with non-zero code: {PackageId} pid={Pid} exitCode={ExitCode}",
                        PackageId, id, exitCode);
                return;
            }

            _logger.LogError("External Worker exited unexpectedly: {PackageId} pid={Pid} exitCode={ExitCode}",
                PackageId, id, exitCode);
            foreach (var proxy in _nodes.Values)
                proxy.SetFailed();
        };

        _stdoutTask = PumpProcessTextAsync(_process.StandardOutput, "stdout");
        _stderrTask = PumpProcessTextAsync(_process.StandardError, "stderr");
        _logger.LogInformation(
            "Started external Worker: {PackageId} executable={Executable} pid={Pid}",
            PackageId, _definition.WorkerExecutablePath, _process.Id);
    }

    private async Task PumpProcessTextAsync(StreamReader reader, string source)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                _logger.LogDebug("External Worker {PackageId} {Source}: {Line}", PackageId, source, line);
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private async Task PumpMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _connection is not null)
            {
                var line = await _connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var message = ExternalProtocol.Parse(line);
                if (message is null)
                {
                    _logger.LogError("External Worker {PackageId} sent malformed JSON: {Line}", PackageId, line);
                    continue;
                }

                await HandleMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "External Worker message loop failed: {PackageId}", PackageId);
            foreach (var proxy in _nodes.Values)
                proxy.SetFailed();
        }
    }

    private async Task HandleMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var type = ExternalProtocol.GetString(message, "type") ?? "";
        var correlationId = ExternalProtocol.GetString(message, "correlationId");
        _logger.LogTrace("External Worker {PackageId} received message type={Type} messageId={MessageId} correlationId={CorrelationId}",
            PackageId, type, ExternalProtocol.GetString(message, "messageId"), correlationId);

        if (correlationId is not null && _pending.TryRemove(correlationId, out var pending))
        {
            if (type == "error")
            {
                pending.TrySetException(new ExternalWorkerException(
                    ExternalProtocol.GetString(message, "code") ?? "WORKER_ERROR",
                    ExternalProtocol.GetString(message, "message") ?? "Worker request failed."));
            }
            else
            {
                pending.TrySetResult(message);
            }
            return;
        }

        switch (type)
        {
            case "worker.hello":
            case "worker.register":
            case "worker.ready":
            case "worker.registered":
                await _events.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                return;

            case "worker.heartbeat":
                ValidateProtocolVersion(message, "worker.heartbeat");
                ValidateWorkerId(message, "worker.heartbeat");
                Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                return;

            case "pin.send":
                HandlePinSend(message);
                return;

            case "node.state":
                HandleNodeState(message);
                return;

            case "node.error":
                HandleNodeError(message);
                return;

            case "broadcast.subscribe":
                await HandleBroadcastSubscribeAsync(message, cancellationToken).ConfigureAwait(false);
                return;

            case "broadcast.unsubscribe":
                await HandleBroadcastUnsubscribeAsync(message, cancellationToken).ConfigureAwait(false);
                return;

            case "broadcast.publish":
                await HandleBroadcastPublishAsync(message, cancellationToken).ConfigureAwait(false);
                return;

            case "log":
                LogWorkerMessage(message);
                return;

            case "error":
                _logger.LogError("External Worker protocol error {Code}: {Message}",
                    ExternalProtocol.GetString(message, "code"),
                    ExternalProtocol.GetString(message, "message"));
                return;

            default:
                _logger.LogWarning("External Worker {PackageId} sent unknown message type '{Type}'.",
                    PackageId, type);
                return;
        }
    }

    private void HandlePinSend(JsonObject message)
    {
        var nodeInstanceId = ExternalProtocol.GetInt32(message, "nodeInstanceId");
        var sourcePin = ExternalProtocol.GetString(message, "sourcePin");
        if (nodeInstanceId is null || sourcePin is null ||
            !_nodes.TryGetValue(nodeInstanceId.Value, out var proxy))
        {
            _ = SendProtocolErrorAsync(message, "NODE_UNKNOWN", "pin.send references an unknown node instance.");
            return;
        }

        try
        {
            proxy.HandleWorkerPinSend(sourcePin, message["sample"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "External node #{NodeId} pin.send failed for {Pin}.", nodeInstanceId, sourcePin);
            _ = SendProtocolErrorAsync(message, "PIN_INVALID", ex.Message);
        }
    }

    private void HandleNodeState(JsonObject message)
    {
        var nodeInstanceId = ExternalProtocol.GetInt32(message, "nodeInstanceId");
        var state = ExternalProtocol.GetString(message, "state");
        if (nodeInstanceId is null || !_nodes.TryGetValue(nodeInstanceId.Value, out var proxy))
            return;
        proxy.HandleWorkerState(state);
    }

    private void HandleNodeError(JsonObject message)
    {
        var nodeInstanceId = ExternalProtocol.GetInt32(message, "nodeInstanceId");
        if (nodeInstanceId is null)
            return;
        var fatal = ExternalProtocol.GetBool(message, "fatal") ?? false;
        MarkNodeFailed(
            nodeInstanceId.Value,
            ExternalProtocol.GetString(message, "message") ?? "External node error.",
            fatal);
    }

    private async Task HandleBroadcastSubscribeAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var nodeInstanceId = ExternalProtocol.GetInt32(message, "nodeInstanceId");
        var topic = ExternalProtocol.GetString(message, "topic");
        if (nodeInstanceId is null || string.IsNullOrWhiteSpace(topic))
            return;

        _runtime.Subscribe(nodeInstanceId.Value, topic);
        var response = ExternalProtocol.Create("broadcast.subscription", NextMessageId());
        response["correlationId"] = ExternalProtocol.GetString(message, "messageId");
        response["nodeInstanceId"] = nodeInstanceId.Value;
        response["topic"] = topic;
        response["status"] = "ok";
        await SendAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleBroadcastUnsubscribeAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var nodeInstanceId = ExternalProtocol.GetInt32(message, "nodeInstanceId");
        var topic = ExternalProtocol.GetString(message, "topic");
        if (nodeInstanceId is null || string.IsNullOrWhiteSpace(topic))
            return;

        _runtime.Unsubscribe(nodeInstanceId.Value, topic);
        var response = ExternalProtocol.Create("broadcast.subscription", NextMessageId());
        response["correlationId"] = ExternalProtocol.GetString(message, "messageId");
        response["nodeInstanceId"] = nodeInstanceId.Value;
        response["topic"] = topic;
        response["status"] = "ok";
        await SendAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleBroadcastPublishAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var sourceNodeInstanceId = ExternalProtocol.GetInt32(message, "nodeInstanceId");
        var topic = ExternalProtocol.GetString(message, "topic");
        if (sourceNodeInstanceId is null || string.IsNullOrWhiteSpace(topic))
            return;

        await _runtime.PublishBroadcastFromWorkerAsync(
            sourceNodeInstanceId.Value, topic, message, cancellationToken).ConfigureAwait(false);
    }

    private void LogWorkerMessage(JsonObject message)
    {
        var level = ExternalProtocol.GetString(message, "level") ?? "info";
        var text = ExternalProtocol.GetString(message, "message") ?? "";
        var nodeId = ExternalProtocol.GetInt32(message, "nodeInstanceId");
        switch (level.ToLowerInvariant())
        {
            case "trace": _logger.LogTrace("[Worker {PackageId} #{NodeId}] {Message}", PackageId, nodeId, text); break;
            case "debug": _logger.LogDebug("[Worker {PackageId} #{NodeId}] {Message}", PackageId, nodeId, text); break;
            case "warning": _logger.LogWarning("[Worker {PackageId} #{NodeId}] {Message}", PackageId, nodeId, text); break;
            case "error": _logger.LogError("[Worker {PackageId} #{NodeId}] {Message}", PackageId, nodeId, text); break;
            case "critical": _logger.LogCritical("[Worker {PackageId} #{NodeId}] {Message}", PackageId, nodeId, text); break;
            default: _logger.LogInformation("[Worker {PackageId} #{NodeId}] {Message}", PackageId, nodeId, text); break;
        }
    }

    private async Task<JsonObject> WaitForEventAsync(string expectedType, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await _events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var type = ExternalProtocol.GetString(message, "type");
            if (type == expectedType)
                return message;
            if (type == "error")
                throw new ExternalWorkerException(
                    ExternalProtocol.GetString(message, "code") ?? "WORKER_PROTOCOL_ERROR",
                    ExternalProtocol.GetString(message, "message") ?? "Worker startup failed.");
            _logger.LogTrace("Ignoring Worker message {Type} while waiting for {ExpectedType}", type, expectedType);
        }
    }

    private async Task<JsonObject> RequestAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var messageId = ExternalProtocol.GetString(request, "messageId")
            ?? throw new InvalidOperationException("Request messageId is missing.");
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[messageId] = completion;
        try
        {
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(TimeSpan.FromMilliseconds(_manifest.Worker.CommandTimeoutMs), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(messageId, out _);
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (_connection is null)
            throw new InvalidOperationException($"Worker '{PackageId}' is not connected.");
        await _connection.WriteLineAsync(ExternalProtocol.Serialize(message), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendProtocolErrorAsync(JsonObject source, string code, string message)
    {
        try
        {
            var error = ExternalProtocol.Create("error", NextMessageId());
            error["correlationId"] = ExternalProtocol.GetString(source, "messageId");
            error["workerId"] = _workerId;
            error["nodeInstanceId"] = ExternalProtocol.GetInt32(source, "nodeInstanceId");
            error["code"] = code;
            error["severity"] = "error";
            error["message"] = message;
            error["fatal"] = false;
            await SendAsync(error, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send protocol error to Worker {PackageId}.", PackageId);
        }
    }

    private void ValidateProtocolVersion(JsonObject message, string type)
    {
        var version = ExternalProtocol.GetInt32(message, "protocolVersion");
        if (version != ExternalProtocol.ProtocolVersion)
            throw new ExternalWorkerException(
                "PROTOCOL_VERSION_UNSUPPORTED",
                $"Unsupported protocolVersion {version} in {type}; expected {ExternalProtocol.ProtocolVersion}.");
    }

    private void ValidateRegisteredCapabilities(JsonObject message)
    {
        var capabilities = ExternalProtocol.GetArray(message, "capabilities")
            ?.Select(node => node?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var required = _manifest.Worker.Capabilities.Count > 0
            ? _manifest.Worker.Capabilities
            : new List<string> { "lifecycle" };
        foreach (var capability in required)
        {
            if (!capabilities.Contains(capability))
                throw new ExternalWorkerException(
                    "CAPABILITY_MISSING",
                    $"Worker must declare the '{capability}' capability.");
        }
    }
    private static void EnsureStatusOk(JsonObject response, string operation)
    {
        var status = ExternalProtocol.GetString(response, "status");
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            throw new ExternalWorkerException(
                ExternalProtocol.GetString(response, "code") ?? "WORKER_COMMAND_FAILED",
                ExternalProtocol.GetString(response, "message") ?? $"{operation} failed.");
    }

    private async Task MonitorHeartbeatAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(_manifest.Worker.HeartbeatTimeoutMs);
        var intervalMs = Math.Max(50, Math.Min(_manifest.Worker.HeartbeatIntervalMs, _manifest.Worker.HeartbeatTimeoutMs / 2));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
                var last = Interlocked.Read(ref _lastHeartbeatTimestamp);
                if (last != 0 && Stopwatch.GetElapsedTime(last) <= timeout)
                    continue;

                _logger.LogError(
                    "External Worker heartbeat timed out: {PackageId} workerId={WorkerId} timeoutMs={TimeoutMs}",
                    PackageId, _workerId, _manifest.Worker.HeartbeatTimeoutMs);
                Volatile.Write(ref _ready, 0);
                foreach (var proxy in _nodes.Values)
                    proxy.SetFailed();
                KillProcess();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void ValidateWorkerId(JsonObject message, string messageType)
    {
        var workerId = ExternalProtocol.GetString(message, "workerId");
        if (workerId is not null && !string.Equals(workerId, _workerId, StringComparison.Ordinal))
            throw new ExternalWorkerException(
                "WORKER_ID_MISMATCH",
                $"{messageType} workerId '{workerId}' does not match active worker '{_workerId}'.");
    }

    private void ValidateWorkerRegistration(JsonObject register)
    {
        var registeredNodes = ExternalProtocol.GetArray(register, "nodeTypes");
        if (registeredNodes is null)
            throw new ExternalWorkerException(
                "WORKER_REGISTRATION_INVALID",
                "worker.register must include a nodeTypes array.");

        var expectedNodes = _definition.Manifest.NodeTypes;
        if (registeredNodes.Count != expectedNodes.Count)
            throw new ExternalWorkerException(
                "WORKER_REGISTRATION_MISMATCH",
                $"Worker registered {registeredNodes.Count} node types; manifest declares {expectedNodes.Count}.");

        var actualByName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in registeredNodes)
        {
            if (node is not JsonObject nodeObject)
                throw new ExternalWorkerException(
                    "WORKER_REGISTRATION_INVALID",
                    "worker.register nodeTypes entries must be JSON objects.");
            var nodeType = ExternalProtocol.GetString(nodeObject, "nodeType");
            if (string.IsNullOrWhiteSpace(nodeType) || !actualByName.TryAdd(nodeType, nodeObject))
                throw new ExternalWorkerException(
                    "WORKER_REGISTRATION_INVALID",
                    $"worker.register contains an empty or duplicate nodeType '{nodeType}'.");
        }

        foreach (var expected in expectedNodes)
        {
            if (!actualByName.TryGetValue(expected.NodeType, out var actual))
                throw new ExternalWorkerException(
                    "WORKER_REGISTRATION_MISMATCH",
                    $"Worker did not register manifest nodeType '{expected.NodeType}'.");

            ValidateRegisteredPins(expected, actual);
            ValidateRegisteredParameters(expected, actual);
            ValidateRegisteredSubscriptions(expected, actual);
        }
    }

    private static void ValidateRegisteredPins(
        ExternalNodeTypeManifest expected,
        JsonObject actual)
    {
        var pins = ExternalProtocol.GetArray(actual, "pins")
            ?? throw new ExternalWorkerException(
                "WORKER_REGISTRATION_INVALID",
                $"Registered nodeType '{expected.NodeType}' is missing pins.");
        var expectedPins = expected.Pins
            .ToDictionary(pin => pin.Name, pin => pin, StringComparer.OrdinalIgnoreCase);
        if (pins.Count != expectedPins.Count)
            throw new ExternalWorkerException(
                "WORKER_REGISTRATION_MISMATCH",
                $"Registered nodeType '{expected.NodeType}' pin count does not match the manifest.");

        foreach (var pinNode in pins)
        {
            if (pinNode is not JsonObject pin)
                throw new ExternalWorkerException(
                    "WORKER_REGISTRATION_INVALID",
                    $"Registered nodeType '{expected.NodeType}' contains a non-object pin.");
            var name = ExternalProtocol.GetString(pin, "name");
            if (string.IsNullOrWhiteSpace(name) || !expectedPins.TryGetValue(name, out var expectedPin))
                throw new ExternalWorkerException(
                    "WORKER_REGISTRATION_MISMATCH",
                    $"Registered nodeType '{expected.NodeType}' declares unknown pin '{name}'.");

            var direction = ExternalProtocol.GetString(pin, "direction");
            var dataType = ExternalProtocol.GetString(pin, "dataType");
            if (!string.Equals(direction, expectedPin.Direction, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(dataType, expectedPin.DataType, StringComparison.OrdinalIgnoreCase))
                throw new ExternalWorkerException(
                    "WORKER_REGISTRATION_MISMATCH",
                    $"Registered pin '{expected.NodeType}.{name}' does not match manifest direction/dataType.");
        }
    }

    private static void ValidateRegisteredParameters(
        ExternalNodeTypeManifest expected,
        JsonObject actual)
    {
        var parameters = ExternalProtocol.GetArray(actual, "parameters")
            ?? throw new ExternalWorkerException(
                "WORKER_REGISTRATION_INVALID",
                $"Registered nodeType '{expected.NodeType}' is missing parameters.");
        var expectedParameters = expected.Parameters
            .ToDictionary(parameter => parameter.Name, parameter => parameter, StringComparer.OrdinalIgnoreCase);
        if (parameters.Count != expectedParameters.Count)
            throw new ExternalWorkerException(
                "WORKER_REGISTRATION_MISMATCH",
                $"Registered nodeType '{expected.NodeType}' parameter count does not match the manifest.");

        foreach (var parameterNode in parameters)
        {
            if (parameterNode is not JsonObject parameter)
                throw new ExternalWorkerException(
                    "WORKER_REGISTRATION_INVALID",
                    $"Registered nodeType '{expected.NodeType}' contains a non-object parameter.");
            var name = ExternalProtocol.GetString(parameter, "name");
            if (string.IsNullOrWhiteSpace(name) || !expectedParameters.TryGetValue(name, out var expectedParameter))
                throw new ExternalWorkerException(
                    "WORKER_REGISTRATION_MISMATCH",
                    $"Registered nodeType '{expected.NodeType}' declares unknown parameter '{name}'.");
            var dataType = ExternalProtocol.GetString(parameter, "type");
            if (!string.Equals(dataType, expectedParameter.Type, StringComparison.OrdinalIgnoreCase))
                throw new ExternalWorkerException(
                    "WORKER_REGISTRATION_MISMATCH",
                    $"Registered parameter '{expected.NodeType}.{name}' type does not match the manifest.");
        }
    }

    private static void ValidateRegisteredSubscriptions(
        ExternalNodeTypeManifest expected,
        JsonObject actual)
    {
        var subscriptions = ExternalProtocol.GetArray(actual, "subscriptions");
        if (subscriptions is null)
            throw new ExternalWorkerException(
                "WORKER_REGISTRATION_INVALID",
                $"Registered nodeType '{expected.NodeType}' is missing subscriptions.");
        var actualTopics = subscriptions
            .Select(node => node?.GetValue<string>())
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var expectedTopics = expected.Subscriptions.ToHashSet(StringComparer.Ordinal);
        if (!actualTopics.SetEquals(expectedTopics))
            throw new ExternalWorkerException(
                "WORKER_REGISTRATION_MISMATCH",
                $"Registered nodeType '{expected.NodeType}' subscriptions do not match the manifest.");
    }

    private string NextMessageId() => $"msg-{Interlocked.Increment(ref _nextMessageId)}";

    private async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited)
            return;
        try
        {
            await _process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}

public sealed class ExternalWorkerException : Exception
{
    public ExternalWorkerException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
