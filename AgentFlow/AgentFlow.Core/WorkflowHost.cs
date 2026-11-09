// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowHost.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Read-only workflow access shared by AgentFlow and product-specific GUIs.
/// Product GUIs use this interface to load a workflow, run it, and inspect node health
/// without depending on Avalonia or any graph rendering types.
/// </summary>
public interface IWorkflowReadOnlyHost
{
    /// <summary>Registered node types available to the host.</summary>
    IReadOnlyList<WorkflowNodeTypeInfo> GetNodeTypes();

    /// <summary>Current immutable workflow snapshot, including nodes and connections.</summary>
    WorkflowSnapshot GetWorkflowSnapshot();

    /// <summary>Current runtime health for every managed node instance.</summary>
    WorkflowRuntimeSnapshot GetRuntimeSnapshot();

    /// <summary>Current runtime health for one node instance, or null when it is absent.</summary>
    WorkflowNodeRuntimeSnapshot? GetNodeRuntimeSnapshot(int instanceId);

    /// <summary>Commands exposed by a node for its context menu.</summary>
    IReadOnlyList<NodeCommandInfo> GetNodeCommands(int instanceId);

    /// <summary>Load a workflow document in the layered AgentFlow JSON format.</summary>
    void LoadWorkflowJson(string json);

    /// <summary>Load a workflow document from a file.</summary>
    void LoadWorkflowFile(string path);

    /// <summary>Validate the current workflow before it is started.</summary>
    IReadOnlyList<string> Validate();

    /// <summary>Start the current workflow and keep it running until stopped or failed.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop the current workflow.</summary>
    Task StopAsync(CancellationToken cancellationToken = default, bool preserveFailure = false);

    /// <summary>Invoke every node's final application-exit callback.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>Execute one node command through the Core runtime.</summary>
    Task ExecuteNodeCommandAsync(
        int instanceId,
        string commandId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Editor-only operations used by AgentFlow. Product-specific GUIs should not use this
/// interface because they must not mutate workflow structure or saved documents.
/// </summary>
public interface IWorkflowEditorHost : IWorkflowReadOnlyHost
{
    /// <summary>Create a node instance and add it to the editor graph.</summary>
    EditorNode CreateNode(
        string typeName,
        double x,
        double y,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string? id = null,
        string? displayName = null,
        int priority = 0);

    /// <summary>Remove a node and all of its connections from the current graph.</summary>
    bool RemoveNode(string editorNodeId);

    /// <summary>Update editor-only node state and logical parameters.</summary>
    void UpdateNode(
        string editorNodeId,
        string? displayName,
        int priority,
        double x,
        double y,
        IReadOnlyDictionary<string, object?>? parameters = null);

    /// <summary>Connect two nodes by their stable editor node ids and pin names.</summary>
    bool Connect(string fromNodeId, string fromPinName, string toNodeId, string toPinName);

    /// <summary>Disconnect the wire ending at an input pin.</summary>
    bool DisconnectInput(string nodeId, string inputPinName);

    /// <summary>Remove every node and connection from the current graph.</summary>
    void Clear();
}

/// <summary>Immutable description of a registered node type.</summary>
public sealed record WorkflowNodeTypeInfo(
    string Name,
    string DisplayName,
    string Category,
    IReadOnlyList<WorkflowPinInfo> InputPins,
    IReadOnlyList<WorkflowPinInfo> OutputPins,
    IReadOnlyList<WorkflowParameterInfo> Parameters);

/// <summary>Immutable pin metadata used by hosts and product GUIs.</summary>
public sealed record WorkflowPinInfo(
    string Name,
    string DataType,
    bool IsInput,
    bool Required);

/// <summary>Immutable parameter metadata used by hosts and editor property panels.</summary>
public sealed record WorkflowParameterInfo(
    string Name,
    string DataType,
    object? Value,
    bool IsEditable,
    string? Zone,
    string? Group,
    ParameterEditorKind Editor,
    IReadOnlyList<string> Options);

/// <summary>Immutable node structure snapshot.</summary>
public sealed record WorkflowNodeSnapshot(
    string Id,
    int InstanceId,
    string NodeName,
    string DisplayName,
    string Category,
    double X,
    double Y,
    int Priority,
    IReadOnlyList<WorkflowPinInfo> InputPins,
    IReadOnlyList<WorkflowPinInfo> OutputPins,
    IReadOnlyList<WorkflowParameterInfo> Parameters,
    WorkflowNodeRuntimeSnapshot Runtime);

/// <summary>Immutable connection snapshot.</summary>
public sealed record WorkflowConnectionSnapshot(
    string FromNodeId,
    string FromPinName,
    string ToNodeId,
    string ToPinName,
    int FromPinId,
    int ToPinId);

/// <summary>Immutable workflow structure snapshot.</summary>
public sealed record WorkflowSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<WorkflowNodeSnapshot> Nodes,
    IReadOnlyList<WorkflowConnectionSnapshot> Connections);

/// <summary>Health state for one node instance.</summary>
public sealed record WorkflowNodeRuntimeSnapshot(
    string? EditorNodeId,
    int InstanceId,
    string NodeName,
    string DisplayName,
    string Category,
    NodeState State,
    bool Running,
    bool Connecting,
    bool Failed);

/// <summary>Aggregated workflow runtime health.</summary>
public sealed record WorkflowRuntimeSnapshot(
    DateTimeOffset CapturedAt,
    bool IsActive,
    bool IsRunning,
    bool IsStarting,
    bool IsFailed,
    IReadOnlyList<WorkflowNodeRuntimeSnapshot> Nodes);

/// <summary>
/// Unified host facade for AgentFlow.Core. It owns the public lifecycle, read, health,
/// and editor operations while hiding PluginLoader, EditorGraph, and NodeInstanceManager
/// from concrete GUIs.
/// </summary>
public sealed class WorkflowHost : IWorkflowEditorHost
{
    private readonly NodeRegistry _registry;
    private readonly PluginLoader _pluginLoader;
    private readonly EditorGraph _graph;
    private readonly NodeInstanceManager _nodeManager;
    private readonly ILogger<WorkflowHost> _logger;

