using AgentFlow.Contracts;
using ContractPinDirection = AgentFlow.Contracts.PinDirection;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// 编辑态图节点：持有一个 INode 实例 + 它的所有运行时 pin（输入/输出）。
/// 连接关系由 <see cref="RuntimeOutputPin.Targets"/> 直接维护——
/// 输出 pin 保存输入 pin 引用，数据直接 Send→Receive 穿透。
/// </summary>
public sealed class EditorNode
{
    public string Id { get; }
    public string TypeId { get; }
    public string DisplayName { get; }
    public string Category { get; }

    /// <summary>User-editable instance name (persisted to JSON).</summary>
    public string? Name { get; set; }

    /// <summary>Start-up priority, mirroring ALC's "Startup Priority".</summary>
    public int Priority { get; set; }

    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>节点运行时实例（契约实现）。</summary>
    public INode RuntimeNode { get; }

    /// <summary>Current parameter values owned by the editor graph (persisted to JSON).</summary>
    public Dictionary<string, object?> Parameters { get; set; } = new();

    /// <summary>所有输入 pin（按名称索引）。</summary>
    public IReadOnlyDictionary<string, RuntimeInputPin> Inputs { get; }

    /// <summary>所有输出 pin（按名称索引）。</summary>
    public IReadOnlyDictionary<string, RuntimeOutputPin> Outputs { get; }

    public EditorNode(
        string id,
        NodeDescriptor descriptor,
        INode runtimeNode,
        double x,
        double y,
        string? name = null,
        int priority = 0,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        Id = id;
        TypeId = descriptor.TypeId;
        DisplayName = descriptor.DisplayName;
        Category = descriptor.Category;
        Name = name;
        Priority = priority;
        X = x;
        Y = y;
        RuntimeNode = runtimeNode;
        Parameters = parameters is null ? new() : new Dictionary<string, object?>(parameters);

        var inputs = new Dictionary<string, RuntimeInputPin>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, RuntimeOutputPin>(StringComparer.Ordinal);
        foreach (var pin in descriptor.RuntimePins)
        {
            if (pin.Direction == ContractPinDirection.Input)
                inputs[pin.Name] = new RuntimeInputPin(pin, runtimeNode);
            else
                outputs[pin.Name] = new RuntimeOutputPin(pin, runtimeNode);
        }
        Inputs = inputs;
        Outputs = outputs;
    }
}

/// <summary>编辑态连接：一条从输出 pin 到输入 pin 的线。</summary>
public sealed class EditorConnection
{
    public EditorNode FromNode { get; }
    public string FromPin { get; }
    public EditorNode ToNode { get; }
    public string ToPin { get; }

    public EditorConnection(EditorNode fromNode, string fromPin, EditorNode toNode, string toPin)
    {
        FromNode = fromNode;
        FromPin = fromPin;
        ToNode = toNode;
        ToPin = toPin;
    }

    public RuntimeOutputPin SourcePin => FromNode.Outputs[FromPin];
    public RuntimeInputPin TargetPin => ToNode.Inputs[ToPin];
}

/// <summary>
/// 编辑态图管理器：GUI 层只调用这里的方法增删节点/连线/保存/加载，
/// 不自己维护 pin 间引用。连接建立后，输出 pin 直接持有输入 pin 引用，
/// 运行时 Send/Receive 沿引用直接穿透。
/// </summary>
public sealed class EditorGraph
{
    private readonly NodeRegistry _registry;
    private readonly PluginLoader _pluginLoader;
    private readonly ILogger _logger;
    private readonly IGuiBridge _gui;

    public List<EditorNode> Nodes { get; } = new();
    public List<EditorConnection> Connections { get; } = new();

    /// <summary>连接建立/断开/节点增删/节点属性变更时触发（GUI 层订阅以刷新 IsConnected 等外观）。</summary>
    public event Action? GraphChanged;

    public EditorGraph(NodeRegistry registry, ILoggerFactory loggerFactory, PluginLoader pluginLoader, IGuiBridge gui)
    {
        _registry = registry;
        _pluginLoader = pluginLoader;
        _logger = loggerFactory.CreateLogger(nameof(EditorGraph));
        _gui = gui;
    }

    /// <summary>从节点类型创建一个编辑态节点实例。保留 <paramref name="id"/> 以支持加载时 ID 稳定。</summary>
    public EditorNode AddNode(
        string typeId,
        double x,
        double y,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string? id = null,
        string? name = null,
        int priority = 0)
    {
        var descriptor = _registry.Get(typeId);
        var instance = _pluginLoader.CreateNodeInstance(typeId);
        if (parameters is not null && parameters.Count > 0)
            instance.Configure(parameters);

        var node = new EditorNode(
            id ?? Guid.NewGuid().ToString("N")[..8],
            descriptor,
            instance,
            x,
            y,
            name,
            priority,
            parameters);
        Nodes.Add(node);
        _logger.LogInformation("EditorGraph: added node {Id} ({TypeId})", node.Id, typeId);
        GraphChanged?.Invoke();
        return node;
    }

