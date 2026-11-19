// -----------------------------------------------------------------------
// <copyright company="Your Company" file="PassFailTotalYieldNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using AgentFlow.Shared.RTS.CFG.Broadcasts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.PassFailTotalYield;

/// <summary>
/// Maintains per-slot Pass/Fail counters in the configured Yield.ini profile, computes slot
/// and aggregate yields, and broadcasts the latest snapshot through TopicBus.
/// </summary>
public sealed class PassFailTotalYieldNode : BaseNode
{
    private const string ModeParameterName = "Mode";
    private const string FilePathParameterName = "YieldFilePath";
    private static readonly string[] ModeOptions = { YieldProfile.CfgMode, YieldProfile.CfgsMode };

    private readonly BasePin[] _cfgInputPins = CreateInputPins("A", "B", "C", "D", "F");
    private readonly BasePin[] _cfgsInputPins = CreateInputPins("1", "2", "3", "4", "5", "6", "7", "8", "9", "10");

    private readonly object _stateGate = new();

    private YieldProfile _profile = YieldProfile.Cfg;
    private YieldFileData _data = YieldFileData.Empty(YieldProfile.Cfg);

    private ILogger? _logger;
    private ITopicBus? _topicBus;
    private ITopicSubscription? _resetSubscription;

    public PassFailTotalYieldNode()
    {
        Uuid = "F7AC4369-7966-4FCA-8C12-D705F235AD78";
        AddParam();
        AddPins();
    }

    public override string Name => "Pass Fail Total Yield";
    public override string DisplayName { get; set; } = "Pass Fail Total Yield";
    public override string Category => "C";