    public WorkflowHost(
        NodeRegistry registry,
        PluginLoader pluginLoader,
        EditorGraph graph,
        NodeInstanceManager nodeManager,
        ILoggerFactory loggerFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _pluginLoader = pluginLoader ?? throw new ArgumentNullException(nameof(pluginLoader));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _nodeManager = nodeManager ?? throw new ArgumentNullException(nameof(nodeManager));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))
            ).CreateLogger<WorkflowHost>();
    }

    /// <summary>Create a complete host with its own Core components.</summary>
    public static WorkflowHost Create(ILoggerFactory loggerFactory, string? pluginDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var registry = new NodeRegistry();
        var pluginLoader = new PluginLoader(loggerFactory.CreateLogger(nameof(PluginLoader)));
        var graph = new EditorGraph(registry, loggerFactory, pluginLoader);
        var manager = new NodeInstanceManager(pluginLoader, loggerFactory.CreateLogger(nameof(NodeInstanceManager)));
        var host = new WorkflowHost(registry, pluginLoader, graph, manager, loggerFactory);
        if (!string.IsNullOrWhiteSpace(pluginDirectory))
            host.LoadPlugins(pluginDirectory);
        return host;
    }

    /// <summary>Host services resolved by node contexts during command execution and runs.</summary>
    public NodeServiceRegistry? Services
    {
        get => _graph.Services;
        set => _graph.Services = value;
    }

    /// <summary>Load plugin assemblies from a directory and replace the registered node types.</summary>
    public void LoadPlugins(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        _registry.Clear();
        _pluginLoader.LoadFromDirectory(pluginDirectory, _registry);
    }

    public IReadOnlyList<WorkflowNodeTypeInfo> GetNodeTypes() =>
        _registry.Nodes
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToNodeTypeInfo)
            .ToList();

    public WorkflowSnapshot GetWorkflowSnapshot()
    {
        var nodes = _graph.Nodes.Select(ToNodeSnapshot).ToList();
        var connections = _graph.Connections
            .Select(c => new WorkflowConnectionSnapshot(
                c.FromNode.Id,
                c.FromPin,
                c.ToNode.Id,
                c.ToPin,
                c.SourcePin.Id,
                c.TargetPin.Id))
            .ToList();
        return new WorkflowSnapshot(DateTimeOffset.UtcNow, nodes, connections);
    }

    public WorkflowRuntimeSnapshot GetRuntimeSnapshot()
    {
        var nodes = _pluginLoader.Instances
            .Select(ToRuntimeSnapshot)
            .OrderBy(n => n.InstanceId)
            .ToList();
        var isFailed = nodes.Any(n => n.Failed);
        var isRunning = nodes.Count > 0 && nodes.All(n => n.State == NodeState.Running && n.Running);
        var isStarting = !isRunning && !isFailed && nodes.Any(n => n.Connecting);
        var isActive = nodes.Any(n => n.State != NodeState.Idle);
        return new WorkflowRuntimeSnapshot(
            DateTimeOffset.UtcNow, isActive, isRunning, isStarting, isFailed, nodes);
    }

    public WorkflowNodeRuntimeSnapshot? GetNodeRuntimeSnapshot(int instanceId)
    {
        var node = _pluginLoader.Instances.FirstOrDefault(n => n.InstanceId == instanceId);
        return node is null ? null : ToRuntimeSnapshot(node);
    }

    public IReadOnlyList<NodeCommandInfo> GetNodeCommands(int instanceId) =>
        _nodeManager.GetNodeCommands(instanceId);

    public IReadOnlyList<string> Validate() =>
        WorkflowValidation.Validate(_registry, _graph.ToWorkflowGraph());

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var errors = Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Workflow validation failed:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");

        if (_pluginLoader.Instances.Any(n => n.State != NodeState.Idle))
            await StopAsync(cancellationToken).ConfigureAwait(false);

        var contexts = _pluginLoader.Instances
            .ToDictionary(node => node, node => _graph.CreateContext(node));
        await _nodeManager.StartAsync(node => contexts[node], cancellationToken).ConfigureAwait(false);
        _logger.LogTrace("WorkflowHost started {NodeCount} nodes.", contexts.Count);
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default,
        bool preserveFailure = false)
    {
        await _nodeManager
            .StopAsync(_graph.CreateContext, cancellationToken, preserveFailure)
            .ConfigureAwait(false);
        _logger.LogTrace("WorkflowHost stopped.");
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
        => _nodeManager.ShutdownAsync(_graph.CreateContext, cancellationToken);
    public Task ExecuteNodeCommandAsync(
        int instanceId,
        string commandId,
        CancellationToken cancellationToken = default)
        => _nodeManager.ExecuteNodeCommandAsync(
            instanceId,
            commandId,
            _graph.CreateContext,
            cancellationToken);

    public EditorNode CreateNode(
        string typeName,
        double x,
        double y,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string? id = null,
        string? displayName = null,
        int priority = 0)
    {
        var instance = _pluginLoader.CreateNodeInstance(typeName);
        var node = _graph.AddNode(instance, x, y, parameters, id, displayName, priority);
        _logger.LogTrace("WorkflowHost created node {NodeId} ({TypeName}).", node.Id, typeName);
        return node;
    }

    public bool RemoveNode(string editorNodeId)
    {
        var node = _graph.Nodes.FirstOrDefault(n => n.Id == editorNodeId);
        if (node is null)
            return false;
        _graph.RemoveNode(node);
        return true;
    }

    public void UpdateNode(
        string editorNodeId,
        string? displayName,
        int priority,
        double x,
        double y,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var node = _graph.Nodes.FirstOrDefault(n => n.Id == editorNodeId)
            ?? throw new InvalidOperationException($"Editor node '{editorNodeId}' was not found.");
        _graph.UpdateNode(node, displayName, priority, x, y, parameters);
    }

    public bool Connect(string fromNodeId, string fromPinName, string toNodeId, string toPinName)
    {
        var from = _graph.Nodes.FirstOrDefault(n => n.Id == fromNodeId)
            ?? throw new InvalidOperationException($"Editor node '{fromNodeId}' was not found.");
        var to = _graph.Nodes.FirstOrDefault(n => n.Id == toNodeId)
            ?? throw new InvalidOperationException($"Editor node '{toNodeId}' was not found.");
        return _graph.Connect(from, fromPinName, to, toPinName);
    }

    public bool DisconnectInput(string nodeId, string inputPinName)
    {
        var node = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
            return false;
        var connection = _graph.Connections.FirstOrDefault(c =>
            ReferenceEquals(c.ToNode, node) && c.ToPin == inputPinName);
        if (connection is null)
            return false;
        _graph.Disconnect(connection);
        return true;
    }

    public void Clear()
    {
        _graph.Clear();
        _logger.LogTrace("WorkflowHost cleared the graph.");
    }

    public void LoadWorkflowJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        _graph.Clear();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequireKind(root, JsonValueKind.Object, "workflow document");
            var version = ReadString(root, "version", "Version") ?? "1.0";
            if (!string.Equals(version, "1.0", StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported workflow version '{version}'.");

            var nodes = ReadArray(root, "nodes", "Nodes");
            foreach (var nodeElement in nodes.EnumerateArray())
                LoadNode(nodeElement);
            _graph.EnsurePinIds();

            var connections = ReadArray(root, "connections", "Connections");
            foreach (var connectionElement in connections.EnumerateArray())
            {
                var fromPinId = ReadInt(connectionElement, "fromPinId", "FromPinId");
                var toPinId = ReadInt(connectionElement, "toPinId", "ToPinId");
                if (!_graph.TryConnectByPinId(fromPinId, toPinId))
                    throw new InvalidDataException(
                        $"Saved connection {fromPinId} -> {toPinId} could not be restored.");
            }

            _logger.LogTrace("WorkflowHost loaded {NodeCount} nodes.", _graph.Nodes.Count);
        }
        catch
        {
            _graph.Clear();
            throw;
        }
    }

    public void LoadWorkflowFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Workflow file not found.", path);
        LoadWorkflowJson(File.ReadAllText(path));
    }

    private void LoadNode(JsonElement nodeElement)
    {
        RequireKind(nodeElement, JsonValueKind.Object, "node");
        var id = ReadString(nodeElement, "id", "Id")
            ?? throw new InvalidDataException("A workflow node is missing its id.");
        var logicElement = nodeElement.TryGetProperty("logic", out var logic) ||
                           nodeElement.TryGetProperty("Logic", out logic)
            ? logic
            : throw new InvalidDataException($"Node '{id}' is missing logic.");
        RequireKind(logicElement, JsonValueKind.Object, "node logic");

        var logicObject = JsonNode.Parse(logicElement.GetRawText()) as JsonObject
            ?? throw new InvalidDataException($"Node '{id}' has malformed logic.");
        var typeName = ReadString(logicElement, "name", "Name");
        if (string.IsNullOrWhiteSpace(typeName))
            throw new InvalidDataException($"Node '{id}' is missing name in logic.");

        var instanceId = ReadInt(logicElement, "instanceId", "InstanceId");
        var dependsOn = JsonValueReader.GetIntList(logicObject, "dependsOn");
        var displayName = ReadString(logicElement, "displayName", "DisplayName") ?? string.Empty;
        var x = ReadDouble(nodeElement, "x", "X");
        var y = ReadDouble(nodeElement, "y", "Y");
        var priority = ReadInt(nodeElement, "priority", "Priority");

        var instance = _pluginLoader.CreateNodeInstance(typeName, instanceId, dependsOn, logicObject);
        var parameters = instance.NodeParameters
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        _graph.AddNode(
            instance, x, y, parameters, id, displayName, priority, assignPinIds: false);
    }

    private static WorkflowNodeTypeInfo ToNodeTypeInfo(NodeDescriptor descriptor) =>
        new(
            descriptor.Name,
            descriptor.DisplayName,
            descriptor.Category,
            descriptor.InputPins.Select(ToPinInfo).ToList(),
            descriptor.OutputPins.Select(ToPinInfo).ToList(),
            descriptor.Parameters
                .Select(p => new WorkflowParameterInfo(
                    p.Name,
                    p.DataType.Name,
                    p.DefaultValue,
                    true,
                    null,
                    null,
                    p.Editor,
                    p.Options ?? Array.Empty<string>()))
                .ToList());

    private static WorkflowPinInfo ToPinInfo(PinDescriptor pin) =>
        new(pin.Name, pin.DataType.ToString(), pin.Direction == PinDirection.Input, pin.Required);

    private WorkflowNodeSnapshot ToNodeSnapshot(EditorNode node)
    {
        var runtime = ToRuntimeSnapshot(node.RuntimeNode);
        return new WorkflowNodeSnapshot(
            node.Id,
            node.RuntimeNode.InstanceId,
            node.Name,
            node.RuntimeNode.DisplayName ?? node.DisplayName ?? string.Empty,
            node.Category,
            node.X,
            node.Y,
            node.Priority,
            node.RuntimeNode.InputPins.Select(ToContractPinInfo).ToList(),
            node.RuntimeNode.OutputPins.Select(ToContractPinInfo).ToList(),
            node.RuntimeNode.NodeParameters.Select(ToParameterInfo).ToList(),
            runtime);
    }

    private WorkflowNodeRuntimeSnapshot ToRuntimeSnapshot(BaseNode node)
    {
        var editorNode = _graph.Nodes.FirstOrDefault(n => ReferenceEquals(n.RuntimeNode, node));
        return new WorkflowNodeRuntimeSnapshot(
            editorNode?.Id,
            node.InstanceId,
            node.Name,
            node.DisplayName ?? string.Empty,
            editorNode?.Category ?? string.Empty,
            node.State,
            node.Running,
            node.Connecting,
            node.Failed);
    }

    private static WorkflowPinInfo ToContractPinInfo(BasePin pin) =>
        new(pin.Name, pin.DataType.ToString(), pin.Direction == AgentFlow.Contracts.PinDirection.Input, pin.Required);

    private static WorkflowParameterInfo ToParameterInfo(NodeParameter parameter) =>
        new(
            parameter.Name,
            parameter.Type.Name,
            parameter.Value,
            parameter.IsEditable,
            parameter.Zone,
            parameter.Group,
            parameter.Editor,
            parameter.Options);

    private static JsonElement ReadArray(
        JsonElement root,
        string camelName,
        string pascalName)
    {
        if (root.TryGetProperty(camelName, out var camel) ||
            root.TryGetProperty(pascalName, out camel))
        {
            RequireKind(camel, JsonValueKind.Array, camelName);
            return camel;
        }

        throw new InvalidDataException($"Workflow document is missing '{camelName}'.");
    }

    private static string? ReadString(
        JsonElement element,
        string camelName,
        string pascalName)
    {
        if (element.TryGetProperty(camelName, out var camel) ||
            element.TryGetProperty(pascalName, out camel))
            return camel.ValueKind == JsonValueKind.String ? camel.GetString() : camel.ToString();
        return null;
    }

    private static int ReadInt(
        JsonElement element,
        string camelName,
        string pascalName)
    {
        if (element.TryGetProperty(camelName, out var camel) ||
            element.TryGetProperty(pascalName, out camel))
        {
            if (camel.ValueKind == JsonValueKind.Number && camel.TryGetInt32(out var value))
                return value;
            if (camel.ValueKind == JsonValueKind.String &&
                int.TryParse(camel.GetString(), out value))
                return value;
        }

        return 0;
    }

    private static double ReadDouble(
        JsonElement element,
        string camelName,
        string pascalName)
    {
        if (element.TryGetProperty(camelName, out var camel) ||
            element.TryGetProperty(pascalName, out camel))
        {
            if (camel.ValueKind == JsonValueKind.Number && camel.TryGetDouble(out var value))
                return value;
            if (camel.ValueKind == JsonValueKind.String &&
                double.TryParse(camel.GetString(), out value))
                return value;
        }

        return 0;
    }

    private static void RequireKind(
        JsonElement element,
        JsonValueKind expected,
        string description)
    {
        if (element.ValueKind != expected)
            throw new InvalidDataException(
                $"Expected {description} to be {expected}, but found {element.ValueKind}.");
    }
}


