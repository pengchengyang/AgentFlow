// -----------------------------------------------------------------------
// <copyright company="Your Company" file="YieldSettingsStore.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Node.PassFailTotalYield;

/// <summary>
/// Owns persistence for the CFG Yield.ini file. Slot pass/fail counts are the source of
/// truth; aggregate pass/fail/total/yield values are always derived when the file is written.
/// </summary>
internal static class YieldSettingsStore
{
    public static YieldFileData EnsureAndLoad(YieldProfile profile, ILogger? logger)
    {
        EnsureCfgProfile(profile);
        EnsureDirectory(profile.FilePath);

        if (!File.Exists(profile.FilePath))
        {
            var empty = YieldFileData.Empty(profile);
            Save(profile, empty, logger, resetFormat: true);
            logger?.LogInformation(
                "Created missing CFG Yield settings file at {FilePath} with zeroed counters.",
                profile.FilePath);
            return empty;
        }

        var sections = ReadSections(profile.FilePath);
        var data = YieldFileData.Empty(profile);
        foreach (var slot in profile.Slots)
        {
            data = data.WithCounter(
                slot.SlotNumber,
                new YieldCounter(
                    ReadCount(sections, $"{slot.SectionKey}_P", logger),
                    ReadCount(sections, $"{slot.SectionKey}_F", logger)));
        }

        ValidateAggregateValues(profile, sections, data, logger);
        logger?.LogInformation(
            "Loaded CFG Yield settings from {FilePath}: {SlotSummary}.",
            profile.FilePath,
            FormatSlotSummary(profile, data));

        return data;
    }

    public static void Save(
        YieldProfile profile,
        YieldFileData data,
        ILogger? logger,
        bool resetFormat = false)
    {
        EnsureCfgProfile(profile);
        EnsureDirectory(profile.FilePath);

        var passTotal = GetPassTotal(profile, data);
        var failTotal = GetFailTotal(profile, data);
        var total = passTotal + failTotal;
        var yield = CalculateYield(passTotal, failTotal);
        var yieldText = resetFormat
            ? "0.00"
            : yield.ToString("0.0", CultureInfo.InvariantCulture);

        var lines = new List<string>
        {
            "[P]",
            passTotal.ToString(CultureInfo.InvariantCulture),
            "[F]",
            failTotal.ToString(CultureInfo.InvariantCulture),
            "[T]",
            total.ToString(CultureInfo.InvariantCulture),
            "[Y]",
            yieldText,
            string.Empty,
        };

        foreach (var slot in profile.Slots)
        {
            var counter = data.GetCounter(slot.SlotNumber);
            lines.Add($"[{slot.SectionKey}_P]");
            lines.Add(counter.Pass.ToString(CultureInfo.InvariantCulture));
            lines.Add($"[{slot.SectionKey}_F]");
            lines.Add(counter.Fail.ToString(CultureInfo.InvariantCulture));
            lines.Add(string.Empty);
        }

        var content = string.Join(Environment.NewLine, lines);
        var tempPath = profile.FilePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, profile.FilePath, overwrite: true);
            logger?.LogTrace(
                "Saved CFG Yield settings to {FilePath}: P={Pass}, F={Fail}, T={Total}, Y={Yield}.",
                profile.FilePath,
                passTotal,
                failTotal,
                total,
                yieldText);
        }
        catch
        {
            TryDelete(tempPath, logger);
            throw;
        }
    }

    public static long GetPassTotal(YieldProfile profile, YieldFileData data) =>
        profile.Slots.Sum(slot => data.GetCounter(slot.SlotNumber).Pass);

    public static long GetFailTotal(YieldProfile profile, YieldFileData data) =>
        profile.Slots.Sum(slot => data.GetCounter(slot.SlotNumber).Fail);

    public static double CalculateYield(long pass, long fail)
    {
        var total = pass + fail;
        return total == 0
            ? 0.0
            : Math.Round(
                pass * 100.0 / total,
                1,
                MidpointRounding.AwayFromZero);
    }

    private static void EnsureCfgProfile(YieldProfile profile)
    {
        if (profile.Mode != YieldMode.Cfg)
            throw new ArgumentException("CFG Yield store requires a CFG profile.", nameof(profile));
    }

    private static Dictionary<string, string> ReadSections(string path)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed[1..^1].Trim();
                continue;
            }

            if (currentSection is not null && !sections.ContainsKey(currentSection))
                sections[currentSection] = trimmed;
        }

        return sections;
    }

    private static long ReadCount(
        IReadOnlyDictionary<string, string> sections,
        string section,
        ILogger? logger)
    {
        if (!sections.TryGetValue(section, out var text))
        {
            logger?.LogWarning(
                "CFG Yield settings section [{Section}] is missing; defaulting to zero.",
                section);
            return 0;
        }

        if (long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value) &&
            value >= 0)
        {
            return value;
        }

        logger?.LogWarning(
            "CFG Yield settings section [{Section}] contains invalid count {Value}; defaulting to zero.",
            section,
            text);
        return 0;
    }

    private static void ValidateAggregateValues(
        YieldProfile profile,
        IReadOnlyDictionary<string, string> sections,
        YieldFileData data,
        ILogger? logger)
    {
        var expectedPass = GetPassTotal(profile, data);
        var expectedFail = GetFailTotal(profile, data);
        var expectedTotal = expectedPass + expectedFail;
        var expectedYield = CalculateYield(expectedPass, expectedFail);

        ValidateAggregate("P", expectedPass.ToString(CultureInfo.InvariantCulture));
        ValidateAggregate("F", expectedFail.ToString(CultureInfo.InvariantCulture));
        ValidateAggregate("T", expectedTotal.ToString(CultureInfo.InvariantCulture));
        ValidateAggregate("Y", expectedYield.ToString("0.0", CultureInfo.InvariantCulture));

        void ValidateAggregate(string section, string expected)
        {
            if (!sections.TryGetValue(section, out var actual))
            {
                logger?.LogWarning(
                    "CFG Yield settings aggregate section [{Section}] is missing; it will be derived on the next save.",
                    section);
                return;
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning(
                    "CFG Yield settings aggregate [{Section}] contains {Actual}, but slot values derive {Expected}; slot values take precedence.",
                    section,
                    actual,
                    expected);
            }
        }
    }

    private static string FormatSlotSummary(YieldProfile profile, YieldFileData data) =>
        string.Join(
            ", ",
            profile.Slots.Select(slot =>
            {
                var counter = data.GetCounter(slot.SlotNumber);
                return $"{slot.SectionKey}={counter.Pass}/{counter.Fail}";
            }));

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static void TryDelete(string path, ILogger? logger)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Could not delete temporary CFG Yield settings file {Path}.",
                path);
        }
    }
}