    public override void AddPins()
    {
        foreach (var pin in _cfgInputPins)
            AddInputPin(pin);
        foreach (var pin in _cfgsInputPins)
            AddInputPin(pin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            ModeParameterName,
            typeof(string),
            YieldProfile.CfgMode,
            isEditable: true,
            group: "General",
            zone: "PassFailTotalYieldZone",
            editor: ParameterEditorKind.SingleSelect,
            options: ModeOptions));
        AddParameter(new NodeParameter(
            FilePathParameterName,
            typeof(string),
            string.Empty,
            isEditable: true,
            group: "General",
            zone: "PassFailTotalYieldZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        var mode = parameters.TryGetValue(ModeParameterName, out var modeValue)
            ? ParameterValues.GetString(modeValue, YieldProfile.CfgMode) ?? YieldProfile.CfgMode
            : Param(ModeParameterName).Value?.ToString();

        var modeProfile = YieldProfile.FromMode(mode);
        var filePath = parameters.TryGetValue(FilePathParameterName, out var pathValue)
            ? ParameterValues.GetString(pathValue, string.Empty) ?? string.Empty
            : Param(FilePathParameterName).Value?.ToString() ?? string.Empty;
        filePath = filePath.Trim();

        Param(ModeParameterName).Value = modeProfile.ModeName;
        Param(FilePathParameterName).Value = filePath;

        _profile = string.IsNullOrEmpty(filePath)
            ? modeProfile
            : modeProfile with { FilePath = filePath };
        _data = YieldFileData.Empty(_profile);
    }

    public override async Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        _topicBus = ResolveTopicBus(context);

        var data = LoadData();
        YieldSnapshot snapshot;
        lock (_stateGate)
        {
            _data = data;
            snapshot = CreateSnapshot();
        }

        await BroadcastSnapshotAsync(snapshot).ConfigureAwait(false);
        EnsureResetSubscription();

        _logger?.LogTrace(
            "PassFailTotalYield initialized mode {Mode} with file {FilePath} and snapshot {Snapshot}.",
            _profile.ModeName,
            _profile.FilePath,
            snapshot);
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        _topicBus = ResolveTopicBus(context);
        Running = true;
        _logger?.LogTrace("PassFailTotalYield is running in {Mode} mode.", _profile.ModeName);
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _resetSubscription?.Dispose();
        _resetSubscription = null;
        _topicBus = null;
        _logger?.LogTrace("PassFailTotalYield stopped.");
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
    {
        _resetSubscription?.Dispose();
        _resetSubscription = null;
        _topicBus = null;
        _logger = null;
        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        int slotNumber;
        if (_profile.TryResolveInputPin(pin.Name, out slotNumber))
        {
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
                UpdateSlot(slotNumber, ret);
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

            return;
        }

        _logger?.LogWarning(
            "PassFailTotalYield ignored input pin {Pin} because it is not valid in {Mode} mode.",
            pin.Name,
            _profile.ModeName);
    }

    private YieldFileData LoadData() =>
        _profile.Mode == YieldMode.Cfg
            ? YieldSettingsStore.EnsureAndLoad(_profile, _logger)
            : CfgsYieldSettingsStore.EnsureAndLoad(_profile, _logger);

    private void SaveData(YieldFileData data, bool resetFormat)
    {
        if (_profile.Mode == YieldMode.Cfg)
        {
            YieldSettingsStore.Save(_profile, data, _logger, resetFormat);
            return;
        }

        CfgsYieldSettingsStore.Save(_profile, data, _logger, resetFormat);
    }

    private void UpdateSlot(int slotNumber, long ret)
    {
        var kind = ret == 0 ? YieldResultKind.Pass : YieldResultKind.Fail;
        YieldSnapshot snapshot;
        lock (_stateGate)
        {
            var data = ApplyDelta(_data, slotNumber, kind);
            SaveData(data, resetFormat: false);
            _data = data;
            snapshot = CreateSnapshot();
            _logger?.LogTrace(
                "PassFailTotalYield recorded RET {Ret} for {Slot} as {Kind} in {Mode} mode.",
                ret,
                _profile.FormatSlot(slotNumber),
                kind,
                _profile.ModeName);
            _ = BroadcastSnapshotAsync(snapshot);
        }
    }

    private void ResetSlots(IReadOnlySet<int> slots, string trigger)
    {
        YieldSnapshot snapshot;
        lock (_stateGate)
        {
            var data = _data;
            foreach (var slot in slots)
            {
                data = data.WithCounter(slot, new YieldCounter(0, 0));
            }

            SaveData(data, resetFormat: slots.Count == _profile.Slots.Count);
            _data = data;
            snapshot = CreateSnapshot();
            _logger?.LogInformation(
                "PassFailTotalYield reset slots {Slots} and updated {FilePath} after {Trigger}.",
                FormatSlots(slots),
                _profile.FilePath,
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
                "PassFailTotalYield received reset command on {Topic} for slots {Slots} in {Mode} mode.",
                message.Topic,
                FormatSlots(slots),
                _profile.ModeName);
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

    private bool TryParseResetSlots(
        object? payload,
        out IReadOnlySet<int> slots,
        out string error)
    {
        slots = new HashSet<int>();

        if (payload is not BroadcastTopicMessage { SamplesByKey: { } samplesByKey } ||
            samplesByKey.Count == 0)
        {
            error = "payload must contain a non-empty SamplesByKey dictionary";
            return false;
        }

        var requestedSlots = new HashSet<int>();
        foreach (var pair in samplesByKey)
        {
            int slotNumber;
            if (!_profile.TryResolveResetSlot(pair.Key, out slotNumber))
            {
                error = $"unsupported slot key '{pair.Key}' for {_profile.ModeName} mode";
                return false;
            }

            if (!requestedSlots.Add(slotNumber))
            {
                error = $"duplicate normalized slot key '{pair.Key}'";
                return false;
            }

            if (pair.Value is not StringSample value ||
                !string.Equals(value.StringValue, "Reset", StringComparison.Ordinal))
            {
                error = $"slot '{pair.Key}' must contain StringSample value 'Reset'";
                return false;
            }
        }

        slots = requestedSlots;
        error = string.Empty;
        return true;
    }

    private static YieldFileData ApplyDelta(
        YieldFileData data,
        int slotNumber,
        YieldResultKind kind)
    {
        var counter = data.GetCounter(slotNumber);
        var updated = kind == YieldResultKind.Pass
            ? counter with { Pass = counter.Pass + 1 }
            : counter with { Fail = counter.Fail + 1 };
        return data.WithCounter(slotNumber, updated);
    }

    private YieldSnapshot CreateSnapshot()
    {
        var slots = _profile.Slots
            .Select(slot =>
            {
                var counter = _data.GetCounter(slot.SlotNumber);
                var yield = YieldSettingsStore.CalculateYield(counter.Pass, counter.Fail);
                return new YieldSlotSnapshot(
                    slot.SlotNumber,
                    slot.BroadcastKey,
                    counter.Pass,
                    counter.Fail,
                    yield);
            })
            .ToArray();

        var passTotal = slots.Sum(static slot => slot.Pass);
        var failTotal = slots.Sum(static slot => slot.Fail);
        var total = passTotal + failTotal;
        var aggregateYield = YieldSettingsStore.CalculateYield(passTotal, failTotal);

        return new YieldSnapshot(
            _profile.ModeName,
            _profile.Slots.Count,
            slots,
            passTotal,
            failTotal,
            total,
            aggregateYield);
    }

    private async Task BroadcastSnapshotAsync(YieldSnapshot snapshot)
    {
        var samplesByKey = new Dictionary<string, Sample>(StringComparer.Ordinal);
        foreach (var slot in snapshot.Slots)
        {
            var prefix = $"slot{slot.BroadcastKey}";
            samplesByKey[$"{prefix}_Pass"] = CreateInt64Sample($"{prefix}_Pass", slot.Pass);
            samplesByKey[$"{prefix}_Fail"] = CreateInt64Sample($"{prefix}_Fail", slot.Fail);
            samplesByKey[$"{prefix}_Yield"] = CreateDoubleSample($"{prefix}_Yield", slot.Yield);
        }

        samplesByKey["P"] = CreateInt64Sample("P", snapshot.PassTotal);
        samplesByKey["F"] = CreateInt64Sample("F", snapshot.FailTotal);
        samplesByKey["T"] = CreateInt64Sample("T", snapshot.Total);
        samplesByKey["Y"] = CreateDoubleSample("Y", snapshot.Yield);

        try
        {
            await (_topicBus ?? TopicBus.Instance).PublishAsync(
                CFGBroadcastSubscriptions.CFGPassFailTotalYield,
                BroadcastTopicMessage.FromSampleDictionary(samplesByKey),
                ct: CancellationToken.None).ConfigureAwait(false);

            _logger?.LogTrace(
                "PassFailTotalYield broadcast {Count} values on {Topic} in {Mode} mode: P={Pass}, F={Fail}, T={Total}, Y={Yield}.",
                samplesByKey.Count,
                CFGBroadcastSubscriptions.CFGPassFailTotalYield,
                snapshot.ModeName,
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
                CFGBroadcastSubscriptions.CFGPassFailTotalYield,
                ex.Message);
        }
    }

    private void EnsureResetSubscription()
    {
        if (_resetSubscription is { IsActive: true })
            return;

        _resetSubscription?.Dispose();
        _resetSubscription = (_topicBus ?? TopicBus.Instance).Subscribe(
            CFGPassFailTotalYieldBroadcasts.CFGReset,
            OnResetBroadcast);
        _logger?.LogTrace(
            "PassFailTotalYield subscribed to reset broadcasts on {Topic} for {Mode} mode.",
            CFGPassFailTotalYieldBroadcasts.CFGReset,
            _profile.ModeName);
    }

    private string FormatSlots(IReadOnlySet<int> slots) =>
        string.Join(
            ",",
            slots.OrderBy(static slot => slot).Select(_profile.FormatSlot));

    private static ITopicBus ResolveTopicBus(INodeContext context) =>
        context.GetService<ITopicBus>() ?? TopicBus.Instance;

    private static BasePin[] CreateInputPins(params string[] slotKeys) =>
        slotKeys
            .Select(static key => new BasePin(
                $"input_int64_ret{key}",
                DataType.INT64,
                PinDirection.Input,
                required: false))
            .ToArray();

    private NodeParameter Param(string name) => NodeParameters.First(parameter => parameter.Name == name);

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

    private enum YieldResultKind
    {
        Pass,
        Fail,
    }

    private sealed record YieldSlotSnapshot(
        int SlotNumber,
        string BroadcastKey,
        long Pass,
        long Fail,
        double Yield);

    private sealed record YieldSnapshot(
        string ModeName,
        int SlotCount,
        IReadOnlyList<YieldSlotSnapshot> Slots,
        long PassTotal,
        long FailTotal,
        long Total,
        double Yield);
}