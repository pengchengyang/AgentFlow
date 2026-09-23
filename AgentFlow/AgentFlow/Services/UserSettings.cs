// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="UserSettings.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;

namespace AgentFlow.Services;

/// <summary>
/// Lightweight per-user settings persisted as JSON under the user''s AppData folder
/// (the cross-platform equivalent of .NET user settings). Values are simple key/value strings.
/// </summary>
public sealed class UserSettings
{
    private static readonly string DirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentFlow");

    private static readonly string FilePath = Path.Combine(DirPath, "usersettings.json");

    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static UserSettings Load()
    {
        var s = new UserSettings();
        if (File.Exists(FilePath))
        {
            try
            {
                s._values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                            ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch
            {
                s._values = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
        return s;
    }

    public string Get(string key, string defaultValue = "")
        => _values.TryGetValue(key, out var v) ? v : defaultValue;

    public void Set(string key, string value) => _values[key] = value;

    public void Save()
    {
        Directory.CreateDirectory(DirPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
    }
}