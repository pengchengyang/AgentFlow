// -----------------------------------------------------------------------
// <copyright company="Your Company" file="YieldProfile.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Node.PassFailTotalYield;

internal enum YieldMode
{
    Cfg,
    Cfgs,
}

internal sealed record YieldSlotDefinition(
    int SlotNumber,
    string SectionKey,
    string BroadcastKey,
    string InputPinName);

internal sealed record YieldCounter(long Pass, long Fail);

internal sealed record YieldFileData(IReadOnlyDictionary<int, YieldCounter> Slots)
{
    public static YieldFileData Empty(YieldProfile profile)
    {
        var slots = profile.Slots.ToDictionary(
            static slot => slot.SlotNumber,
            static _ => new YieldCounter(0, 0));
        return new YieldFileData(slots);
    }

    public YieldCounter GetCounter(int slotNumber) =>
        Slots.TryGetValue(slotNumber, out var counter)
            ? counter
            : new YieldCounter(0, 0);

    public YieldFileData WithCounter(int slotNumber, YieldCounter counter)
    {
        var slots = new Dictionary<int, YieldCounter>(Slots)
        {
            [slotNumber] = counter,
        };
        return new YieldFileData(slots);
    }
}

internal sealed record YieldProfile(
    YieldMode Mode,
    string FilePath,
    IReadOnlyList<YieldSlotDefinition> Slots)
{
    public const string CfgMode = "CFG";
    public const string CfgsMode = "CFGS";

    public static YieldProfile Cfg { get; } = new(
        YieldMode.Cfg,
        @"C:\RTSResources\UserSettingsCFG\YieldCFG.ini",
        new[]
        {
            new YieldSlotDefinition(1, "A", "A", "input_int64_retA"),
            new YieldSlotDefinition(2, "B", "B", "input_int64_retB"),
            new YieldSlotDefinition(3, "C", "C", "input_int64_retC"),
            new YieldSlotDefinition(4, "D", "D", "input_int64_retD"),
            new YieldSlotDefinition(5, "F", "F", "input_int64_retF"),
        });

    public static YieldProfile Cfgs { get; } = new(
        YieldMode.Cfgs,
        @"C:\RTSResources\UserSettingsCFGS\YieldCFGS.ini",
        Enumerable.Range(1, 10)
            .Select(static slot => new YieldSlotDefinition(
                slot,
                slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"input_int64_ret{slot.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
            .ToArray());

    public string ModeName => Mode == YieldMode.Cfg ? CfgMode : CfgsMode;

    public static YieldProfile FromMode(string? mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode)
            ? CfgMode
            : mode.Trim().ToUpperInvariant();

        return normalized switch
        {
            CfgMode => Cfg,
            CfgsMode => Cfgs,
            _ => throw new ArgumentException(
                $"Unsupported Yield mode '{mode}'. Expected '{CfgMode}' or '{CfgsMode}'.",
                nameof(mode)),
        };
    }

    public bool TryResolveInputPin(string pinName, out int slotNumber)
    {
        foreach (var slot in Slots)
        {
            if (string.Equals(slot.InputPinName, pinName, StringComparison.Ordinal))
            {
                slotNumber = slot.SlotNumber;
                return true;
            }
        }

        slotNumber = 0;
        return false;
    }

    public bool TryResolveResetSlot(string key, out int slotNumber)
    {
        var normalized = key.Trim();
        if (Mode == YieldMode.Cfg)
            normalized = normalized.ToUpperInvariant();

        foreach (var slot in Slots)
        {
            if (string.Equals(slot.SectionKey, normalized, StringComparison.Ordinal))
            {
                slotNumber = slot.SlotNumber;
                return true;
            }
        }

        slotNumber = 0;
        return false;
    }

    public string FormatSlot(int slotNumber) =>
        Slots.FirstOrDefault(slot => slot.SlotNumber == slotNumber)?.SectionKey ??
        slotNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
}