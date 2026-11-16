// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="PluginLoader.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using AgentFlow.Core.ExternalNodes;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Plugin loader: scans a directory for dlls via reflection, discovers [Node] + BaseNode
/// implementations and registers them. Uses isolated collectible AssemblyLoadContexts so plugins
/// stay decoupled from the UI process; <see cref="UnloadAll"/> releases them once the graph is empty.
/// Besides type discovery it is the <b>single source of truth for node instances</b>: every
/// managed node instance created via <see cref="CreateNodeInstance"/> is tracked in
/// <see cref="Instances"/>. Each instance gets a compact <see cref="int"/> id in the range 1000..5000
/// (<see cref="BaseNode.InstanceId"/>); the smallest unused id is allocated sequentially and freed ids are recycled so the set stays small while nodes
/// are added/removed dynamically. The editor graph routes its add/remove through this class.
/// </summary>
public sealed class PluginLoader : IDisposable
{
    private readonly ILogger _logger;
    private readonly List<AssemblyLoadContext> _contexts = new();
    private readonly HashSet<string> _loadedAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BaseNode> _instances = new();
    private readonly object _gate = new();
    private readonly NodeIdPool _nodeIds = new();
    private static readonly TimeSpan ExternalReleaseTimeout = TimeSpan.FromSeconds(3);
    private NodeRegistry? _registry;
    private ExternalNodeRuntime? _externalNodeRuntime;
    private ExternalNodeRuntime? _pendingExternalNodeRuntime;

