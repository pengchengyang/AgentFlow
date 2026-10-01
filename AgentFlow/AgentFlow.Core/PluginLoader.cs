// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="PluginLoader.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Plugin loader: scans a directory for dlls via reflection, discovers [Node] + BaseNode
/// implementations and registers them. Uses isolated AssemblyLoadContexts so plugins
/// stay decoupled from the UI process (unload support can be added later).
/// Besides type discovery it is the <b>single source of truth for node instances</b>: every
/// managed node instance created via <see cref="CreateNodeInstance"/> is tracked in
/// <see cref="Instances"/>. Each instance gets a compact <see cref="int"/> id in the range 1000..5000
/// (<see cref="BaseNode.InstanceId"/>); the smallest unused id is allocated sequentially and freed ids are recycled so the set stays small while nodes
/// are added/removed dynamically. The editor graph routes its add/remove through this class.
/// </summary>
public sealed class PluginLoader
{
    private readonly ILogger _logger;
    private readonly List<AssemblyLoadContext> _contexts = new();
    private readonly List<BaseNode> _instances = new();
    private readonly object _gate = new();
    private NodeRegistry? _registry;


    public PluginLoader(ILogger logger) => _logger = logger;

    /// <summary>All managed node instances (live view, single source of truth).</summary>
    /// <remarks>Instances are returned in dependency order: a node appears after any node it depends on.</remarks>
    public IReadOnlyList<BaseNode> Instances
    {
        get { lock (_gate) return SortByDependenciesLocked(); }
    }

