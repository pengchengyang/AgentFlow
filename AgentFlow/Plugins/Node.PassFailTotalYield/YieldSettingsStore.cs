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
/// Owns persistence for the fixed Yield.ini file. Slot pass/fail counts are the source of
/// truth; aggregate pass/fail/total/yield values are always derived when the file is written.
/// </summary>
internal static class YieldSettingsStore
{
    public const string SettingsDirectory = @"C:\AC710\UserSettings";
    public const string SettingsFileName = "Yield.ini";
    public static readonly string SettingsFilePath =
        Path.Combine(SettingsDirectory, SettingsFileName);

    public static YieldFileData EnsureAndLoad(ILogger? logger)
    {
        Directory.CreateDirectory(SettingsDirectory);

        if (!File.Exists(SettingsFilePath))
        {
            Save(YieldFileData.Empty, logger, resetFormat: true);
            logger?.LogInformation(
                "Created missing Yield settings file at {FilePath} with zeroed counters.",
                SettingsFilePath);
            return YieldFileData.Empty;
        }

        var sections = ReadSections(SettingsFilePath);
        var data = new YieldFileData(
            ReadCount(sections, "A_P", logger),
            ReadCount(sections, "A_F", logger),
            ReadCount(sections, "B_P", logger),
            ReadCount(sections, "B_F", logger),
            ReadCount(sections, "C_P", logger),
            ReadCount(sections, "C_F", logger),
            ReadCount(sections, "D_P", logger),
            ReadCount(sections, "D_F", logger),
            ReadCount(sections, "F_P", logger),
            ReadCount(sections, "F_F", logger));

        ValidateAggregateValues(sections, data, logger);
        logger?.LogInformation(
            "Loaded Yield settings from {FilePath}: A={AP}/{AF}, B={BP}/{BF}, C={CP}/{CF}, D={DP}/{DF}, F={FP}/{FF}.",
            SettingsFilePath,
            data.SlotAPass,
            data.SlotAFail,
            data.SlotBPass,
            data.SlotBFail,
            data.SlotCPass,
            data.SlotCFail,
            data.SlotDPass,
            data.SlotDFail,
            data.SlotFPass,
            data.SlotFFail);

        return data;
    }

    public static void Save(
        YieldFileData data,
        ILogger? logger,
        bool resetFormat = false)
    {
        Directory.CreateDirectory(SettingsDirectory);

        var passTotal = GetPassTotal(data);
        var failTotal = GetFailTotal(data);
        var total = passTotal + failTotal;
        var yield = CalculateYield(passTotal, failTotal);
        var yieldText = resetFormat
            ? "0.00"
            : yield.ToString("0.0", CultureInfo.InvariantCulture);

        var content = string.Join(Environment.NewLine, new[]
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
            "[A_P]",
            data.SlotAPass.ToString(CultureInfo.InvariantCulture),
            "[A_F]",
            data.SlotAFail.ToString(CultureInfo.InvariantCulture),
            string.Empty,
            "[B_P]",
            data.SlotBPass.ToString(CultureInfo.InvariantCulture),
            "[B_F]",
            data.SlotBFail.ToString(CultureInfo.InvariantCulture),
            string.Empty,
            "[C_P]",
            data.SlotCPass.ToString(CultureInfo.InvariantCulture),
            "[C_F]",
            data.SlotCFail.ToString(CultureInfo.InvariantCulture),
            string.Empty,
            "[D_P]",
            data.SlotDPass.ToString(CultureInfo.InvariantCulture),
            "[D_F]",
            data.SlotDFail.ToString(CultureInfo.InvariantCulture),
            string.Empty,
            "[F_P]",
            data.SlotFPass.ToString(CultureInfo.InvariantCulture),
            "[F_F]",
            data.SlotFFail.ToString(CultureInfo.InvariantCulture),
            string.Empty,
        });

        var tempPath = SettingsFilePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, SettingsFilePath, overwrite: true);
            logger?.LogTrace(
                "Saved Yield settings to {FilePath}: P={Pass}, F={Fail}, T={Total}, Y={Yield}.",
                SettingsFilePath,
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

    public static long GetPassTotal(YieldFileData data) =>
        data.SlotAPass + data.SlotBPass + data.SlotCPass + data.SlotDPass + data.SlotFPass;

    public static long GetFailTotal(YieldFileData data) =>
        data.SlotAFail + data.SlotBFail + data.SlotCFail + data.SlotDFail + data.SlotFFail;

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
                "Yield settings section [{Section}] is missing; defaulting to zero.",
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
            "Yield settings section [{Section}] contains invalid count {Value}; defaulting to zero.",
            section,
            text);
        return 0;
    }

    private static void ValidateAggregateValues(
        IReadOnlyDictionary<string, string> sections,
        YieldFileData data,
        ILogger? logger)
    {
        var expectedPass = GetPassTotal(data);
        var expectedFail = GetFailTotal(data);
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
                    "Yield settings aggregate section [{Section}] is missing; it will be derived on the next save.",
                    section);
                return;
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning(
                    "Yield settings aggregate [{Section}] contains {Actual}, but slot values derive {Expected}; slot values take precedence.",
                    section,
                    actual,
                    expected);
            }
        }
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
                "Could not delete temporary Yield settings file {Path}.",
                path);
        }
    }
}

/// <summary>Slot pass/fail counters loaded from or persisted to Yield.ini.</summary>
internal sealed record YieldFileData(
    long SlotAPass,
    long SlotAFail,
    long SlotBPass,
    long SlotBFail,
    long SlotCPass,
    long SlotCFail,
    long SlotDPass,
    long SlotDFail,
    long SlotFPass,
    long SlotFFail)
{
    public static YieldFileData Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
