// -----------------------------------------------------------------------
// <copyright company="Your Company" file="PassFailTotalYieldNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using AgentFlow.Shared.FlashForgeHmi.Broadcasts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.PassFailTotalYield;

/// <summary>
/// Maintains per-slot Pass/Fail counters in Yield.ini, computes slot and aggregate yields,
/// and broadcasts the latest snapshot through TopicBus.
/// </summary>
public sealed class PassFailTotalYieldNode : BaseNode
{
    private readonly BasePin _slotAInputPin =
        new("input_int64_retA", DataType.INT64, PinDirection.Input, required: false);
    private readonly BasePin _slotBInputPin =
        new("input_int64_retB", DataType.INT64, PinDirection.Input, required: false);
    private readonly BasePin _slotCInputPin =
        new("input_int64_retC", DataType.INT64, PinDirection.Input, required: false);
    private readonly BasePin _slotDInputPin =
        new("input_int64_retD", DataType.INT64, PinDirection.Input, required: false);
    private readonly BasePin _slotFInputPin =
        new("input_int64_retF", DataType.INT64, PinDirection.Input, required: false);

    private readonly object _stateGate = new();

    private long _slotAPass;
    private long _slotAFail;
    private long _slotBPass;
    private long _slotBFail;
    private long _slotCPass;
    private long _slotCFail;
    private long _slotDPass;
    private long _slotDFail;
    private long _slotFPass;
    private long _slotFFail;

    private double _slotAYieldPercent;
    private double _slotBYieldPercent;
    private double _slotCYieldPercent;
    private double _slotDYieldPercent;
    private double _slotFYieldPercent;

    private ILogger? _logger;
    private ITopicSubscription? _resetSubscription;

    public PassFailTotalYieldNode()
    {
        Uuid = "F7AC4369-7966-4FCA-8C12-D705F235AD78";
        AddParam();
        AddPins();
    }

    public override string Name => "Pass Fail Total Yield";
    public override string DisplayName { get; set; } = "Pass Fail Total Yield";
    public override string Category => "Data";