    /// <summary>Scan all dlls in the directory and register discovered nodes.</summary>
    public void LoadFromDirectory(string pluginDir, NodeRegistry registry)
    {
        _registry = registry;
        if (!Directory.Exists(pluginDir))
        {
            _logger.LogWarning("Plugin directory does not exist: {Dir}", pluginDir);
            return;
        }

        var fullDir = Path.GetFullPath(pluginDir);
        foreach (var dll in Directory.EnumerateFiles(fullDir, "*.dll"))
        {
            try
            {
                LoadAssembly(Path.GetFullPath(dll), registry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin: {Dll}", dll);
            }
        }
    }

    /// <summary>
    /// Create a managed node instance, assign it a compact <see cref="int"/>
    /// <see cref="BaseNode.InstanceId"/> (reusing a freed id when available) and track it.
    /// </summary>
    public BaseNode CreateNodeInstance(string name)
    {
        var registry = _registry ?? throw new InvalidOperationException(
            "PluginLoader is not initialized: call LoadFromDirectory first.");

        var instance = registry.CreateInstance(name);
        EnsurePinsCreated(instance, registry.Get(name));
        lock (_gate)
        {
            instance.InstanceId = NodeIdPool.Allocate();
            _instances.Add(instance);
        }
        _logger.LogInformation("PluginLoader: created node instance #{InstanceId} ({Name})",
            instance.InstanceId, name);
        return instance;
    }

    /// <summary>
    /// Restore a node instance from a saved configuration (used when loading a graph).
    /// Creates the node and its pin instances, reserves the saved instance id, restores
    /// <see cref="BaseNode.DependsOn"/>, and applies the saved logical parameters
    /// (uuid / displayName / instanceId / dependsOn / parameters) from <paramref name="logic"/>.
    /// </summary>
    public BaseNode CreateNodeInstance(string name, int instanceId, int dependsOn, JsonObject? logic = null)
    {
        var registry = _registry ?? throw new InvalidOperationException(
            "PluginLoader is not initialized: call LoadFromDirectory first.");

        var instance = registry.CreateInstance(name);
        EnsurePinsCreated(instance, registry.Get(name));
        lock (_gate)
        {
            if (instanceId > 0)
            {
                NodeIdPool.Reserve(instanceId);
                instance.InstanceId = instanceId;
            }
            else
            {
                instance.InstanceId = NodeIdPool.Allocate();
            }
            instance.DependsOn = dependsOn;
            _instances.Add(instance);
        }
        if (logic is not null)
        {
            LogicDeserializer.Deserialize(instance, logic);
            LogicDeserializer.RestorePinIds(instance, logic);
        }
        _logger.LogInformation("PluginLoader: restored node instance #{InstanceId} ({Name}, dependsOn={DependsOn})",
            instanceId, name, dependsOn);
        return instance;
    }

    /// <summary>Delete a managed node instance by its <see cref="BaseNode.InstanceId"/>.</summary>
    /// <returns>True if the instance was found and removed.</returns>
    public bool DeleteNodeInstance(int instanceId)
    {
        lock (_gate)
        {
            int idx = _instances.FindIndex(i => i.InstanceId == instanceId);
            if (idx < 0) return false;
            var instance = _instances[idx];
            _instances.RemoveAt(idx);
            NodeIdPool.Release(instance.InstanceId);
            ReleasePins(instance);
        }
        _logger.LogInformation("PluginLoader: deleted node instance #{InstanceId}", instanceId);
        return true;
    }

    /// <summary>Delete a managed node instance by reference.</summary>
    /// <returns>True if the instance was found and removed.</returns>
    public bool DeleteNodeInstance(BaseNode instance)
    {
        lock (_gate)
        {
            if (!_instances.Remove(instance)) return false;
            NodeIdPool.Release(instance.InstanceId);
            ReleasePins(instance);
        }
        _logger.LogInformation("PluginLoader: deleted node instance #{InstanceId} ({DisplayName})",
            instance.InstanceId, instance.DisplayName);
        return true;
    }

    /// <summary>
    /// Ensure the node actually owns its pin instances. If the derived node constructor did not
    /// create a pin declared by the descriptor, create it here (same metadata as the DLL probe).
    /// Then attach every pin to the node so <see cref="BasePin.Owner"/> is set.
    /// </summary>
    private static void EnsurePinsCreated(BaseNode node, NodeDescriptor descriptor)
    {
        foreach (var pin in descriptor.InputPins)
        {
            if (node.InputPins.Any(p => p.Name == pin.Name)) continue;
            node.AddInputPin(new BasePin(
                pin.Name,
                pin.DataType,
                (AgentFlow.Contracts.PinDirection)pin.Direction,
                pin.Required));
        }

        foreach (var pin in descriptor.OutputPins)
        {
            if (node.OutputPins.Any(p => p.Name == pin.Name)) continue;
            node.AddOutputPin(new BasePin(
                pin.Name,
                pin.DataType,
                (AgentFlow.Contracts.PinDirection)pin.Direction,
                pin.Required));
        }

        foreach (var pin in node.InputPins)
            pin.Owner = node;
        foreach (var pin in node.OutputPins)
            pin.Owner = node;
    }

    /// <summary>
    /// Release every pin owned by the node: clear any bidirectional ConnectedPin references
    /// and return the pin ids to <see cref="PinIdPool"/> so they can be reused.
    /// </summary>
    private static void ReleasePins(BaseNode node)
    {
        foreach (var pin in node.InputPins)
            ReleasePin(pin);
        foreach (var pin in node.OutputPins)
            ReleasePin(pin);
    }

    private static void ReleasePin(BasePin pin)
    {
        if (pin.ConnectedPin is { } other && ReferenceEquals(other.ConnectedPin, pin))
            other.ConnectedPin = null;
        pin.ConnectedPin = null;
        pin.ReleaseId();
    }

    /// <summary>Remove all managed node instances (e.g. before loading a fresh workflow).</summary>
    public void ClearInstances()
    {
        lock (_gate)
        {
            _instances.Clear();
            NodeIdPool.Reset();
        }
    }

    /// <summary>Return instances in dependency order (caller holds <see cref="_gate"/>).</summary>
    private List<BaseNode> SortByDependenciesLocked()
    {
        var byId = _instances.ToDictionary(n => n.InstanceId);
        var dependents = new Dictionary<int, List<BaseNode>>();
        var indegree = _instances.ToDictionary(n => n, _ => 0);

        foreach (var node in _instances)
        {
            if (node.DependsOn == 0 || !byId.TryGetValue(node.DependsOn, out var dependency))
                continue;

            if (!dependents.TryGetValue(dependency.InstanceId, out var list))
            {
                list = new List<BaseNode>();
                dependents[dependency.InstanceId] = list;
            }

            list.Add(node);
            indegree[node]++;
        }

        var queue = new Queue<BaseNode>(_instances.Where(n => indegree[n] == 0));
        var sorted = new List<BaseNode>(_instances.Count);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            sorted.Add(node);

            if (!dependents.TryGetValue(node.InstanceId, out var next))
                continue;

            foreach (var dependent in next)
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (sorted.Count < _instances.Count)
            sorted.AddRange(_instances.Where(n => !sorted.Contains(n)));

        return sorted;
    }

    private void LoadAssembly(string dllPath, NodeRegistry registry)
    {
        // One collectible ALC per plugin dll; Contracts resolves to the shared default context.
        var alc = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(dllPath), isCollectible: true);
        alc.Resolving += (_, name) =>
        {
            // Resolve plugin dependencies from the plugin directory first.
            var candidate = Path.Combine(Path.GetDirectoryName(dllPath)!, name.Name + ".dll");
            return File.Exists(candidate) ? alc.LoadFromAssemblyPath(Path.GetFullPath(candidate)) : null;
        };
        _contexts.Add(alc);

        var asm = alc.LoadFromAssemblyPath(dllPath);

        foreach (var type in asm.GetTypes())
        {
            if (!typeof(BaseNode).IsAssignableFrom(type) || type.IsAbstract)
                continue;

            // Instantiate once to probe the pin/parameter metadata (static per type).
            if (Activator.CreateInstance(type) is not BaseNode probe)
                continue;

            registry.Register(new NodeDescriptor(
                probe.Name, probe.DisplayName ?? "", probe.Category, type, probe.InputPins, probe.OutputPins, probe.Parameters));

            _logger.LogInformation("Registered node: {Name} ({DisplayName}) <- {Dll}",
                probe.Name, probe.DisplayName, Path.GetFileName(dllPath));
        }
    }
}
