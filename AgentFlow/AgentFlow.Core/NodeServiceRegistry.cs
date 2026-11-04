// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeServiceRegistry.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Host-provided services that <see cref="INodeContext.GetService{T}"/> resolves for nodes.
/// Hosts register either a singleton service or a per-node factory. Core stays independent of
/// concrete service implementations (BroadcastKit, UI, LLM clients).
/// </summary>
public sealed class NodeServiceRegistry
{
    private readonly Dictionary<Type, object> _singletons = new();
    private readonly Dictionary<Type, Func<BaseNode, object?>> _factories = new();
    private readonly object _gate = new();

    /// <summary>Register one singleton instance for the service type.</summary>
    public NodeServiceRegistry Add<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_gate)
        {
            _factories.Remove(typeof(T));
            _singletons[typeof(T)] = service;
        }

        return this;
    }

    /// <summary>
    /// Register a per-node factory. A context can only resolve it with its own node, so each
    /// node gets its own service view (for example a host broadcast facade bound to a node id).
    /// </summary>
    public NodeServiceRegistry AddFactory<T>(Func<BaseNode, T?> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            _singletons.Remove(typeof(T));
            _factories[typeof(T)] = node => factory(node);
        }

        return this;
    }

    /// <summary>Remove a singleton or factory registration.</summary>
    public bool Remove<T>()
    {
        lock (_gate)
        {
            return _singletons.Remove(typeof(T)) | _factories.Remove(typeof(T));
        }
    }

    /// <summary>Resolve a service; per-node factories return null when no node is supplied.</summary>
    public object? Get<T>(BaseNode? node = null)
    {
        Func<BaseNode, object?>? factory = null;
        object? singleton = null;
        var hasSingleton = false;

        lock (_gate)
        {
            if (_factories.TryGetValue(typeof(T), out factory))
            {
                // factory wins; a singleton for the same type is removed on registration.
            }
            else if (_singletons.TryGetValue(typeof(T), out singleton))
            {
                hasSingleton = true;
            }
        }

        if (factory is not null)
            return node is null ? null : factory(node);

        return hasSingleton ? singleton : null;
    }
}