    /// <summary>
    /// Assemblies that must resolve from the host's default ALC. Plugins may reference these
    /// libraries directly, but each plugin ALC must not load its own copy: static singletons and
    /// generic type identity would then split into per-plugin worlds.
    /// </summary>
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AgentFlow.Contracts",
        "AgentFlow.Shared",
        "BroadcastKit",
        "BroadcastKit.Contracts"
    };


    public PluginLoader(ILogger logger) => _logger = logger;

    /// <summary>Bind the loader to the live registry after a staged plugin load is committed.</summary>
    internal void UseRegistry(NodeRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>Commit the staged external Worker runtime after the node catalog is swapped.</summary>
    internal void CommitExternalRuntime()
    {
        var previous = _externalNodeRuntime;
        _externalNodeRuntime = _pendingExternalNodeRuntime;
        _pendingExternalNodeRuntime = null;
        previous?.Dispose();
    }

    /// <summary>Discard a staged external Worker runtime when the staged catalog is not committed.</summary>
    internal void DiscardPendingExternalRuntime()
    {
        var pending = _pendingExternalNodeRuntime;
        _pendingExternalNodeRuntime = null;
        pending?.Dispose();
    }
    /// <summary>Release the external Worker runtime while leaving loaded C# assemblies intact.</summary>
    public void Dispose()
    {
        var runtime = _externalNodeRuntime;
        _externalNodeRuntime = null;
        runtime?.Dispose();
    }

    /// <summary>All managed node instances (live view, single source of truth).</summary>
    /// <remarks>Instances are returned in dependency order: a node appears after any node it depends on.</remarks>
    public IReadOnlyList<BaseNode> Instances
    {
        get { lock (_gate) return SortByDependenciesLocked(); }
    }

    /// <summary>
    /// Scan all dlls in the directory and register discovered nodes.
    /// </summary>
    /// <remarks>
    /// A missing or empty directory, a plugin assembly that fails to load, and a directory that
    /// yields no node types at all are all reported at error level: a host with zero node types
    /// is unusable (empty palette, an empty workflow cannot run, and a saved workflow cannot be
    /// restored), so this must be visible in the log rather than a silent no-op.
    /// </remarks>
    public void LoadFromDirectory(
        string pluginDir,
        NodeRegistry registry,
        IReadOnlyCollection<string>? requestedNodeTypes = null,
        bool deferRuntimeCommit = false)
    {
        _registry = registry;
        if (requestedNodeTypes is null && !deferRuntimeCommit)
        {
            _externalNodeRuntime?.Dispose();
            _externalNodeRuntime = null;
        }

        var fullDir = Path.GetFullPath(pluginDir);
        if (!Directory.Exists(fullDir))
        {
            _logger.LogError(
                "Plugin directory not found: {Dir}. No node types are available; check that the deployment contains the Plugins folder.",
                fullDir);
            return;
        }

        var nodeTypesBefore = registry.Nodes.Count;
        var failedDlls = new List<string>();
        var dlls = Directory.EnumerateFiles(fullDir, "*.dll").ToList();

        if (requestedNodeTypes is null)
        {
            LoadAssemblies(dlls, registry, failedDlls);
        }
        else
        {
            LoadAssembliesForNodeTypes(dlls, registry, requestedNodeTypes, failedDlls);
        }

        var external = ExternalNodeCatalog.RegisterFromDirectory(
            Path.Combine(fullDir, "External"),
            registry,
            _logger,
            requestedNodeTypes);
        if (deferRuntimeCommit)
        {
            _pendingExternalNodeRuntime = external.Runtime;
        }
        else if (external.Runtime is not null)
        {
            _externalNodeRuntime = external.Runtime;
        }

        if (requestedNodeTypes is not null)
        {
            LoadRemainingAssembliesForMissingNodeTypes(dlls, registry, requestedNodeTypes, failedDlls);
        }

        var nodeTypesLoaded = registry.Nodes.Count - nodeTypesBefore;
        if (nodeTypesLoaded == 0)
        {
            if (requestedNodeTypes is { Count: 0 })
                return;

            _logger.LogError(
                "No node types were registered from plugin directory {Dir}; {DllCount} DLLs were present and {FailedCount} failed to load.",
                fullDir, dlls.Count, failedDlls.Count);
            return;
        }

        if (requestedNodeTypes is not null)
        {
            var missing = requestedNodeTypes
                .Where(typeName => !registry.Contains(typeName))
                .ToArray();
            if (missing.Length > 0)
            {
                _logger.LogError(
                    "On-demand plugin loading could not register requested node type(s): {NodeTypes}.",
                    string.Join(", ", missing));
            }
        }

        if (failedDlls.Count > 0)
        {
            _logger.LogError(
                "Loaded {NodeTypes} node types from {Dir}, but {FailedCount} of {DllCount} plugin assemblies failed to load: {FailedDlls}",
                nodeTypesLoaded, fullDir, failedDlls.Count, dlls.Count, string.Join(", ", failedDlls));
        }
    }

    private void LoadAssemblies(
        IReadOnlyList<string> dlls,
        NodeRegistry registry,
        List<string> failedDlls)
    {
        foreach (var dll in dlls)
            TryLoadAssembly(dll, registry, failedDlls);
    }

    private void LoadAssembliesForNodeTypes(
        IReadOnlyList<string> dlls,
        NodeRegistry registry,
        IReadOnlyCollection<string> requestedNodeTypes,
        List<string> failedDlls)
    {
        var requestedKeys = requestedNodeTypes
            .Select(NormalizeNodeKey)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = ScanAssemblyCandidates(dlls)
            .Where(candidate => requestedKeys.Contains(candidate.NodeKey))
            .ToArray();

        _logger.LogInformation(
            "On-demand plugin prescan selected {CandidateCount}/{DllCount} managed plugin assemblies for {NodeTypeCount} requested node type(s).",
            candidates.Length,
            dlls.Count,
            requestedNodeTypes.Count);

        foreach (var candidate in candidates)
            TryLoadAssembly(candidate.Path, registry, failedDlls);
    }

    private void LoadRemainingAssembliesForMissingNodeTypes(
        IReadOnlyList<string> dlls,
        NodeRegistry registry,
        IReadOnlyCollection<string> requestedNodeTypes,
        List<string> failedDlls)
    {
        if (requestedNodeTypes.All(registry.Contains))
            return;

        foreach (var dll in dlls)
        {
            if (requestedNodeTypes.All(registry.Contains))
                break;

            var fullPath = Path.GetFullPath(dll);
            if (failedDlls.Contains(Path.GetFileName(dll), StringComparer.OrdinalIgnoreCase))
                continue;

            TryLoadAssembly(fullPath, registry, failedDlls);
        }
    }

    private void TryLoadAssembly(string dll, NodeRegistry registry, List<string> failedDlls)
    {
        var fullPath = Path.GetFullPath(dll);
        if (_loadedAssemblyPaths.Contains(fullPath))
            return;

        try
        {
            LoadAssembly(fullPath, registry);
            _loadedAssemblyPaths.Add(fullPath);
        }
        catch (Exception ex)
        {
            var fileName = Path.GetFileName(dll);
            if (!failedDlls.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                failedDlls.Add(fileName);
            _logger.LogError(ex, "Failed to load plugin: {Dll}", dll);
        }
    }

    private static IReadOnlyList<PluginAssemblyCandidate> ScanAssemblyCandidates(
        IReadOnlyList<string> dlls)
    {
        var candidates = new ConcurrentBag<PluginAssemblyCandidate>();
        Parallel.ForEach(dlls, dll =>
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(Path.GetFullPath(dll));
                var key = NormalizeNodeKey(assemblyName.Name ?? Path.GetFileNameWithoutExtension(dll));
                if (key.StartsWith("node", StringComparison.Ordinal))
                    key = key[4..];
                if (key.Length > 0)
                    candidates.Add(new PluginAssemblyCandidate(Path.GetFullPath(dll), key));
            }
            catch
            {
                // The full loading pass reports malformed assemblies when they are actually loaded.
            }
        });

        return candidates
            .OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeNodeKey(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record PluginAssemblyCandidate(string Path, string NodeKey);

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
            instance.InstanceId = _nodeIds.Allocate();
            _instances.Add(instance);
        }
        _logger.LogTrace("PluginLoader: created node instance #{InstanceId} ({Name})",
            instance.InstanceId, name);
        return instance;
    }

    /// <summary>
    /// Restore a node instance from a saved configuration (used when loading a graph).
    /// Creates the node and its pin instances, reserves the saved instance id, restores
    /// <see cref="BaseNode.DependsOn"/>, and applies the saved logical parameters
    /// (uuid / displayName / instanceId / dependsOn / parameters) from <paramref name="logic"/>.
    /// </summary>
    public BaseNode CreateNodeInstance(
        string name,
        int instanceId,
        IReadOnlyList<int>? dependsOn,
        JsonObject? logic = null)
    {
        var registry = _registry ?? throw new InvalidOperationException(
            "PluginLoader is not initialized: call LoadFromDirectory first.");

        var instance = registry.CreateInstance(name);
        EnsurePinsCreated(instance, registry.Get(name));
        lock (_gate)
        {
            if (instanceId > 0)
            {
                _nodeIds.Reserve(instanceId);
                instance.InstanceId = instanceId;
            }
            else
            {
                instance.InstanceId = _nodeIds.Allocate();
            }
            instance.DependsOn = BaseNode.NormalizeDependsOn(dependsOn);
            _instances.Add(instance);
        }
        if (logic is not null)
        {
            LogicDeserializer.Deserialize(instance, logic);
            LogicDeserializer.RestorePinIds(instance, logic);
        }
        _logger.LogTrace("PluginLoader: restored node instance #{InstanceId} ({Name}, dependsOn={DependsOn})",
            instanceId, name, instance.DependsOnText);
        return instance;
    }

    /// <summary>Compatibility overload for callers that still pass one legacy dependency id.</summary>
    public BaseNode CreateNodeInstance(string name, int instanceId, int dependsOn, JsonObject? logic = null)
        => CreateNodeInstance(
            name,
            instanceId,
            dependsOn > 0 ? new[] { dependsOn } : Array.Empty<int>(),
            logic);

    /// <summary>Delete a managed node instance by its <see cref="BaseNode.InstanceId"/>.</summary>
    /// <returns>True if the instance was found and removed.</returns>
    public bool DeleteNodeInstance(int instanceId)
    {
        BaseNode? instance;
        lock (_gate)
        {
            instance = _instances.FirstOrDefault(i => i.InstanceId == instanceId);
            if (instance is null) return false;
        }

        // Release the Worker-side node before recycling its instance id. Otherwise an old
        // asynchronous cleanup could remove a newly created node that reused the same id.
        ReleaseExternalNode(instance as ExternalNodeProxy);

        lock (_gate)
        {
            if (!_instances.Remove(instance)) return false;
            _nodeIds.Release(instance.InstanceId);
            ReleasePins(instance);
        }
        _logger.LogTrace("PluginLoader: deleted node instance #{InstanceId}", instanceId);
        return true;
    }

    /// <summary>Delete a managed node instance by reference.</summary>
    /// <returns>True if the instance was found and removed.</returns>
    public bool DeleteNodeInstance(BaseNode instance)
    {
        lock (_gate)
        {
            if (!_instances.Contains(instance)) return false;
        }

        ReleaseExternalNode(instance as ExternalNodeProxy);

        lock (_gate)
        {
            if (!_instances.Remove(instance)) return false;
            _nodeIds.Release(instance.InstanceId);
            ReleasePins(instance);
        }
        _logger.LogTrace("PluginLoader: deleted node instance #{InstanceId} ({DisplayName})",
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
    /// Release every pin owned by the node: clear wiring (targets + ConnectedPin), the latest
    /// received value and any bound execution context, so a reused instance starts clean.
    /// (Pin ids are per-document, assigned when the node is added / saved.)
    /// </summary>
    private static void ReleasePins(BaseNode node)
    {
        foreach (var pin in node.InputPins)
            pin.ResetRuntimeState();
        foreach (var pin in node.OutputPins)
            pin.ResetRuntimeState();
    }

    /// <summary>
    /// Start every external Worker process referenced by the current instances without
    /// creating worker-side node instances. This makes load-stage subscription and process
    /// startup explicit before the normal Initialize/Run lifecycle.
    /// </summary>
    public async Task PrepareExternalNodesAsync(CancellationToken cancellationToken = default)
    {
        List<ExternalNodeProxy> proxies;
        lock (_gate)
            proxies = _instances.OfType<ExternalNodeProxy>().ToList();

        foreach (var proxy in proxies)
            await proxy.PrepareAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Remove all managed node instances (e.g. before loading a fresh workflow).</summary>
    public void ClearInstances()
    {
        List<ExternalNodeProxy> external;
        lock (_gate)
            external = _instances.OfType<ExternalNodeProxy>().ToList();
        foreach (var proxy in external)
            ReleaseExternalNode(proxy);

        lock (_gate)
        {
            _instances.Clear();
            _nodeIds.Reset();
        }
    }

    /// <summary>
    /// Release an external node with a bounded wait. If the Worker does not cooperate within
    /// the timeout, its process tree is terminated by process id and the failure is logged.
    /// </summary>
    private void ReleaseExternalNode(ExternalNodeProxy? proxy)
    {
        if (proxy is null)
            return;

        var releaseTask = proxy.ReleaseAsync();
        try
        {
            releaseTask.WaitAsync(ExternalReleaseTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            var processId = proxy.WorkerProcessId;
            var killed = proxy.ForceKillWorkerProcess();
            _logger.LogError(
                "External node #{InstanceId} did not release within {TimeoutMs} ms; forced worker termination pid={Pid} result={Killed}.",
                proxy.InstanceId,
                ExternalReleaseTimeout.TotalMilliseconds,
                processId,
                killed);
            _ = releaseTask.ContinueWith(
                task => _logger.LogError(
                    task.Exception,
                    "External node #{InstanceId} release failed after forced worker termination.",
                    proxy.InstanceId),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release external node #{InstanceId}.", proxy.InstanceId);
        }
    }

    /// <summary>
    /// Unload every plugin AssemblyLoadContext and clear the registry. Only valid when no node
    /// instances are alive; a live type reference would make the collectible context unloadable.
    /// </summary>
    public void UnloadAll()
    {
        lock (_gate)
        {
            if (_instances.Count > 0)
                throw new InvalidOperationException(
                    "Cannot unload plugins while node instances still exist; clear the graph first.");
            _nodeIds.Reset();
        }

        foreach (var context in _contexts)
        {
            try { context.Unload(); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to unload plugin context: {Name}", context.Name); }
        }
        _contexts.Clear();
        _loadedAssemblyPaths.Clear();
        _registry?.Clear();
        _registry = null;
    }

    /// <summary>Return instances in dependency order (caller holds <see cref="_gate"/>).</summary>
    private List<BaseNode> SortByDependenciesLocked() => WorkflowOrdering.SortByDependencies(_instances);

    private void LoadAssembly(string dllPath, NodeRegistry registry)
    {
        // One collectible ALC per plugin dll; host-owned shared libraries resolve to default.
        var alc = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(dllPath), isCollectible: true);
        alc.Resolving += (_, name) =>
        {
            // Host-owned shared assemblies always come from the default ALC. This is what lets
            // plugins share BroadcastKit contracts while resolving the TopicBus supplied by the
            // current Editor/Batch workflow run.
            if (name.Name is not null && SharedAssemblyNames.Contains(name.Name))
            {
                try
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyName(name);
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
            }

            // Resolve private plugin dependencies from the plugin directory.
            var candidate = Path.Combine(Path.GetDirectoryName(dllPath)!, name.Name + ".dll");
            return File.Exists(candidate) ? alc.LoadFromAssemblyPath(Path.GetFullPath(candidate)) : null;
        };
        _contexts.Add(alc);

        var asm = alc.LoadFromAssemblyPath(dllPath);

        // Collect descriptors first, then register atomically for this assembly. A duplicate type
        // name throws before any of this DLL's types are added, so a failed load cannot leave a
        // half-registered plugin behind.
        var descriptors = new List<NodeDescriptor>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in asm.GetTypes())
        {
            if (!typeof(BaseNode).IsAssignableFrom(type) || type.IsAbstract)
                continue;

            // Instantiate once to probe the pin/parameter metadata (static per type).
            if (Activator.CreateInstance(type) is not BaseNode probe)
                continue;

            if (!names.Add(probe.Name) || registry.Contains(probe.Name))
                throw new InvalidOperationException(
                    $"Duplicate node type '{probe.Name}' in plugin '{Path.GetFileName(dllPath)}'.");

            descriptors.Add(new NodeDescriptor(
                probe.Name, probe.DisplayName ?? "", probe.Category, type, probe.InputPins, probe.OutputPins, probe.Parameters));
        }

        foreach (var descriptor in descriptors)
        {
            registry.Register(descriptor);
            _logger.LogTrace("Registered node: {Name} ({DisplayName}) <- {Dll}",
                descriptor.Name, descriptor.DisplayName, Path.GetFileName(dllPath));
        }
    }
}
