using System.Reflection;
using System.Runtime.Loader;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Plugin loader: scans a directory for dlls via reflection, discovers [Node] + INode
/// implementations and registers them. Uses isolated AssemblyLoadContexts so plugins
/// stay decoupled from the UI process (unload support can be added later).
/// </summary>
public sealed class PluginLoader
{
    private readonly ILogger _logger;
    private readonly List<AssemblyLoadContext> _contexts = new();

    public PluginLoader(ILogger logger) => _logger = logger;

    /// <summary>Scan all dlls in the directory and register discovered nodes.</summary>
    public void LoadFromDirectory(string pluginDir, NodeRegistry registry)
    {
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
            if (attr is null || !typeof(INode).IsAssignableFrom(type) || type.IsAbstract)
                continue;

            // Instantiate once to probe the pin/parameter metadata (static per type).
            if (Activator.CreateInstance(type) is not INode probe)
                continue;

            registry.Register(new NodeDescriptor(
                attr.TypeId, attr.DisplayName, attr.Category, type, probe.Pins, probe.Parameters));

            _logger.LogInformation("Registered node: {TypeId} ({Name}) <- {Dll}",
                attr.TypeId, attr.DisplayName, Path.GetFileName(dllPath));
        }
    }
}
