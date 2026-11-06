// -----------------------------------------------------------------------
// <copyright company="Your Company" file="UserSettingsParser.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Node.UserSettings;

/// <summary>
/// Parses the fixed AgentFlow user-settings INI file.
/// Each section name is the dictionary key and every non-empty content line is
/// stored as one value in the section's ordered value collection.
/// </summary>
public sealed class UserSettingsParser
{
    private readonly string _settingsDirectory;
    private readonly string _settingsFilePath;

    private readonly ILogger? _logger;

    public UserSettingsParser(
        ILogger? logger,
        string settingsDirectory,
        string settingsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);

        _logger = logger;
        _settingsDirectory = settingsDirectory;
        _settingsFilePath = settingsFilePath;
    }

    /// <summary>
    /// The result of the most recent successful load. It is empty until a file
    /// has been read successfully.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Settings { get; private set; } =
        EmptySettings();

    /// <summary>
    /// Loads the fixed UserSettings.ini file. Missing directories are created;
    /// a missing file is reported at Error level and is never created implicitly.
    /// </summary>
    public bool TryLoad(out IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        settings = EmptySettings();

        try
        {
            if (!Directory.Exists(_settingsDirectory))
            {
                Directory.CreateDirectory(_settingsDirectory);
                _logger?.LogInformation(
                    "Created UserSettings directory at {Directory}.",
                    _settingsDirectory);
            }

            if (!File.Exists(_settingsFilePath))
            {
                _logger?.LogError(
                    "UserSettings.ini is required at {FilePath}. The file was not found.",
                    _settingsFilePath);
                return false;
            }

            var parsed = ParseFile(_settingsFilePath);
            Settings = parsed;
            settings = parsed;
            _logger?.LogInformation(
                "Loaded UserSettings.ini from {FilePath} with {SectionCount} sections.",
                _settingsFilePath,
                parsed.Count);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(
                ex,
                "Failed to load UserSettings.ini from {FilePath}.",
                _settingsFilePath);
            return false;
        }
    }

    /// <summary>
    /// Loads the settings and returns an empty dictionary when the file is
    /// unavailable. Use <see cref="TryLoad"/> when failure must be observable.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Load()
    {
        TryLoad(out var settings);
        return settings;
    }

    /// <summary>
    /// Loads the fixed INI file and returns its parsed sections as a JSON object.
    /// Each section is represented by an array of strings so multiple values are preserved.
    /// </summary>
    public bool TryLoadAsJson(out string json, bool indented = false)
    {
        json = string.Empty;
        if (!TryLoad(out var settings))
            return false;

        json = SerializeSettings(settings, indented);
        return true;
    }

    /// <summary>Serializes the current parsed settings as a JSON object.</summary>
    public string ToJson(bool indented = false)
        => SerializeSettings(Settings, indented);

    /// <summary>
    /// Rebuilds section settings from a JSON object. Section names remain case-insensitive,
    /// while string values preserve their original casing and order.
    /// </summary>
    public bool TryFromJson(string? json, out IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        settings = EmptySettings();
        if (string.IsNullOrWhiteSpace(json))
        {
            _logger?.LogError("Cannot parse UserSettings JSON because the payload is empty.");
            return false;
        }

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>?>>(json);
            if (raw is null)
            {
                _logger?.LogError("Cannot parse UserSettings JSON because the payload is null.");
                return false;
            }

            var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in raw)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                if (!merged.TryGetValue(pair.Key, out var values))
                {
                    values = new List<string>();
                    merged.Add(pair.Key, values);
                }

                if (pair.Value is not null)
                    values.AddRange(pair.Value);
            }

            var parsed = merged.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
            Settings = parsed;
            settings = parsed;
            _logger?.LogInformation(
                "Parsed UserSettings JSON with {SectionCount} sections.",
                parsed.Count);
            return true;
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to parse UserSettings JSON.");
            return false;
        }
    }

    /// <summary>
    /// Rebuilds settings from JSON and returns an empty dictionary when parsing fails.
    /// Use <see cref="TryFromJson"/> when failure must be observable.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FromJson(string? json)
    {
        TryFromJson(json, out var settings);
        return settings;
    }

    private static string SerializeSettings(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        bool indented)
    {
        var payload = settings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = indented,
        });
    }


    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseFile(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        return ParseLines(lines);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseLines(IEnumerable<string> lines)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = default(string);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var sectionName = line[1..^1].Trim();
                if (sectionName.Length == 0)
                    continue;

                if (!sections.TryGetValue(sectionName, out var values))
                {
                    values = new List<string>();
                    sections.Add(sectionName, values);
                }

                currentSection = sectionName;
                continue;
            }

            if (currentSection is null)
                continue;

            sections[currentSection].Add(line);
        }

        return sections.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> EmptySettings()
        => new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}




