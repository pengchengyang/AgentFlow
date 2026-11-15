using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using AgentFlow.Core;
using AgentFlow.Core.ExternalNodes;
using BroadcastKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PinDirection = AgentFlow.Contracts.PinDirection;

try
{
    await RunTestsAsync();
    Console.WriteLine("ALL PASS");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static async Task RunTestsAsync()
{
    var registry = new NodeRegistry();
    Register<SuccessNode>(registry);
    Register<FailureNode>(registry);
    Register<SlowNode>(registry);
    Register<BroadcastPublishNode>(registry);
    Register<BroadcastSinkNode>(registry);
    Register<SelfStopNode>(registry);
    Register<ConcurrencyProbeNode>(registry);
    Register<ShutdownFailureNode>(registry);

    await SameWorkflowMultipleInstancesAsync(registry);
    await DifferentWorkflowsFailureIsolationAsync(registry);
    await FileBackedWorkflowAsync(registry);
    await StopAllCancelsEveryInstanceAsync(registry);
    await PersistentRunStaysActiveUntilStopAsync(registry);
    await BatchBroadcastIsolationAsync(registry);
    await AutoStopSinkStopsOnlyCurrentInstanceAsync(registry);
    await MaxConcurrencyIsHonoredAsync(registry);
    await TenInstanceStressAsync(registry);
    await SharedConcurrencyGateIsGlobalAsync(registry);
    await QueuedInstanceCanBeStoppedAsync(registry);
    await AutoFiniteWorkflowSucceedsAsync(registry);
    await CleanupFailureIsReportedAsync(registry);
    await ExternalWorkersMultipleInstancesAsync();
}


static async Task BatchBroadcastIsolationAsync(NodeRegistry registry)
{
    BroadcastSinkNode.Reset();
    var snapshot = CreateSnapshot(
        new BroadcastPublishNode { InstanceId = 1001 },
        new BroadcastSinkNode { InstanceId = 1002 });
    var requests = Enumerable.Range(1, 2)
        .Select(i => WorkflowRunRequest.FromJson($"broadcast-{i}", snapshot, i))
        .ToArray();

    var manager = NewManager(registry);
    var results = await manager.RunAsync(requests);

    Assert(results.All(r => r.State == WorkflowRunState.Succeeded),
        "broadcast isolation workflows must both succeed: " +
        string.Join(" | ", results.Select(r => $"{r.RunId}={r.State}:{r.Error}")));
    Assert(BroadcastSinkNode.ReceivedCount == 2,
        $"each isolated run must receive exactly its own broadcast; got {BroadcastSinkNode.ReceivedCount}");
    Check("batch runs -> broadcasts isolated per run instance");
}

static async Task AutoStopSinkStopsOnlyCurrentInstanceAsync(NodeRegistry registry)
{
    SelfStopNode.Reset();
    SlowNode.Reset();
    var stopSnapshot = CreateSnapshot(new SelfStopNode());
    var slowSnapshot = CreateSnapshot(new SlowNode());
    var manager = NewManager(registry);
    var runTask = manager.RunAsync(new[]
    {
        WorkflowRunRequest.FromJson("self-stop", stopSnapshot, 1),
        WorkflowRunRequest.FromJson("still-running", slowSnapshot, 1),
    });

    await WaitUntilAsync(() => SelfStopNode.RequestedCount == 1,
        "self-stop node did not request a workflow stop");
    await Task.Delay(250);

    var selfStop = manager.Sessions.Single(s => s.RunId == "self-stop");
    var other = manager.Sessions.Single(s => s.RunId == "still-running");
    Assert(selfStop.IsTerminal && selfStop.State == WorkflowRunState.Cancelled,
        $"self-stop instance must be cancelled; actual={selfStop.State}");
    Assert(!other.IsTerminal && other.State == WorkflowRunState.Running,
        $"the other instance must keep running; actual={other.State}");

    await manager.StopAllAsync();
    var results = await runTask;
    Assert(results.All(r => r.State == WorkflowRunState.Cancelled),
        "the remaining instance must still be stopped by the batch host");
    Check("AutoStop sink -> stops only the current run instance");
}

static async Task MaxConcurrencyIsHonoredAsync(NodeRegistry registry)
{
    ConcurrencyProbeNode.Reset();
    var snapshot = CreateSnapshot(new ConcurrencyProbeNode());
    var requests = Enumerable.Range(1, 4)
        .Select(i => WorkflowRunRequest.FromJson($"limit-{i}", snapshot, i))
        .ToArray();
    var manager = NewManager(registry);
    var results = await manager.RunAsync(
        requests,
        new WorkflowRunOptions(MaxConcurrency: 2, FailurePolicy: WorkflowRunFailurePolicy.ContinueOthers));

    Assert(results.All(r => r.State == WorkflowRunState.Succeeded),
        "max-concurrency probe workflows must all succeed");
    Assert(ConcurrencyProbeNode.MaxObserved <= 2,
        $"max concurrency must not exceed 2; actual={ConcurrencyProbeNode.MaxObserved}");
    Assert(ConcurrencyProbeNode.MaxObserved >= 1,
        $"max concurrency must actually run instances; actual={ConcurrencyProbeNode.MaxObserved}");
    Check("RunAsync options -> max concurrency honored");
}

static async Task TenInstanceStressAsync(NodeRegistry registry)
{
    ConcurrencyProbeNode.Reset();
    var snapshot = CreateSnapshot(new ConcurrencyProbeNode());
    var requests = Enumerable.Range(1, 10)
        .Select(i => WorkflowRunRequest.FromJson($"stress-{i:00}", snapshot, i,
            displayName: $"Stress #{i}"))
        .ToArray();

    var manager = NewManager(registry);
    var results = await manager.RunAsync(
        requests,
        new WorkflowRunOptions(MaxConcurrency: 5, FailurePolicy: WorkflowRunFailurePolicy.ContinueOthers));

    Assert(results.Count == 10, "ten-instance stress run must return ten results");
    Assert(results.All(r => r.State == WorkflowRunState.Succeeded && r.ExitCode == 0),
        "all ten stress instances must succeed: " +
        string.Join(" | ", results.Select(r => $"{r.RunId}={r.State}:{r.EffectiveError}")));
    Assert(results.Select(r => r.RunId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 10,
        "ten-instance stress run must allocate ten distinct RunIds");
    Assert(results.Select(r => r.Ordinal).Distinct().Count() == 10,
        "ten-instance stress run must preserve distinct ordinals");
    Assert(ConcurrencyProbeNode.MaxObserved <= 5,
        $"ten-instance stress run must honor MaxConcurrency=5; actual={ConcurrencyProbeNode.MaxObserved}");
    Assert(ConcurrencyProbeNode.MaxObserved >= 2,
        $"ten-instance stress run should exercise concurrent execution; actual={ConcurrencyProbeNode.MaxObserved}");
    Check("ten workflow instances -> isolated results and bounded concurrency");
}

static async Task SharedConcurrencyGateIsGlobalAsync(NodeRegistry registry)
{
    ConcurrencyProbeNode.Reset();
    var snapshot = CreateSnapshot(new ConcurrencyProbeNode());
    var gate = new WorkflowRunConcurrencyGate();
    var managerA = new WorkflowRunManager(
        registry,
        NullLoggerFactory.Instance,
        _ => NullLoggerFactory.Instance,
        concurrencyGate: gate);
    var managerB = new WorkflowRunManager(
        registry,
        NullLoggerFactory.Instance,
        _ => NullLoggerFactory.Instance,
        concurrencyGate: gate);
    var options = new WorkflowRunOptions(
        MaxConcurrency: 1,
        FailurePolicy: WorkflowRunFailurePolicy.ContinueOthers);

    var requestsA = Enumerable.Range(1, 2)
        .Select(i => WorkflowRunRequest.FromJson($"global-a-{i}", snapshot, i))
        .ToArray();
    var requestsB = Enumerable.Range(1, 2)
        .Select(i => WorkflowRunRequest.FromJson($"global-b-{i}", snapshot, i))
        .ToArray();

    var runA = managerA.RunAsync(requestsA, options);
    var runB = managerB.RunAsync(requestsB, options);
    var results = (await Task.WhenAll(runA, runB)).SelectMany(results => results).ToArray();

    Assert(results.All(result => result.State == WorkflowRunState.Succeeded),
        "shared-gate workflows must all succeed");
    Assert(ConcurrencyProbeNode.MaxObserved == 1,
        $"shared gate must enforce one global active instance; actual={ConcurrencyProbeNode.MaxObserved}");
    Check("multiple RunManagers -> global concurrency gate honored");
}

static async Task QueuedInstanceCanBeStoppedAsync(NodeRegistry registry)
{
    SlowNode.Reset();
    var snapshot = CreateSnapshot(new SlowNode());
    var manager = NewManager(registry);
    var requests = Enumerable.Range(1, 3)
        .Select(i => WorkflowRunRequest.FromJson($"queued-stop-{i}", snapshot, i))
        .ToArray();
    var runTask = manager.RunAsync(
        requests,
        new WorkflowRunOptions(MaxConcurrency: 1));

    await WaitUntilAsync(() => SlowNode.StartedCount >= 1,
        "the first queued-stop instance did not start");

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var stopped = await manager.StopSessionAsync("queued-stop-2");
    stopwatch.Stop();

    Assert(stopped is { State: WorkflowRunState.Cancelled },
        $"a queued instance Stop must return Cancelled; actual={stopped?.State}");
    Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
        $"a queued instance Stop must not wait for the concurrency gate; elapsed={stopwatch.Elapsed}");

    await manager.StopAllAsync();
    var results = await runTask;
    Assert(results.Count == 3, "queued Stop batch must still return every result");
    Check("queued instance -> Stop cancels without waiting for gate release");
}

static async Task AutoFiniteWorkflowSucceedsAsync(NodeRegistry registry)
{
    SuccessNode.Reset();
    var snapshot = CreateSnapshot(new SuccessNode());
    var manager = NewManager(registry);
    var results = await manager.RunAsync(
        new[] { WorkflowRunRequest.FromJson("auto-finite", snapshot, 1) },
        new WorkflowRunOptions(RunMode: WorkflowRunMode.Auto));

    Assert(results.All(result => result.State == WorkflowRunState.Succeeded),
        $"Auto must complete a finite workflow successfully; actual={results[0].State}:{results[0].EffectiveError}");
    Assert(results[0].ExitCode == 0,
        $"Auto finite workflow must exit with zero; actual={results[0].ExitCode}");
    var afterCompletion = await manager.StopAllAsync();
    Assert(afterCompletion.Count == 0,
        "StopAllAsync without an active batch must return an empty result set");
    Check("Auto mode -> finite workflow succeeds and stale StopAll result is cleared");
}

static async Task CleanupFailureIsReportedAsync(NodeRegistry registry)
{
    var snapshot = CreateSnapshot(new ShutdownFailureNode());
    var manager = NewManager(registry);
    var results = await manager.RunAsync(
        new[] { WorkflowRunRequest.FromJson("cleanup-failure", snapshot, 1) },
        new WorkflowRunOptions(RunMode: WorkflowRunMode.Auto));

    Assert(results.Count == 1, "cleanup failure workflow must return one result");
    Assert(results[0].State == WorkflowRunState.Failed,
        $"a successful run with cleanup failure must become Failed; actual={results[0].State}");
    Assert(results[0].CleanupError?.Contains("Shutdown", StringComparison.OrdinalIgnoreCase) == true,
        $"cleanup failure must be preserved in the result; actual={results[0].CleanupError}");
    Check("cleanup failure -> surfaced as Failed result with CleanupError");
}

static async Task ExternalWorkersMultipleInstancesAsync()
{
    var externalRoot = FindExternalPackageRoot();
    var packages = new[]
    {
        ("Node.PythonUpper", ":py"),
        ("Node.CUpper", ":c"),
        ("Node.CppUpper", ":cpp"),
    };

    foreach (var (nodeType, suffix) in packages)
    {
        RecordingSinkNode.Reset();
        var registry = new NodeRegistry();
        Register<StringSourceNode>(registry);
        Register<RecordingSinkNode>(registry);
        var external = ExternalNodeCatalog.RegisterFromDirectory(
            externalRoot, registry, NullLogger.Instance);
        Assert(external.RegisteredNodeTypes > 0,
            $"external node catalog did not register any packages from {externalRoot}");

        var snapshot = CreateExternalWorkflowSnapshot(nodeType, registry);
        var factories = new ConcurrentDictionary<string, CapturingLoggerFactory>(
            StringComparer.OrdinalIgnoreCase);
        var manager = new WorkflowRunManager(
            registry,
            NullLoggerFactory.Instance,
            request =>
            {
                var factory = new CapturingLoggerFactory(request.RunId);
                factories[request.RunId] = factory;
                return factory;
            });

        try
        {
            var requests = Enumerable.Range(1, 3)
                .Select(i => WorkflowRunRequest.FromJson(
                    $"{nodeType}-{i}", snapshot, i, displayName: nodeType))
                .ToArray();
            var results = await manager.RunAsync(requests);

            Assert(results.Count == 3,
                $"{nodeType}: three workflow instances must return three results");
            Assert(results.All(r => r.State == WorkflowRunState.Succeeded),
                $"{nodeType}: every external Worker instance must succeed. " +
                string.Join(" | ", results.Select(r => $"{r.RunId}={r.State}:{r.Error}")));
            Assert(RecordingSinkNode.Values.Count == 6,
                $"{nodeType}: every run must produce both external output pins");
            Assert(RecordingSinkNode.Values.Count(v => v == "ABC" + suffix) == 3,
                $"{nodeType}: the uppercase output must be produced by every instance. " +
                $"Received: {string.Join(", ", RecordingSinkNode.Values)}");
            Assert(RecordingSinkNode.Values.Count(v => v == "xyz" + suffix) == 3,
                $"{nodeType}: the lowercase output must be produced by every instance. " +
                $"Received: {string.Join(", ", RecordingSinkNode.Values)}");

            var workerIds = factories.Values
                .SelectMany(factory => factory.Messages)
                .Where(message => message.Contains("External Worker ready", StringComparison.Ordinal))
                .Select(ExtractWorkerId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
            Assert(workerIds.Length == 3,
                $"{nodeType}: expected one started external Worker per workflow instance");
            Assert(workerIds.Distinct(StringComparer.Ordinal).Count() == 3,
                $"{nodeType}: workflow instances must not share an external Worker process");

            Check($"{nodeType} -> three isolated external Worker processes");
        }
        finally
        {
            if (external.Runtime is not null)
                await external.Runtime.DisposeAsync();
        }
    }
}

static string FindExternalPackageRoot()
{
    var configured = Environment.GetEnvironmentVariable("AGENTFLOW_EXTERNAL_PLUGIN_ROOT");
    if (!string.IsNullOrWhiteSpace(configured))
        return Path.GetFullPath(configured);

    var config = AppContext.BaseDirectory.Contains(
        $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        var candidate = Path.Combine(current.FullName, "artifacts", "External", config,
            ExternalRuntimeIdentifier.Current);
        if (Directory.Exists(candidate))
            return Directory.GetParent(candidate)!.FullName;
        current = current.Parent;
    }

    throw new DirectoryNotFoundException(
        "External package output was not found. Build scripts/build_external_nodes.py first " +
        "or set AGENTFLOW_EXTERNAL_PLUGIN_ROOT to the directory containing win-x64/linux-*/.");
}

static string? ExtractWorkerId(string message)
{
    const string marker = "workerId=";
    var start = message.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0)
        return null;
    start += marker.Length;
    var end = message.IndexOf(' ', start);
    return end < 0 ? message[start..] : message[start..end];
}

static string CreateExternalWorkflowSnapshot(string nodeType, NodeRegistry registry)
{
    var sourceUpper = new StringSourceNode { InstanceId = 1001 };
    sourceUpper.NodeParameters.First(p => p.Name == "Value").Value = "AbC";
    var sourceLower = new StringSourceNode { InstanceId = 1002 };
    sourceLower.NodeParameters.First(p => p.Name == "Value").Value = "xYz";
    var external = registry.CreateInstance(nodeType);
    external.InstanceId = 2001;
    var sinkUpper = new RecordingSinkNode { InstanceId = 3001 };
    var sinkLower = new RecordingSinkNode { InstanceId = 3002 };

    static JsonObject Node(string id, BaseNode node, params (string Name, int Id)[] pins)
    {
        var logic = LogicSerializer.Serialize(node);
        logic["name"] = node.Name;
        var pinArray = new JsonArray();
        foreach (var pin in pins)
            pinArray.Add(new JsonObject { ["name"] = pin.Name, ["id"] = pin.Id });
        logic["pins"] = pinArray;
        return new JsonObject { ["id"] = id, ["logic"] = logic };
    }

    var doc = new JsonObject
    {
        ["version"] = "1.0",
        ["nodes"] = new JsonArray
        {
            Node("source-upper", sourceUpper, ("output", 1001)),
            Node("source-lower", sourceLower, ("output", 1002)),
            Node("external", external,
                ("input_string_toupper", 2001),
                ("input_string_tolower", 2002),
                ("output_string_toupper", 2003),
                ("output_string_tolower", 2004)),
            Node("sink-upper", sinkUpper, ("input", 3001)),
            Node("sink-lower", sinkLower, ("input", 3002)),
        },
        ["connections"] = new JsonArray
        {
            new JsonObject { ["fromPinId"] = 1001, ["toPinId"] = 2001 },
            new JsonObject { ["fromPinId"] = 1002, ["toPinId"] = 2002 },
            new JsonObject { ["fromPinId"] = 2003, ["toPinId"] = 3001 },
            new JsonObject { ["fromPinId"] = 2004, ["toPinId"] = 3002 },
        },
    };
    return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}

static async Task SameWorkflowMultipleInstancesAsync(NodeRegistry registry)
{
    SuccessNode.Reset();
    var snapshot = CreateSnapshot(new SuccessNode());

    var requests = new[]
    {
        WorkflowRunRequest.FromJson("same-1", snapshot, 1),
        WorkflowRunRequest.FromJson("same-2", snapshot, 2),
        WorkflowRunRequest.FromJson("same-3", snapshot, 3),
    };

    var manager = NewManager(registry);
    var results = await manager.RunAsync(requests);

    Assert(results.Count == 3, "same workflow must produce three results");
    Assert(results.All(r => r.State == WorkflowRunState.Succeeded && r.ExitCode == 0),
        "all instances of the same workflow must succeed");
    Assert(results.Select(r => r.RunId).Distinct(StringComparer.Ordinal).Count() == 3,
        "each instance must have a distinct RunId");
    Assert(SuccessNode.ShutdownCount == 3,
        "each instance must execute its own Shutdown callback");
    Check("same workflow -> three independent instances");
}

static async Task DifferentWorkflowsFailureIsolationAsync(NodeRegistry registry)
{
    SuccessNode.Reset();
    FailureNode.Reset();
    var successSnapshot = CreateSnapshot(new SuccessNode());
    var failureSnapshot = CreateSnapshot(new FailureNode());

    var requests = new[]
    {
        WorkflowRunRequest.FromJson("different-success", successSnapshot, 1),
        WorkflowRunRequest.FromJson("different-fail", failureSnapshot, 1),
    };

    var manager = NewManager(registry);
    var results = await manager.RunAsync(requests);

    Assert(results.Count == 2, "two workflow configurations must produce two results");
    Assert(results.Single(r => r.RunId == "different-success").State == WorkflowRunState.Succeeded,
        "the successful workflow must not be affected by another workflow failure");
    Assert(results.Single(r => r.RunId == "different-fail").State == WorkflowRunState.Failed,
        "the failing workflow must be reported as failed");
    Assert(SuccessNode.ShutdownCount == 1, "successful workflow must complete Shutdown");
    Assert(FailureNode.ShutdownCount == 1, "failed workflow must still complete Shutdown");
    Check("different workflows -> failure isolated from the other instance");
}

static async Task FileBackedWorkflowAsync(NodeRegistry registry)
{
    SuccessNode.Reset();
    var snapshot = CreateSnapshot(new SuccessNode());
    var path = Path.Combine(Path.GetTempPath(), $"agentflow-run-{Guid.NewGuid():N}.json");
    await File.WriteAllTextAsync(path, snapshot);

    try
    {
        var manager = NewManager(registry);
        var results = await manager.RunAsync(new[]
        {
            WorkflowRunRequest.FromFile("file-1", path, 1),
            WorkflowRunRequest.FromFile("file-2", path, 2),
        });

        Assert(results.Count == 2, "file-backed workflow must produce two results");
        Assert(results.All(r => r.State == WorkflowRunState.Succeeded),
            "file-backed workflow instances must succeed");
        Assert(SuccessNode.ShutdownCount == 2,
            "file-backed workflow instances must each complete Shutdown");
        Check("file-backed workflow -> multiple independent instances");
    }
    finally
    {
        File.Delete(path);
    }
}

static async Task StopAllCancelsEveryInstanceAsync(NodeRegistry registry)
{
    SlowNode.Reset();
    var snapshot = CreateSnapshot(new SlowNode());

    var requests = Enumerable.Range(1, 3)
        .Select(i => WorkflowRunRequest.FromJson($"stop-{i}", snapshot, i))
        .ToArray();

    var manager = NewManager(registry);
    var runTask = manager.RunAsync(requests);

    await WaitUntilAsync(() => SlowNode.StartedCount >= 3,
        "all slow instances did not reach Running");
    var stopResults = await manager.StopAllAsync();
    var runResults = await runTask;

    Assert(stopResults.Count == 3, "StopAllAsync must return every instance result");
    Assert(runResults.Count == 3, "RunAsync must return every instance result after cancellation");
    Assert(runResults.All(r => r.State == WorkflowRunState.Cancelled && r.ExitCode == 130),
        "StopAllAsync must cancel every running instance");
    Assert(SlowNode.ShutdownCount == 3,
        "every cancelled instance must still complete Shutdown");
    Check("StopAll -> cancellation and Shutdown for every instance");
}

static async Task PersistentRunStaysActiveUntilStopAsync(NodeRegistry registry)
{
    // Reproduces the TCP Client pattern: Run() launches background work, reports Running = true,
    // and returns immediately. The persistent host must not auto-stop just because Run returned.
    SuccessNode.Reset();
    var snapshot = CreateSnapshot(new SuccessNode());
    var manager = NewManager(registry);
    var runTask = manager.RunAsync(
        new[] { WorkflowRunRequest.FromJson("persistent", snapshot, 1) },
        new WorkflowRunOptions(RunMode: WorkflowRunMode.Persistent));

    await WaitUntilAsync(
        () => manager.Sessions.SingleOrDefault()?.State == WorkflowRunState.Running,
        "persistent workflow did not reach Running");

    await Task.Delay(600);
    Assert(!runTask.IsCompleted,
        "persistent workflow returned before the host requested Stop");
    Assert(!manager.Sessions.Single().IsTerminal,
        "persistent workflow session must remain active after the node Run() method returned");

    var stopResults = await manager.StopAllAsync();
    var runResults = await runTask;
    Assert(stopResults.Count == 1 && runResults.Count == 1,
        "persistent StopAll must return the instance result");
    Assert(runResults[0].State == WorkflowRunState.Cancelled,
        $"persistent workflow must end as Cancelled after Stop; actual={runResults[0].State}");
    Assert(SuccessNode.ShutdownCount == 1,
        $"persistent workflow must complete Shutdown after Stop; actual={SuccessNode.ShutdownCount}");
    Check("persistent workflow -> remains Running until Stop");
}

static WorkflowRunManager NewManager(NodeRegistry registry)
    => new(registry, NullLoggerFactory.Instance, _ => NullLoggerFactory.Instance);

static void Register<T>(NodeRegistry registry) where T : BaseNode, new()
{
    var probe = new T();
    registry.Register(new NodeDescriptor(
        probe.Name,
        probe.DisplayName,
        probe.Category,
        typeof(T),
        probe.InputPins,
        probe.OutputPins,
        probe.Parameters));
}

static string CreateSnapshot(params BaseNode[] nodes)
{
    var nodeArray = new JsonArray();
    for (var i = 0; i < nodes.Length; i++)
    {
        var node = nodes[i];
        node.RefreshBuiltInParameters();
        var logic = LogicSerializer.Serialize(node);
        logic["name"] = node.Name;
        logic["displayName"] = node.DisplayName;
        nodeArray.Add(new JsonObject
        {
            ["id"] = $"n{i + 1}",
            ["x"] = 0,
            ["y"] = 0,
            ["logic"] = logic
        });
    }

    return new JsonObject
    {
        ["version"] = "1.0",
        ["nodes"] = nodeArray,
        ["connections"] = new JsonArray()
    }.ToJsonString();
}

static async Task WaitUntilAsync(Func<bool> predicate, string message, int timeoutMs = 5000)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (!predicate())
    {
        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException(message);
        await Task.Delay(20);
    }
}