    /// <summary>建立一条连接：输出 pin 保存输入 pin 引用。</summary>
    public void Connect(EditorNode fromNode, string fromPin, EditorNode toNode, string toPin)
    {
        if (!fromNode.Outputs.TryGetValue(fromPin, out var output))
            throw new InvalidOperationException($"Node {fromNode.TypeId} has no output pin: {fromPin}");
        if (!toNode.Inputs.TryGetValue(toPin, out var input))
            throw new InvalidOperationException($"Node {toNode.TypeId} has no input pin: {toPin}");

        if (Connections.Any(c =>
                ReferenceEquals(c.ToNode, toNode) && c.ToPin == toPin))
            throw new InvalidOperationException($"Input pin {toNode.TypeId}.{toPin} is already connected.");

        // 类型检查 + 建立引用（RuntimeOutputPin.Connect 内部做类型校验并加入 _targets）
        output.Connect(input);

        Connections.Add(new EditorConnection(fromNode, fromPin, toNode, toPin));
        _logger.LogDebug("EditorGraph: wired {From}.{FromPin} -> {To}.{ToPin}",
            fromNode.Id, fromPin, toNode.Id, toPin);
        GraphChanged?.Invoke();
    }

    /// <summary>删除一条连接：输出 pin 移除对输入 pin 的引用。</summary>
    public void Disconnect(EditorConnection conn)
    {
        var output = conn.SourcePin;
        var input = conn.TargetPin;

        output.Disconnect(input);

        Connections.Remove(conn);
        _logger.LogDebug("EditorGraph: unwired {From}.{FromPin} -> {To}.{ToPin}",
            conn.FromNode.Id, conn.FromPin, conn.ToNode.Id, conn.ToPin);
        GraphChanged?.Invoke();
    }

    /// <summary>删除节点及其所有连线。</summary>
    public void RemoveNode(EditorNode node)
    {
        var related = Connections
            .Where(c => ReferenceEquals(c.FromNode, node) || ReferenceEquals(c.ToNode, node))
            .ToList();
        foreach (var c in related)
            Disconnect(c);

        Nodes.Remove(node);
        _pluginLoader.DeleteNodeInstance(node.RuntimeNode);
        _logger.LogInformation("EditorGraph: removed node {Id}", node.Id);
        GraphChanged?.Invoke();
    }

    /// <summary>同步编辑态节点属性（名称/优先级/坐标/参数），供 UI 在保存前调用。</summary>
    public void UpdateNode(
        EditorNode node,
        string? name,
        int priority,
        double x,
        double y,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        node.Name = name;
        node.Priority = priority;
        node.X = x;
        node.Y = y;
        node.Parameters = parameters is null ? new() : new Dictionary<string, object?>(parameters);
        GraphChanged?.Invoke();
    }

    /// <summary>把编辑态图导出为可序列化/可执行的工作流图。</summary>
    public WorkflowGraph ToWorkflowGraph()
    {
        var graph = new WorkflowGraph();
        foreach (var node in Nodes)
        {
            graph.Nodes.Add(new NodeSpec
            {
                Id = node.Id,
                TypeId = node.TypeId,
                Name = node.Name,
                Priority = node.Priority,
                X = node.X,
                Y = node.Y,
                Parameters = new Dictionary<string, object?>(node.Parameters)
            });
        }

        foreach (var c in Connections)
        {
            graph.Connections.Add(new ConnectionSpec
            {
                FromNode = c.FromNode.Id,
                FromPin = c.FromPin,
                ToNode = c.ToNode.Id,
                ToPin = c.ToPin
            });
        }

        return graph;
    }

    /// <summary>清空整个图（加载新工作流前调用）。</summary>
    public void Clear()
    {
        foreach (var conn in Connections.ToList())
            conn.SourcePin.Disconnect(conn.TargetPin);

        Connections.Clear();
        Nodes.Clear();
        // 让 PluginLoader 容器成为实例唯一管理源：清图时一并清空其实例。
        _pluginLoader.ClearInstances();
        GraphChanged?.Invoke();
    }
    /// <summary>
    /// 让某个编辑态节点立即执行一次并沿已建立的连线向下游推送数据（上下文菜单的 Send）。
    /// 使用该节点已接线的运行时 pin：<see cref="INodeContext.GetInput{T}"/> 读取输入 pin 的
    /// 最新值，<see cref="INodeContext.SetOutput"/> 调用输出 pin 的 Send 推给下游输入 pin。
    /// </summary>
    public async Task SendAsync(EditorNode node, CancellationToken ct = default)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        var ctx = new EditorNodeContext(node, _logger, _gui);
        await node.RuntimeNode.ExecuteAsync(ctx, ct);
        _logger.LogInformation("EditorGraph: sent node {Id} ({TypeId})", node.Id, node.TypeId);
    }

    /// <summary>编辑态 Send 用的节点上下文：绑定到该节点已接线的运行时 pin。</summary>
    private sealed class EditorNodeContext : INodeContext
    {
        private readonly EditorNode _node;
        public ILogger Logger { get; }
        public IGuiBridge Gui { get; }

        public EditorNodeContext(EditorNode node, ILogger logger, IGuiBridge gui)
        {
            _node = node;
            Logger = logger;
            Gui = gui;
        }

        public T? GetInput<T>(string pinName) =>
            _node.Inputs.TryGetValue(pinName, out var pin) && pin.Value is T t ? t : default;

        public void SetOutput(string pinName, object? value)
        {
            if (_node.Outputs.TryGetValue(pinName, out var pin))
                pin.Send(value);
        }
    }
}