    public override void AddPins()
    {
        AddInputPin(_slotAInputPin);
        AddInputPin(_slotBInputPin);
        AddInputPin(_slotCInputPin);
        AddInputPin(_slotDInputPin);
        AddInputPin(_slotFInputPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        // This node has no configurable parameters.
    }

    public override async Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        var data = YieldSettingsStore.EnsureAndLoad(_logger);

        YieldSnapshot snapshot;
        lock (_stateGate)
        {
            ApplyData(data);
            snapshot = CreateSnapshot();
        }

        await BroadcastSnapshotAsync(snapshot).ConfigureAwait(false);

        EnsureResetSubscription();

        _logger?.LogTrace(
            "PassFailTotalYield initialized with snapshot {Snapshot}.",
            snapshot);
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        Running = true;
        _logger?.LogTrace("PassFailTotalYield is running.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger?.LogTrace("PassFailTotalYield stopped.");
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
    {
        _resetSubscription?.Dispose();
        _resetSubscription = null;
        _logger = null;
        return Task.CompletedTask;
    }

    private void EnsureResetSubscription()
    {
        if (_resetSubscription is { IsActive: true })
            return;

        _resetSubscription?.Dispose();
        _resetSubscription = TopicBus.Instance.Subscribe(
            PassFailTotalYieldBroadcasts.Reset,
            OnResetBroadcast);
        _logger?.LogTrace(
            "PassFailTotalYield subscribed to reset broadcasts on {Topic}.",
            PassFailTotalYieldBroadcasts.Reset);
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (!IsKnownInputPin(pin))
        {
            _logger?.LogWarning(
                "PassFailTotalYield ignored a sample from an unknown pin {Pin}.",
                pin.Name);
            return;
        }

        if (value is not Int64Sample { Int64Value: { } ret })
        {
            _logger?.LogWarning(
                "PassFailTotalYield ignored invalid input on {Pin}: expected Int64Sample with non-null Int64Value, got {Type}.",
                pin.Name,
                value?.Type.ToString() ?? "null");
            return;
        }

        try
        {
            UpdateSlot(pin, ret);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "PassFailTotalYield failed to process RET {Ret} on {Pin}: {Message}",
                ret,
                pin.Name,
                ex.Message);
        }
    }

    private void UpdateSlot(BasePin pin, long ret)
    {
        var kind = ret == 0 ? YieldResultKind.Pass : YieldResultKind.Fail;
        YieldSnapshot snapshot;
        lock (_stateGate)
        {
            var data = CreateData();
            data = ApplyDelta(pin, kind, data);
            YieldSettingsStore.Save(data, _logger);
            ApplyData(data);
            snapshot = CreateSnapshot();
            _logger?.LogTrace(
                "PassFailTotalYield recorded RET {Ret} for {Slot} as {Kind}.",
                ret,
                GetSlotName(pin),
                kind);
            _ = BroadcastSnapshotAsync(snapshot);
        }
    }

    private void ResetSlots(IReadOnlySet<char> slots, string trigger)
    {
        YieldSnapshot snapshot;
        lock (_stateGate)
        {
            var data = CreateData();
            foreach (var slot in slots)
                data = ResetSlot(data, slot);

            YieldSettingsStore.Save(data, _logger, resetFormat: slots.Count == 5);
            ApplyData(data);
            snapshot = CreateSnapshot();
            _logger?.LogInformation(
                "PassFailTotalYield reset slots {Slots} and updated Yield.ini after {Trigger}.",
                FormatSlots(slots),
                trigger);
            _ = BroadcastSnapshotAsync(snapshot);
        }
    }

    private Task OnResetBroadcast(TopicMessage message)
    {
        try
        {
            if (!TryParseResetSlots(message.Payload, out var slots, out var error))
            {
                _logger?.LogWarning(
                    "PassFailTotalYield ignored reset broadcast on {Topic}: {Error}",
                    message.Topic,
                    error);
                return Task.CompletedTask;
            }

            _logger?.LogInformation(
                "PassFailTotalYield received reset command on {Topic} for slots {Slots}.",
                message.Topic,
                FormatSlots(slots));
            ResetSlots(slots, $"broadcast {message.Topic}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "PassFailTotalYield failed to process reset broadcast on {Topic}: {Message}",
                message.Topic,
                ex.Message);
        }

        return Task.CompletedTask;
    }

    private static bool TryParseResetSlots(
        object? payload,
        out IReadOnlySet<char> slots,
        out string error)
    {
        slots = new HashSet<char>();

        if (payload is not BroadcastTopicMessage { SamplesByKey: { } samplesByKey } ||
            samplesByKey.Count == 0)
        {
            error = "payload must contain a non-empty SamplesByKey dictionary";
            return false;
        }

        var requestedSlots = new HashSet<char>();
        foreach (var pair in samplesByKey)
        {
            var key = pair.Key.Trim().ToUpperInvariant();
            if (key.Length != 1 || !IsSupportedResetSlot(key[0]))
            {
                error = $"unsupported slot key '{pair.Key}'";
                return false;
            }

            if (!requestedSlots.Add(key[0]))
            {
                error = $"duplicate normalized slot key '{key}'";
                return false;
            }

            if (pair.Value is not StringSample value ||
                !string.Equals(value.StringValue, "Reset", StringComparison.Ordinal))
            {
                error = $"slot '{key}' must contain StringSample value 'Reset'";
                return false;
            }
        }

        slots = requestedSlots;
        error = string.Empty;
        return true;
    }

    private static YieldFileData ResetSlot(YieldFileData data, char slot) =>
        slot switch
        {
            'A' => data with { SlotAPass = 0, SlotAFail = 0 },
            'B' => data with { SlotBPass = 0, SlotBFail = 0 },
            'C' => data with { SlotCPass = 0, SlotCFail = 0 },
            'D' => data with { SlotDPass = 0, SlotDFail = 0 },
            'F' => data with { SlotFPass = 0, SlotFFail = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unsupported reset slot."),
        };

    private static bool IsSupportedResetSlot(char slot) =>
        slot is 'A' or 'B' or 'C' or 'D' or 'F';

    private static string FormatSlots(IReadOnlySet<char> slots) =>
        string.Join(",", slots.OrderBy(static slot => slot));

    private YieldFileData ApplyDelta(
        BasePin pin,
        YieldResultKind kind,
        YieldFileData data)
    {
        if (ReferenceEquals(pin, _slotAInputPin))
        {
            return kind == YieldResultKind.Pass
                ? data with { SlotAPass = data.SlotAPass + 1 }
                : data with { SlotAFail = data.SlotAFail + 1 };
        }

        if (ReferenceEquals(pin, _slotBInputPin))
        {
            return kind == YieldResultKind.Pass
                ? data with { SlotBPass = data.SlotBPass + 1 }
                : data with { SlotBFail = data.SlotBFail + 1 };
        }

        if (ReferenceEquals(pin, _slotCInputPin))
        {
            return kind == YieldResultKind.Pass
                ? data with { SlotCPass = data.SlotCPass + 1 }
                : data with { SlotCFail = data.SlotCFail + 1 };
        }

        if (ReferenceEquals(pin, _slotDInputPin))
        {
            return kind == YieldResultKind.Pass
                ? data with { SlotDPass = data.SlotDPass + 1 }
                : data with { SlotDFail = data.SlotDFail + 1 };
        }

        if (ReferenceEquals(pin, _slotFInputPin))
        {
            return kind == YieldResultKind.Pass
                ? data with { SlotFPass = data.SlotFPass + 1 }
                : data with { SlotFFail = data.SlotFFail + 1 };
        }

        throw new InvalidOperationException($"Unsupported input pin '{pin.Name}'.");
    }

    private YieldFileData CreateData() =>
        new(
            _slotAPass,
            _slotAFail,
            _slotBPass,
            _slotBFail,
            _slotCPass,
            _slotCFail,
            _slotDPass,
            _slotDFail,
            _slotFPass,
            _slotFFail);

    private void ApplyData(YieldFileData data)
    {
        _slotAPass = data.SlotAPass;
        _slotAFail = data.SlotAFail;
        _slotBPass = data.SlotBPass;
        _slotBFail = data.SlotBFail;
        _slotCPass = data.SlotCPass;
        _slotCFail = data.SlotCFail;
        _slotDPass = data.SlotDPass;
        _slotDFail = data.SlotDFail;
        _slotFPass = data.SlotFPass;
        _slotFFail = data.SlotFFail;

        _slotAYieldPercent = YieldSettingsStore.CalculateYield(_slotAPass, _slotAFail);
        _slotBYieldPercent = YieldSettingsStore.CalculateYield(_slotBPass, _slotBFail);
        _slotCYieldPercent = YieldSettingsStore.CalculateYield(_slotCPass, _slotCFail);
        _slotDYieldPercent = YieldSettingsStore.CalculateYield(_slotDPass, _slotDFail);
        _slotFYieldPercent = YieldSettingsStore.CalculateYield(_slotFPass, _slotFFail);
    }

    private YieldSnapshot CreateSnapshot()
    {
        var passTotal = _slotAPass + _slotBPass + _slotCPass + _slotDPass + _slotFPass;
        var failTotal = _slotAFail + _slotBFail + _slotCFail + _slotDFail + _slotFFail;
        var total = passTotal + failTotal;
        var aggregateYield = YieldSettingsStore.CalculateYield(passTotal, failTotal);

        return new YieldSnapshot(
            _slotAPass,
            _slotAFail,
            _slotAYieldPercent,
            _slotBPass,
            _slotBFail,
            _slotBYieldPercent,
            _slotCPass,
            _slotCFail,
            _slotCYieldPercent,
            _slotDPass,
            _slotDFail,
            _slotDYieldPercent,
            _slotFPass,
            _slotFFail,
            _slotFYieldPercent,
            passTotal,
            failTotal,
            total,
            aggregateYield);
    }

    private async Task BroadcastSnapshotAsync(YieldSnapshot snapshot)
    {
        var samplesByKey = new Dictionary<string, Sample>(StringComparer.Ordinal)
        {
            ["slotA_Pass"] = CreateInt64Sample("slotA_Pass", snapshot.SlotAPass),
            ["slotA_Fail"] = CreateInt64Sample("slotA_Fail", snapshot.SlotAFail),
            ["slotA_Yield"] = CreateDoubleSample("slotA_Yield", snapshot.SlotAYield),
            ["slotB_Pass"] = CreateInt64Sample("slotB_Pass", snapshot.SlotBPass),
            ["slotB_Fail"] = CreateInt64Sample("slotB_Fail", snapshot.SlotBFail),
            ["slotB_Yield"] = CreateDoubleSample("slotB_Yield", snapshot.SlotBYield),
            ["slotC_Pass"] = CreateInt64Sample("slotC_Pass", snapshot.SlotCPass),
            ["slotC_Fail"] = CreateInt64Sample("slotC_Fail", snapshot.SlotCFail),
            ["slotC_Yield"] = CreateDoubleSample("slotC_Yield", snapshot.SlotCYield),
            ["slotD_Pass"] = CreateInt64Sample("slotD_Pass", snapshot.SlotDPass),
            ["slotD_Fail"] = CreateInt64Sample("slotD_Fail", snapshot.SlotDFail),
            ["slotD_Yield"] = CreateDoubleSample("slotD_Yield", snapshot.SlotDYield),
            ["slotF_Pass"] = CreateInt64Sample("slotF_Pass", snapshot.SlotFPass),
            ["slotF_Fail"] = CreateInt64Sample("slotF_Fail", snapshot.SlotFFail),
            ["slotF_Yield"] = CreateDoubleSample("slotF_Yield", snapshot.SlotFYield),
            ["P"] = CreateInt64Sample("P", snapshot.PassTotal),
            ["F"] = CreateInt64Sample("F", snapshot.FailTotal),
            ["T"] = CreateInt64Sample("T", snapshot.Total),
            ["Y"] = CreateDoubleSample("Y", snapshot.Yield),
        };

        try
        {
            await TopicBus.Instance.PublishAsync(
                FlashForgeHmiBroadcastSubscriptions.PassFailTotalYield,
                BroadcastTopicMessage.FromSampleDictionary(samplesByKey),
                ct: CancellationToken.None).ConfigureAwait(false);

            _logger?.LogTrace(
                "PassFailTotalYield broadcast {Count} values on {Topic}: P={Pass}, F={Fail}, T={Total}, Y={Yield}.",
                samplesByKey.Count,
                FlashForgeHmiBroadcastSubscriptions.PassFailTotalYield,
                snapshot.PassTotal,
                snapshot.FailTotal,
                snapshot.Total,
                snapshot.Yield);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "PassFailTotalYield failed to broadcast snapshot on {Topic}: {Message}",
                FlashForgeHmiBroadcastSubscriptions.PassFailTotalYield,
                ex.Message);
        }
    }

    private static Int64Sample CreateInt64Sample(string key, long value)
    {
        var sample = Int64Sample.CreateSample();
        sample.SetValue(value, key);
        return sample;
    }

    private static DoubleSample CreateDoubleSample(string key, double value)
    {
        var sample = DoubleSample.CreateSample();
        sample.SetValue(value, key);
        return sample;
    }

    private bool IsKnownInputPin(BasePin pin) =>
        ReferenceEquals(pin, _slotAInputPin) ||
        ReferenceEquals(pin, _slotBInputPin) ||
        ReferenceEquals(pin, _slotCInputPin) ||
        ReferenceEquals(pin, _slotDInputPin) ||
        ReferenceEquals(pin, _slotFInputPin);

    private static string GetSlotName(BasePin pin) =>
        pin.Name switch
        {
            "input_int64_retA" => "A",
            "input_int64_retB" => "B",
            "input_int64_retC" => "C",
            "input_int64_retD" => "D",
            "input_int64_retF" => "F",
            _ => pin.Name,
        };

    private enum YieldResultKind
    {
        Pass,
        Fail,
    }

    private sealed record YieldSnapshot(
        long SlotAPass,
        long SlotAFail,
        double SlotAYield,
        long SlotBPass,
        long SlotBFail,
        double SlotBYield,
        long SlotCPass,
        long SlotCFail,
        double SlotCYield,
        long SlotDPass,
        long SlotDFail,
        double SlotDYield,
        long SlotFPass,
        long SlotFFail,
        double SlotFYield,
        long PassTotal,
        long FailTotal,
        long Total,
        double Yield);
}