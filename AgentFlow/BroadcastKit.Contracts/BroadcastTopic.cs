// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastTopic.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using AgentFlow.Contracts;

namespace BroadcastKit.Contracts;

/// <summary>
/// Shared topic names used by the broadcast demo. Only topics with a real publisher and
/// subscriber belong here; remove unused topics together with their demo.
/// </summary>
public static class BroadcastTopics
{
    /// <summary>GUI -> Node.Broadcast.Demo. Payload: <see cref="BroadcastTopicMessage"/>.</summary>
    public const string GuiToNode = "broadcast.demo.gui-to-node";

    /// <summary>Node.Broadcast.Demo -> GUI. Payload: <see cref="BroadcastTopicMessage"/>.</summary>
    public const string NodeToGui = "broadcast.demo.node-to-gui";
}

/// <summary>
/// Shared broadcast envelope carried inside <c>BroadcastKit.TopicMessage.Payload</c>.
/// <see cref="Payload"/>, <see cref="Sample"/>, <see cref="Samples"/>,
/// <see cref="SamplesByKey"/>, and <see cref="SampleListsByKey"/> are optional peer payloads.
/// TopicBus transports the envelope without interpreting keys, sample types, ordering, or
/// business meaning. Publishers and subscribers must define and validate those rules.
/// </summary>
public sealed record BroadcastTopicMessage
{
    public BroadcastTopicMessage(
        string? payload = null,
        Sample? sample = null,
        IReadOnlyList<Sample>? samples = null,
        IReadOnlyDictionary<string, Sample>? samplesByKey = null,
        IReadOnlyDictionary<string, IReadOnlyList<Sample>>? sampleListsByKey = null)
    {
        Payload = payload;
        Sample = sample;
        Samples = SnapshotSamples(samples);
        SamplesByKey = SnapshotSamplesByKey(samplesByKey);
        SampleListsByKey = SnapshotSampleListsByKey(sampleListsByKey);
    }

    public string? Payload { get; init; }
    public Sample? Sample { get; init; }

    /// <summary>Ordered sample batch. Consumers interpret order, keys, and types by topic contract.</summary>
    public IReadOnlyList<Sample>? Samples { get; init; }

    /// <summary>
    /// Single sample per string key. An immutable snapshot is stored so later changes to the
    /// source dictionary cannot alter the published message.
    /// </summary>
    public IReadOnlyDictionary<string, Sample>? SamplesByKey { get; init; }

    /// <summary>
    /// Ordered sample batch per string key. Each value is copied to an immutable snapshot;
    /// consumers interpret key names, batch order, and sample types by topic contract.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Sample>>? SampleListsByKey { get; init; }

    public static BroadcastTopicMessage FromPayload(string payload) => new(payload: payload);
    public static BroadcastTopicMessage FromSample(Sample sample) => new(sample: sample);

    public static BroadcastTopicMessage FromSamples(IReadOnlyList<Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return new(samples: samples);
    }

    /// <summary>Create an envelope containing one Sample per string key.</summary>
    public static BroadcastTopicMessage FromSampleDictionary(
        IReadOnlyDictionary<string, Sample> samplesByKey)
    {
        ArgumentNullException.ThrowIfNull(samplesByKey);
        return new(samplesByKey: samplesByKey);
    }

    /// <summary>Create an envelope containing an ordered Sample batch per string key.</summary>
    public static BroadcastTopicMessage FromSampleListDictionary(
        IReadOnlyDictionary<string, IReadOnlyList<Sample>> sampleListsByKey)
    {
        ArgumentNullException.ThrowIfNull(sampleListsByKey);
        return new(sampleListsByKey: sampleListsByKey);
    }

    public static BroadcastTopicMessage FromBoth(string payload, Sample sample) => new(payload, sample);

    public bool IsEmpty =>
        Payload is null &&
        Sample is null &&
        (Samples is null || Samples.Count == 0) &&
        (SamplesByKey is null || SamplesByKey.Count == 0) &&
        (SampleListsByKey is null ||
         SampleListsByKey.Count == 0 ||
         SampleListsByKey.All(pair => pair.Value.Count == 0));

    private static IReadOnlyList<Sample>? SnapshotSamples(IReadOnlyList<Sample>? samples)
        => samples is null ? null : Array.AsReadOnly(samples.ToArray());

    private static IReadOnlyDictionary<string, Sample>? SnapshotSamplesByKey(
        IReadOnlyDictionary<string, Sample>? samplesByKey)
    {
        if (samplesByKey is null)
            return null;

        var snapshot = new Dictionary<string, Sample>(StringComparer.Ordinal);
        foreach (var pair in samplesByKey)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new ArgumentException("Sample dictionary keys cannot be empty.", nameof(samplesByKey));
            if (pair.Value is null)
                throw new ArgumentException($"Sample dictionary value for key '{pair.Key}' cannot be null.", nameof(samplesByKey));

            snapshot[pair.Key] = pair.Value;
        }

        return new ReadOnlyDictionary<string, Sample>(snapshot);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<Sample>>? SnapshotSampleListsByKey(
        IReadOnlyDictionary<string, IReadOnlyList<Sample>>? sampleListsByKey)
    {
        if (sampleListsByKey is null)
            return null;

        var snapshot = new Dictionary<string, IReadOnlyList<Sample>>(StringComparer.Ordinal);
        foreach (var pair in sampleListsByKey)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new ArgumentException("Sample list dictionary keys cannot be empty.", nameof(sampleListsByKey));
            if (pair.Value is null)
                throw new ArgumentException($"Sample list for key '{pair.Key}' cannot be null.", nameof(sampleListsByKey));

            snapshot[pair.Key] = Array.AsReadOnly(pair.Value.ToArray());
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<Sample>>(snapshot);
    }
}

