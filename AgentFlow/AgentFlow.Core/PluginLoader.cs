// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="PluginLoader.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Reflection;
using System.Runtime.Loader;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Plugin loader: scans a directory for dlls via reflection, discovers [Node] + BaseNode
/// implementations and registers them. Uses isolated AssemblyLoadContexts so plugins
/// stay decoupled from the UI process (unload support can be added later).
/// Besides type discovery it is the <b>single source of truth for node instances</b>: every
/// managed node instance created via <see cref="CreateNodeInstance"/> is tracked in
/// <see cref="Instances"/>. Each instance gets a compact <see cref="int"/> id in the range 1000..10000
/// (<see cref="BaseNode.InstanceId"/>); the smallest unused id is allocated sequentially and freed ids are recycled so the set stays small while nodes
/// are added/removed dynamically. The editor graph routes its add/remove through this class.
/// </summary>
public sealed class PluginLoader
{
    private readonly ILogger _logger;
    private readonly List<AssemblyLoadContext> _contexts = new();
    private readonly List<BaseNode> _instances = new();
    private readonly SortedSet<int> _freeIds = new();
    private readonly object _gate = new();
    private NodeRegistry? _registry;
    private const int MinInstanceId = 1000;
    private const int MaxInstanceId = 10000;

    private int _nextId = MinInstanceId;

    public PluginLoader(ILogger logger) => _logger = logger;

    /// <summary>All managed node instances (live view, single source of truth).</summary>
    public IReadOnlyList<BaseNode> Instances
    {
        get { lock (_gate) return _instances.ToList(); }
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
    public BaseNode CreateNodeInstance(string typeId)
    {
        var registry = _registry ?? throw new InvalidOperationException(
            "PluginLoader is not initialized: call LoadFromDirectory first.");

        var instance = registry.CreateInstance(typeId);
        lock (_gate)
        {
            instance.InstanceId = AllocateIdLocked();
            _instances.Add(instance);
        }
        _logger.LogInformation("PluginLoader: created node instance #{InstanceId} ({TypeId})",
            instance.InstanceId, typeId);
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
            FreeIdLocked(_instances[idx].InstanceId);
            _instances.RemoveAt(idx);
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
            FreeIdLocked(instance.InstanceId);
        }
        _logger.LogInformation("PluginLoader: deleted node instance #{InstanceId} ({TypeId})",
            instance.InstanceId, instance.TypeId);
        return true;
    }

    /// <summary>Remove all managed node instances (e.g. before loading a fresh workflow).</summary>
    public void ClearInstances()
    {
        lock (_gate)
        {
            _instances.Clear();
            _freeIds.Clear();
            _nextId = MinInstanceId;
        }
    }

    /// <summary>Assign the next available id (caller holds <see cref="_gate"/>).</summary>
    private int AllocateIdLocked()
    {
        // Prefer reusing the smallest id from the free pool.
        if (_freeIds.Count > 0)
        {
            int id = _freeIds.Min;
            _freeIds.Remove(id);
            return id;
        }
        // Otherwise allocate sequentially (default +1), capped at 10000.
        if (_nextId <= MaxInstanceId)
            return _nextId++;
        throw new InvalidOperationException(
            $"No available instance id in range [{MinInstanceId}, {MaxInstanceId}].");
    }

    /// <summary>Return an id to the free pool (caller holds <see cref="_gate"/>).</summary>
    private void FreeIdLocked(int id)
    {
        if (id >= MinInstanceId && id <= MaxInstanceId)
            _freeIds.Add(id);
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
            var attr = type.GetCustomAttribute<NodeAttribute>();
            if (attr is null || !typeof(BaseNode).IsAssignableFrom(type) || type.IsAbstract)
                continue;

            // Instantiate once to probe the pin/parameter metadata (static per type).
            if (Activator.CreateInstance(type) is not BaseNode probe)
                continue;

            registry.Register(new NodeDescriptor(
                attr.TypeId, attr.DisplayName, attr.Category, type, probe.InputPins, probe.OutputPins, probe.Parameters));

            _logger.LogInformation("Registered node: {TypeId} ({Name}) <- {Dll}",
                attr.TypeId, attr.DisplayName, Path.GetFileName(dllPath));
        }
    }
}
