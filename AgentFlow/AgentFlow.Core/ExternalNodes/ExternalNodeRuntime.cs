// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalNodeRuntime.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>
/// Shared external-node runtime. It owns one Worker process per Package and bridges worker
/// broadcasts through the host's process-wide TopicBus.
/// </summary>
public sealed class ExternalNodeRuntime : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly ITopicBus _topicBus;
    private readonly object _bridgeGate = new();
    private readonly ConcurrentDictionary<string, ExternalWorkerProcess> _workers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ExternalWorkerProcess> _nodeOwners = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _subscriptions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ITopicSubscription> _bridgeSubscriptions =
        new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<object, BroadcastSource> _publishSources = new();
    private int _disposed;

    public ExternalNodeRuntime(ILogger logger, ITopicBus? topicBus = null)
    {
        _logger = logger;
        _topicBus = topicBus ?? TopicBus.Instance;
    }

    public ExternalWorkerProcess GetWorker(ExternalNodeDefinition definition)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _workers.GetOrAdd(definition.PackageId,
            _ => new ExternalWorkerProcess(definition, _logger, this));
    }

    public void RegisterNode(int nodeInstanceId, ExternalWorkerProcess worker)
    {
        _nodeOwners[nodeInstanceId] = worker;
    }

    public void UnregisterNode(int nodeInstanceId)
    {
        _nodeOwners.TryRemove(nodeInstanceId, out _);
        RemoveSubscriptions(nodeInstanceId);
    }

    public void Subscribe(int nodeInstanceId, string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            throw new InvalidDataException("Broadcast topic must not be empty.");

        var topicSubscribers = _subscriptions.GetOrAdd(topic,
            _ => new ConcurrentDictionary<int, byte>());
        topicSubscribers[nodeInstanceId] = 0;
        EnsureBridgeSubscription(topic);
    }

    public void Unsubscribe(int nodeInstanceId, string topic)
    {
        if (_subscriptions.TryGetValue(topic, out var subscribers))
        {
            subscribers.TryRemove(nodeInstanceId, out _);
            if (subscribers.IsEmpty)
            {
                _subscriptions.TryRemove(topic, out _);
                DisposeBridgeSubscription(topic);
            }
        }
    }

    private void RemoveSubscriptions(int nodeInstanceId)
    {
        foreach (var pair in _subscriptions)
        {
            pair.Value.TryRemove(nodeInstanceId, out _);
            if (pair.Value.IsEmpty)
            {
                _subscriptions.TryRemove(pair.Key, out _);
                DisposeBridgeSubscription(pair.Key);
            }
        }
    }

    /// <summary>
    /// Publish a Worker-originated message through the shared TopicBus. The exact payload object
    /// is tagged with its source node so the bridge does not echo the message back to its sender.
    /// </summary>
    public async Task PublishBroadcastFromWorkerAsync(
        int sourceNodeInstanceId,
        string topic,
        JsonObject message,
        CancellationToken cancellationToken)
    {
        var localPayload = ExternalBroadcastCodec.ToLocalPayload(message);
        var broadcastId = ExternalProtocol.GetString(message, "messageId") ?? Guid.NewGuid().ToString("N");
        var runId = ExternalProtocol.GetString(message, "runId");
        _publishSources.Add(localPayload, new BroadcastSource(sourceNodeInstanceId, runId, broadcastId));

        Guid? correlationId = Guid.TryParse(broadcastId, out var parsed) ? parsed : null;
        await _topicBus.PublishAsync(topic, localPayload, correlationId, cancellationToken)
            .ConfigureAwait(false);
    }

    private void EnsureBridgeSubscription(string topic)
    {
        lock (_bridgeGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _bridgeSubscriptions.ContainsKey(topic))
                return;

            var subscription = _topicBus.Subscribe(topic,
                message => BridgeTopicMessageAsync(topic, message));
            _bridgeSubscriptions[topic] = subscription;
            _logger.LogDebug("External broadcast bridge subscribed to {Topic}.", topic);
        }
    }

    private void DisposeBridgeSubscription(string topic)
    {
        lock (_bridgeGate)
        {
            if (!_bridgeSubscriptions.TryRemove(topic, out var subscription))
                return;
            subscription.Dispose();
            _logger.LogDebug("External broadcast bridge unsubscribed from {Topic}.", topic);
        }
    }

    private async Task BridgeTopicMessageAsync(string topic, TopicMessage message)
    {
        int? sourceNodeInstanceId = null;
        string? runId = null;
        string broadcastId = message.MessageId.ToString("N");

        try
        {
            if (message.Payload is { } payload &&
                _publishSources.TryGetValue(payload, out var source))
            {
                sourceNodeInstanceId = source.NodeInstanceId;
                runId = source.RunId;
                broadcastId = source.BroadcastId;
            }

            var native = ToBroadcastTopicMessage(message.Payload);
            var wire = ExternalBroadcastCodec.ToWorkerPayload(native);
            await DeliverBroadcastToExternalAsync(
                topic,
                sourceNodeInstanceId,
                broadcastId,
                wire.PayloadKind,
                wire.Payload,
                runId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to bridge TopicBus message {MessageId} on {Topic} to external Workers.",
                message.MessageId, topic);
        }
    }

    private async Task DeliverBroadcastToExternalAsync(
        string topic,
        int? sourceNodeInstanceId,
        string broadcastId,
        string payloadKind,
        JsonNode? payload,
        string? runId,
        CancellationToken cancellationToken)
    {
        if (!_subscriptions.TryGetValue(topic, out var subscribers))
            return;

        foreach (var targetNodeInstanceId in subscribers.Keys)
        {
            if (sourceNodeInstanceId is not null && targetNodeInstanceId == sourceNodeInstanceId.Value)
                continue;
            if (!_nodeOwners.TryGetValue(targetNodeInstanceId, out var worker))
                continue;

            try
            {
                await worker.DeliverBroadcastAsync(
                    targetNodeInstanceId,
                    sourceNodeInstanceId ?? 0,
                    topic,
                    broadcastId,
                    payloadKind,
                    payload?.DeepClone(),
                    runId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deliver external broadcast {Topic} to #{Target}; message was dropped.",
                    topic, targetNodeInstanceId);
            }
        }
    }

    private static BroadcastTopicMessage ToBroadcastTopicMessage(object? payload)
    {
        return payload switch
        {
            null => new BroadcastTopicMessage(),
            BroadcastTopicMessage native => native,
            string text => BroadcastTopicMessage.FromPayload(text),
            Sample sample => BroadcastTopicMessage.FromSample(sample),
            IReadOnlyList<Sample> samples => BroadcastTopicMessage.FromSamples(samples),
            IReadOnlyDictionary<string, Sample> samplesByKey
                => BroadcastTopicMessage.FromSampleDictionary(samplesByKey),
            IReadOnlyDictionary<string, IReadOnlyList<Sample>> sampleListsByKey
                => BroadcastTopicMessage.FromSampleListDictionary(sampleListsByKey),
            _ => BroadcastTopicMessage.FromPayload(JsonSerializer.Serialize(payload))
        };
    }

    public async Task ShutdownWorkerIfUnusedAsync(ExternalWorkerProcess worker, CancellationToken cancellationToken)
    {
        if (!_workers.TryRemove(worker.PackageId, out _))
            return;
        await worker.ShutdownProcessAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        DisposeBridgeSubscriptions();
        foreach (var worker in _workers.Values)
        {
            try { await worker.ShutdownProcessAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { worker.KillProcess(); }
        }
        _workers.Clear();
        _nodeOwners.Clear();
        _subscriptions.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        DisposeBridgeSubscriptions();
        foreach (var worker in _workers.Values)
            worker.KillProcess();
        _workers.Clear();
        _nodeOwners.Clear();
        _subscriptions.Clear();
    }

    private void DisposeBridgeSubscriptions()
    {
        lock (_bridgeGate)
        {
            foreach (var subscription in _bridgeSubscriptions.Values)
                subscription.Dispose();
            _bridgeSubscriptions.Clear();
        }
    }

    private sealed record BroadcastSource(int NodeInstanceId, string? RunId, string BroadcastId);
}

