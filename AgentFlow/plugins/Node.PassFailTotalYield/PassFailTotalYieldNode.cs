// -----------------------------------------------------------------------
// <copyright company="Your Company" file="PassFailTotalYieldNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Contracts;
using BroadcastKit;
using BroadcastKit.Contracts;
using AgentFlow.Shared.FlashForgeHmi.Broadcasts;
using Microsoft.Extensions.Logging;

namespace Node.PassFailTotalYield;

/// <summary>
/// Maintains per-slot Pass/Fail counters in Yield.ini, computes slot and aggregate yields,
/// and broadcasts the latest snapshot through TopicBus.
/// </summary>
public sealed class PassFailTotalYieldNode : BaseNode
{
    private const string ResetCommand = "Reset";

    private readonly BasePin _slotAInputPin =
        new("input_string_slotA", DataType.STRING, PinDirection.Input, required: false);
    private readonly BasePin _slotBInputPin =
        new("input_string_slotB", DataType.STRING, PinDirection.Input, required: false);
    private readonly BasePin _slotCInputPin =
        new("input_string_slotC", DataType.STRING, PinDirection.Input, required: false);
    private readonly BasePin _slotDInputPin =
        new("input_string_slotD", DataType.STRING, PinDirection.Input, required: false);
    private readonly BasePin _slotFInputPin =
        new("input_string_slotF", DataType.STRING, PinDirection.Input, required: false);

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
        _logger = null;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (!IsKnownInputPin(pin))
        {
            _logger?.LogWarning(
                "PassFailTotalYield ignored a sample from an unknown pin {Pin}.",
                pin.Name);
            return;
        }

        if (value is not StringSample { StringValue: { } rawValue })
        {
            _logger?.LogWarning(
                "PassFailTotalYield ignored invalid input on {Pin}: expected StringSample with non-null StringValue.",
                pin.Name);
            return;
        }

        var text = rawValue.Trim();
        if (text.Length == 0)
        {
            _logger?.LogWarning(
                "PassFailTotalYield ignored empty input on {Pin}.",
                pin.Name);
            return;
        }

        try
        {
            if (IsResetCommand(text))
            {
                ResetAll(pin);
                return;
            }

            if (!TryParseDelta(text, out var delta, out var error))
            {
                _logger?.LogWarning(
                    "PassFailTotalYield ignored invalid JSON on {Pin}: {Error} Input={Input}.",
                    pin.Name,
                    error,
                    text);
                return;
            }

            UpdateSlot(pin, delta);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "PassFailTotalYield failed to process input on {Pin}: {Message}",
                pin.Name,
                ex.Message);
        }
    }

    private void UpdateSlot(BasePin pin, YieldInput input)
    {
        YieldSnapshot snapshot;
        lock (_stateGate)
        {
            var data = CreateData();
            data = ApplyDelta(pin, input, data);
            YieldSettingsStore.Save(data, _logger);
            ApplyData(data);
            snapshot = CreateSnapshot();
            _logger?.LogTrace(
                "PassFailTotalYield updated {Slot} with {Kind} count {Count}.",
                GetSlotName(pin),
                input.Kind,
                input.Count);
            _ = BroadcastSnapshotAsync(snapshot);
        }
    }

    private void ResetAll(BasePin pin)
    {
        YieldSnapshot snapshot;
        lock (_stateGate)
        {
            var data = YieldFileData.Empty;
            YieldSettingsStore.Save(data, _logger, resetFormat: true);
            ApplyData(data);
            snapshot = CreateSnapshot();
            _logger?.LogInformation(
                "PassFailTotalYield reset all counters and Yield.ini after input on {Pin}.",
                pin.Name);
            _ = BroadcastSnapshotAsync(snapshot);
        }
    }

    private YieldFileData ApplyDelta(
        BasePin pin,
        YieldInput input,
        YieldFileData data)
    {
        if (ReferenceEquals(pin, _slotAInputPin))
        {
            return input.Kind == YieldResultKind.Pass
                ? data with { SlotAPass = data.SlotAPass + input.Count }
                : data with { SlotAFail = data.SlotAFail + input.Count };
        }

        if (ReferenceEquals(pin, _slotBInputPin))
        {
            return input.Kind == YieldResultKind.Pass
                ? data with { SlotBPass = data.SlotBPass + input.Count }
                : data with { SlotBFail = data.SlotBFail + input.Count };
        }

        if (ReferenceEquals(pin, _slotCInputPin))
        {
            return input.Kind == YieldResultKind.Pass
                ? data with { SlotCPass = data.SlotCPass + input.Count }
                : data with { SlotCFail = data.SlotCFail + input.Count };
        }

        if (ReferenceEquals(pin, _slotDInputPin))
        {
            return input.Kind == YieldResultKind.Pass
                ? data with { SlotDPass = data.SlotDPass + input.Count }
                : data with { SlotDFail = data.SlotDFail + input.Count };
        }

        if (ReferenceEquals(pin, _slotFInputPin))
        {
            return input.Kind == YieldResultKind.Pass
                ? data with { SlotFPass = data.SlotFPass + input.Count }
                : data with { SlotFFail = data.SlotFFail + input.Count };
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

    private static bool IsResetCommand(string text) =>
        string.Equals(text, ResetCommand, StringComparison.Ordinal) ||
        string.Equals(text, "\"Reset\"", StringComparison.Ordinal);

    private static bool TryParseDelta(
        string json,
        out YieldInput input,
        out string error)
    {
        input = default!;
        error = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The JSON root must be an object.";
                return false;
            }

            var hasPass = root.TryGetProperty("Pass", out var passValue);
            var hasFail = root.TryGetProperty("Fail", out var failValue);
            if (hasPass == hasFail)
            {
                error = "Exactly one of 'Pass' or 'Fail' is required.";
                return false;
            }

            var kind = hasPass ? YieldResultKind.Pass : YieldResultKind.Fail;
            var value = hasPass ? passValue : failValue;
            if (value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt64(out var count) ||
                count <= 0)
            {
                error = "The count must be a positive 64-bit integer.";
                return false;
            }

            input = new YieldInput(kind, count);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
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
            "input_string_slotA" => "A",
            "input_string_slotB" => "B",
            "input_string_slotC" => "C",
            "input_string_slotD" => "D",
            "input_string_slotF" => "F",
            _ => pin.Name,
        };

    private enum YieldResultKind
    {
        Pass,
        Fail,
    }

    private sealed record YieldInput(YieldResultKind Kind, long Count);

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