static void Check(string message) => Console.WriteLine($"PASS {message}");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

public sealed class SuccessNode : BaseNode
{
    private static int _shutdownCount;

    public static int ShutdownCount => Volatile.Read(ref _shutdownCount);
    public static void Reset() => Interlocked.Exchange(ref _shutdownCount, 0);

    public SuccessNode()
    {
        AddPins();
    }

    public override string Name => "Test.Success";
    public override string DisplayName { get; set; } = "Test Success";
    public override string Category => "Tests";

    public override void AddPins() { }
    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }
    public override Task Initialize(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _shutdownCount);
        return Task.CompletedTask;
    }
}

public sealed class FailureNode : BaseNode
{
    private static int _shutdownCount;

    public static int ShutdownCount => Volatile.Read(ref _shutdownCount);
    public static void Reset() => Interlocked.Exchange(ref _shutdownCount, 0);

    public FailureNode()
    {
        AddPins();
    }

    public override string Name => "Test.Failure";
    public override string DisplayName { get; set; } = "Test Failure";
    public override string Category => "Tests";

    public override void AddPins() { }
    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }
    public override Task Initialize(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;

    public override Task Run(INodeContext context, CancellationToken ct = default)
        => throw new InvalidOperationException("Intentional test failure.");

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _shutdownCount);
        return Task.CompletedTask;
    }
}

