// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CFGSCommandBuilder.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Node.CFGSHandler;

/// <summary>
/// Parses upper-workflow message batches and builds PsExec command lines from the flat settings
/// dictionary produced by Node.ReadIniFile. The builder does not access node pins or runtime state.
/// </summary>
internal static class CFGSCommandBuilder
{
    public const string Cfg1 = "CFG-1";
    public const string Cfg2 = "CFG-2";

    private const string RootDirectoryKey = "ROOTDIR.RTS";
    private const string PsExecRelativePath = @"RTS\RemoteCallerTool\PsExec64.exe";
    private const string RtsRelativePath = @"RTS\RTS.exe";

    private const string MessagesProperty = "MESSAGES";
    private const string SlotProperty = "SLOT";
    private const string FsnProperty = "FSN";
    private const string ImeiProperty = "IMEI";
    private const string StatusProperty = "STATUS";
    private const string IdleStatus = "IDLE";
    private const string TestStatus = "TEST";

    public static bool TryParseUpperMessages(
        string input,
        out IReadOnlyDictionary<int, CFGSUpperMessageItem> items,
        out string? error)
    {
        items = new Dictionary<int, CFGSUpperMessageItem>();
        error = null;

        var frame = input.Trim();
        if (!frame.StartsWith(';') || !frame.EndsWith(';'))
        {
            error = "upper message must begin and end with ';'";
            return false;
        }

        var payload = frame[1..^1].Trim();
        if (payload.Length == 0)
        {
            error = "upper message JSON payload is empty";
            return false;
        }

        JsonObject root;
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject parsed)
            {
                error = "upper message payload must be a JSON object";
                return false;
            }

            root = parsed;
        }
        catch (JsonException ex)
        {
            error = $"invalid upper message JSON: {ex.Message}";
            return false;
        }

        if (GetProperty(root, MessagesProperty) is not JsonArray messages)
        {
            error = "MESSAGES must be a JSON array";
            return false;
        }

        if (messages.Count != 10)
        {
            error = "MESSAGES must contain exactly ten records";
            return false;
        }

        var parsedItems = new Dictionary<int, CFGSUpperMessageItem>();
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not JsonObject message)
            {
                error = $"MESSAGES[{index}] must be a JSON object";
                return false;
            }

            if (!TryGetString(message, SlotProperty, out var slotText) ||
                !int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) ||
                slot is < 1 or > 10)
            {
                error = $"MESSAGES[{index}].SLOT must be an integer string from 1 through 10";
                return false;
            }

            if (!TryGetString(message, FsnProperty, out var fsn) ||
                !TryGetString(message, ImeiProperty, out var imei))
            {
                error = $"MESSAGES[{index}].FSN and IMEI must be strings";
                return false;
            }

            if (!TryGetString(message, StatusProperty, out var statusText))
            {
                error = $"MESSAGES[{index}].STATUS must be IDLE or TEST";
                return false;
            }

            var status = statusText.Trim().ToUpperInvariant();
            if (status is not IdleStatus and not TestStatus)
            {
                error = $"MESSAGES[{index}].STATUS must be IDLE or TEST";
                return false;
            }

            fsn = fsn.Trim();
            imei = imei.Trim();
            if (string.Equals(status, TestStatus, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(fsn) || string.IsNullOrWhiteSpace(imei)))
            {
                error = $"MESSAGES[{index}] with STATUS TEST requires both FSN and IMEI";
                return false;
            }

            if (parsedItems.ContainsKey(slot))
            {
                error = $"MESSAGES contains duplicate SLOT {slot}";
                return false;
            }

            parsedItems.Add(slot, new CFGSUpperMessageItem(slot, fsn, imei, status));
        }

        for (var slot = 1; slot <= 10; slot++)
        {
            if (!parsedItems.ContainsKey(slot))
            {
                error = $"MESSAGES must contain SLOT {slot}";
                return false;
            }
        }

        items = parsedItems;
        return true;
    }

    public static bool TryBuildGroupMessages(
        IReadOnlyDictionary<int, CFGSUpperMessageItem> items,
        string location,
        out JsonArray messages,
        out bool hasTestData,
        out string? error)
    {
        messages = new JsonArray();
        hasTestData = false;
        error = null;

        var startSlot = string.Equals(location, Cfg1, StringComparison.Ordinal)
            ? 1
            : string.Equals(location, Cfg2, StringComparison.Ordinal)
                ? 6
                : 0;
        if (startSlot == 0)
        {
            error = $"LOCATION must be {Cfg1} or {Cfg2}";
            return false;
        }

        for (var slot = startSlot; slot < startSlot + 5; slot++)
        {
            if (!items.TryGetValue(slot, out var item))
            {
                error = $"MESSAGES is missing SLOT {slot}";
                return false;
            }

            var isTest = string.Equals(item.Status, TestStatus, StringComparison.Ordinal);
            if (isTest)
                hasTestData = true;

            messages.Add(new JsonObject
            {
                [SlotProperty] = slot.ToString(CultureInfo.InvariantCulture),
                [FsnProperty] = isTest ? item.Fsn : string.Empty,
                [ImeiProperty] = isTest ? item.Imei : string.Empty,
                [StatusProperty] = isTest ? TestStatus : IdleStatus,
            });
        }

        return true;
    }

    private static JsonNode? GetProperty(JsonObject obj, string key)
    {
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private static bool TryGetString(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (GetProperty(obj, key) is not JsonValue node ||
            !node.TryGetValue<string>(out var text) ||
            text is null)
        {
            return false;
        }

        value = text;
        return true;
    }

    public static bool TryBuildStartCommands(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        out string cfg1Command,
        out string cfg2Command,
        out string? error)
    {
        cfg1Command = string.Empty;
        cfg2Command = string.Empty;
        error = null;

        if (!TryBuildStartCommand(settings, Cfg1, out cfg1Command, out error))
            return false;

        return TryBuildStartCommand(settings, Cfg2, out cfg2Command, out error);
    }

    public static bool TryBuildStartCommand(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        string location,
        out string command,
        out string? error)
    {
        command = string.Empty;
        error = null;

        if (string.Equals(location, Cfg1, StringComparison.Ordinal))
            return TryBuildLocalStartCommand(settings, out command, out error);

        if (string.Equals(location, Cfg2, StringComparison.Ordinal))
            return TryBuildRemoteStartCommand(settings, out command, out error);

        error = $"LOCATION must be {Cfg1} or {Cfg2}";
        return false;
    }

    private static bool TryBuildLocalStartCommand(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        out string command,
        out string? error)
    {
        command = string.Empty;
        error = null;

        if (!TryGetRequiredSetting(settings, RootDirectoryKey, out var rootDirectory, out error))
            return false;

        var rtsDirectory = Path.Combine(rootDirectory.Trim(), "RTS");
        var rtsPath = Path.Combine(rtsDirectory, "RTS.exe");
        command =
            $"powershell -NoProfile -Command \"$p = Start-Process -FilePath '{rtsPath}' -ArgumentList '--CFG' -WorkingDirectory '{rtsDirectory}' -PassThru; Write-Output ('PID=' + $p.Id)\"";
        return true;
    }

    private static bool TryBuildRemoteStartCommand(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        out string command,
        out string? error)
    {
        command = string.Empty;
        error = null;

        if (!TryResolvePsExecPath(settings, out var psExecPath, out error) ||
            !TryGetRequiredSetting(settings, $"IP.{Cfg2}", out var ip, out error) ||
            !TryGetRequiredSetting(settings, $"NAME.{Cfg2}", out var name, out error) ||
            !TryGetRequiredSetting(settings, $"PASSWORD.{Cfg2}", out var password, out error) ||
            !TryGetRequiredSetting(settings, RootDirectoryKey, out var rootDirectory, out error))
        {
            return false;
        }

        var rtsPath = Path.Combine(rootDirectory.Trim(), RtsRelativePath);
        command =
            $"{psExecPath} \\\\{ip} -u {name} -p {password} -h -i 2 -d \"{rtsPath}\" --CFG";
        return true;
    }

    public static bool TryBuildCfg1PidQueryCommand(
        long pid,
        out string command,
        out string? error)
    {
        command = string.Empty;
        error = null;
        if (pid <= 0)
        {
            error = $"PID must be a positive integer, got {pid.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        command = $"tasklist /FI \"PID eq {pid.ToString(CultureInfo.InvariantCulture)}\"";
        return true;
    }

    public static bool TryBuildCfg1KillProcessTreeCommand(
        long pid,
        out string command,
        out string? error)
    {
        command = string.Empty;
        error = null;
        if (pid <= 0)
        {
            error = $"PID must be a positive integer, got {pid.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        command = $"taskkill /PID {pid.ToString(CultureInfo.InvariantCulture)} /F /T";
        return true;
    }

    public static bool TryClassifyTasklistOutput(
        long pid,
        string? output,
        out PidLookupResult result,
        out string? error)
    {
        result = PidLookupResult.Unknown;
        error = null;

        if (pid <= 0)
        {
            error = $"PID must be a positive integer, got {pid.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (IsTasklistPidNotFound(output))
        {
            result = PidLookupResult.NotFound;
            return true;
        }

        if (ContainsTasklistPid(pid, output))
        {
            result = PidLookupResult.Found;
            return true;
        }

        error = $"tasklist output did not indicate whether PID {pid.ToString(CultureInfo.InvariantCulture)} exists";
        return false;
    }

    public static bool ContainsTasklistPid(long pid, string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || pid <= 0)
            return false;

        var pidText = pid.ToString(CultureInfo.InvariantCulture);
        return Regex.IsMatch(
            output,
            $@"^\s*\S+\s+{Regex.Escape(pidText)}\s+",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
    }

    public static bool IsTasklistPidNotFound(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        return output.Contains(
                   "没有运行的任务匹配指定标准",
                   StringComparison.Ordinal) ||
               output.Contains(
                   "No tasks are running which match the specified criteria",
                   StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryBuildCfg2PidQueryCommand(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        long pid,
        out string command,
        out string? error)
    {
        command = string.Empty;
        error = null;
        if (pid <= 0)
        {
            error = $"PID must be a positive integer, got {pid.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (!TryGetRemoteCommandSettings(
                settings,
                out var psExecPath,
                out var ip,
                out var name,
                out var password,
                out error))
        {
            return false;
        }

        command =
            $"{psExecPath} \\\\{ip} -u {name} -p \"{password}\" -i -h tasklist /FI \"PID eq {pid.ToString(CultureInfo.InvariantCulture)}\"";
        return true;
    }

    public static bool TryBuildCfg2KillProcessTreeCommand(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        long pid,
        out string command,
        out string? error)
    {
        command = string.Empty;
        error = null;
        if (pid <= 0)
        {
            error = $"PID must be a positive integer, got {pid.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (!TryGetRemoteCommandSettings(
                settings,
                out var psExecPath,
                out var ip,
                out var name,
                out var password,
                out error))
        {
            return false;
        }

        command =
            $"{psExecPath} \\\\{ip} -u {name} -p {password} -h -i 2 -d taskkill /PID {pid.ToString(CultureInfo.InvariantCulture)} /F /T";
        return true;
    }

    private static bool TryGetRemoteCommandSettings(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        out string psExecPath,
        out string ip,
        out string name,
        out string password,
        out string? error)
    {
        psExecPath = string.Empty;
        ip = string.Empty;
        name = string.Empty;
        password = string.Empty;
        error = null;

        if (!TryResolvePsExecPath(settings, out psExecPath, out error) ||
            !TryGetRequiredSetting(settings, $"IP.{Cfg2}", out ip, out error) ||
            !TryGetRequiredSetting(settings, $"NAME.{Cfg2}", out name, out error) ||
            !TryGetRequiredSetting(settings, $"PASSWORD.{Cfg2}", out password, out error))
        {
            return false;
        }

        return true;
    }

    public static bool TryBuildSureExitCommand(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        string location,
        long pid,
        out string command,
        out string? error)
    {
        command = string.Empty;
        error = null;

        if (!string.Equals(location, Cfg2, StringComparison.Ordinal))
        {
            error = "CFG-1 exit handling requires the PID query/kill sequence; this builder supports CFG-2 only";
            return false;
        }

        if (pid <= 0)
        {
            error = $"PID must be a positive integer, got {pid.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (!TryResolvePsExecPath(settings, out var psExecPath, out error) ||
            !TryGetRequiredSetting(settings, $"IP.{location}", out var ip, out error) ||
            !TryGetRequiredSetting(settings, $"NAME.{location}", out var name, out error) ||
            !TryGetRequiredSetting(settings, $"PASSWORD.{location}", out var password, out error))
        {
            return false;
        }

        command =
            $"{psExecPath} \\\\{ip} -u {name} -p \"{password}\" -i -h tasklist /FI \"PID eq {pid.ToString(CultureInfo.InvariantCulture)}\"";
        return true;
    }

    public static bool TryResolvePsExecPath(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        out string psExecPath,
        out string? error)
    {
        psExecPath = string.Empty;
        error = null;

        if (!TryGetRequiredSetting(settings, RootDirectoryKey, out var rootDirectory, out error))
            return false;

        var candidate = Path.Combine(rootDirectory.Trim(), PsExecRelativePath);
        if (!File.Exists(candidate))
        {
            error = $"PsExec64.exe was not found at '{candidate}'";
            return false;
        }

        psExecPath = candidate;
        return true;
    }

    public static bool TryGetRequiredSetting(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        string key,
        out string value,
        out string? error)
    {
        value = string.Empty;
        error = null;

        if (!settings.TryGetValue(key, out var values))
        {
            error = $"required setting '{key}' is missing";
            return false;
        }

        var configured = values.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        if (configured is null)
        {
            error = $"required setting '{key}' has no non-empty value";
            return false;
        }

        value = configured;
        return true;
    }
}

/// <summary>
/// One validated SLOT/FSN/IMEI/STATUS record from an upper workflow message.
/// </summary>
internal sealed record CFGSUpperMessageItem(int Slot, string Fsn, string Imei, string Status);

/// <summary>Result of classifying a tasklist PID lookup.</summary>
internal enum PidLookupResult
{
    NotFound,
    Found,
    Unknown,
}