public sealed class SlowNode : BaseNode
{
    private static int _startedCount;
    private static int _shutdownCount;

    public static int StartedCount => Volatile.Read(ref _startedCount);
    public static int ShutdownCount => Volatile.Read(ref _shutdownCount);

    public static void Reset()
    {
        Interlocked.Exchange(ref _startedCount, 0);
        Interlocked.Exchange(ref _shutdownCount, 0);
    }

    public SlowNode()
    {
        AddPins();
    }

    public override string Name => "Test.Slow";
    public override string DisplayName { get; set; } = "Test Slow";
    public override string Category => "Tests";

    public override void AddPins() { }
    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }
    public override Task Initialize(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;

    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        Interlocked.Increment(ref _startedCount);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _shutdownCount);
        return Task.CompletedTask;
    }
}

public sealed class StringSourceNode : BaseNode
{
    private readonly BasePin _output = new("output", DataType.STRING, PinDirection.Output);
    private string _value = "";

    public StringSourceNode()
    {
        AddParam();
        AddPins();
    }

    public override string Name => "Test.StringSource";
    public override string DisplayName { get; set; } = "String Source";
    public override string Category => "Tests";

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter("Value", typeof(string), "", isEditable: true));
    }

    public override void AddPins() => AddOutputPin(_output);
    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Value", out var value))
            _value = ParameterValues.GetString(value, "") ?? "";
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        var sample = StringSample.CreateSample();
        sample.SetValue(_value, _output.Name);
        _output.SendSample(sample);
        Running = true;
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class RecordingSinkNode : BaseNode
{
    private static readonly ConcurrentQueue<string> Received = new();
    private readonly BasePin _input = new("input", DataType.STRING, PinDirection.Input);
    private readonly TaskCompletionSource _received =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static IReadOnlyCollection<string> Values => Received.ToArray();
    public static void Reset() => Received.Clear();

    public RecordingSinkNode()
    {
        AddParam();
        AddPins();
    }

    public override string Name => "Test.RecordingSink";
    public override string DisplayName { get; set; } = "Recording Sink";
    public override string Category => "Tests";

    protected override void AddParam() => base.AddParam();

    public override void AddPins() => AddInputPin(_input);
    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }
    public override Task Initialize(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        await _received.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _input) && value?.StringValue is { } text)
        {
            Received.Enqueue(text);
            _received.TrySetResult();
        }
    }
}

internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<string> _messages = new();

    public CapturingLoggerFactory(string runId) => RunId = runId;
    public string RunId { get; }
    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages, categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            messages.Enqueue($"{category}: {message}");
        }
    }
}

public sealed class BroadcastPublishNode : BaseNode
{
    private const string TestTopic = "AgentFlow.Tests.BroadcastIsolation";

    public BroadcastPublishNode()
    {
        AddParam();
        AddPins();
    }

    public override string Name => "Test.BroadcastPublish";
    public override string DisplayName { get; set; } = "Broadcast Publish";
    public override string Category => "Tests";
    public override void AddPins() { }
    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }
    public override Task Initialize(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;

    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        var bus = context.GetService<ITopicBus>()
            ?? throw new InvalidOperationException("The workflow run did not inject an ITopicBus.");
        Running = true;
        await bus.PublishAsync(TestTopic, "isolated-payload", ct: ct);
        await Task.Delay(50, ct);
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class BroadcastSinkNode : BaseNode
{
    private const string TestTopic = "AgentFlow.Tests.BroadcastIsolation";
    private static int _receivedCount;
    private ITopicSubscription? _subscription;

    public static int ReceivedCount => Volatile.Read(ref _receivedCount);
    public static void Reset() => Interlocked.Exchange(ref _receivedCount, 0);

    public BroadcastSinkNode()
    {
        AddParam();
        AddPins();
    }

    public override string Name => "Test.BroadcastSink";
    public override string DisplayName { get; set; } = "Broadcast Sink";
    public override string Category => "Tests";
    public override void AddPins() { }
    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        var bus = context.GetService<ITopicBus>()
            ?? throw new InvalidOperationException("The workflow run did not inject an ITopicBus.");
        _subscription = bus.Subscribe(TestTopic, _ =>
        {
            Interlocked.Increment(ref _receivedCount);
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        await Task.Delay(250, ct);
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }
}

public sealed class SelfStopNode : BaseNode
{
    private static int _requestedCount;
    public static int RequestedCount => Volatile.Read(ref _requestedCount);
    public static void Reset() => Interlocked.Exchange(ref _requestedCount, 0);

    public SelfStopNode()
    {
        AddParam();
        AddPins();
    }
    public override string Name => "Test.SelfStop";
    public override string DisplayName { get; set; } = "Self Stop";
    public override string Category => "Tests";
    public override void AddPins() { }
    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }
    public override Task Initialize(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;

    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        var sink = context.GetService<IWorkflowStopRequestSink>()
            ?? throw new InvalidOperationException("The workflow run did not inject a stop sink.");
        if (!sink.RequestStop(Name, "self-stop"))
            throw new InvalidOperationException("Session stop sink did not accept the request.");
        Interlocked.Increment(ref _requestedCount);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class ConcurrencyProbeNode : BaseNode
{
    private static int _active;
    private static int _maxObserved;
    public static int MaxObserved => Volatile.Read(ref _maxObserved);
    public static void Reset()
    {
        Interlocked.Exchange(ref _active, 0);
        Interlocked.Exchange(ref _maxObserved, 0);
    }

    public ConcurrencyProbeNode()
    {
        AddParam();
        AddPins();
    }
    public override string Name => "Test.ConcurrencyProbe";
    public override string DisplayName { get; set; } = "Concurrency Probe";
    public override string Category => "Tests";
    public override void AddPins() { }
    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }
    public override Task Initialize(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;

    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        var active = Interlocked.Increment(ref _active);
        while (true)
        {
            var observed = Volatile.Read(ref _maxObserved);
            if (active <= observed || Interlocked.CompareExchange(ref _maxObserved, active, observed) == observed)
                break;
        }

        try { await Task.Delay(150, ct); }
        finally { Interlocked.Decrement(ref _active); }
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class ShutdownFailureNode : BaseNode
{
    public ShutdownFailureNode()
    {
        AddPins();
    }

    public override string Name => "Test.ShutdownFailure";
    public override string DisplayName { get; set; } = "Shutdown Failure";
    public override string Category => "Tests";
    public override void AddPins() { }
    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }
    public override Task Initialize(INodeContext context, CancellationToken ct = default) => Task.CompletedTask;

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => throw new InvalidOperationException("Intentional shutdown failure.");
}
