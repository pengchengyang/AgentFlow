# Node.UserSettings

## Purpose

Provides the default node skeleton for applying profile-specific settings to a workflow value.

## Contract Surface

- Display name: `User Settings`
- Category: `Configuration`
- Input pin: `input_string_normal` (`STRING`, optional)
- Output pin: `output_string_json` (`STRING`)
- Parameters: `Profile`, `SettingsJson`, `PassThrough`

## Get User Settings Command

When `input_string_normal` receives `get user settings`, the node parses the fixed INI
file with `UserSettingsParser`. On success, it sends the resulting JSON string through
`output_string_json` only when that output pin has a connected downstream input. Missing
output wiring is logged as an error; parse or file-read failures are also logged as errors.
## Lifecycle

`Initialize` captures the node logger. `Run` sets `Running = true`. `Stop` clears the running
state and logger. Incoming string samples are recorded and, when `PassThrough` is enabled,
forwarded unchanged through `output_string_json`.

## User Settings File

`UserSettingsParser` reads the fixed file `C:\AC710\UserSettings\UserSettings.ini`.

- INI section names are dictionary keys.
- Each non-empty line in a section is stored as one ordered value.
- Multiple lines in one section produce multiple values.
- Missing directories are created automatically.
- A missing `UserSettings.ini` is not created implicitly and is reported through an
  `Error`-level log entry.
## JSON Transport

`UserSettingsParser` also provides JSON transport methods:

- `TryLoadAsJson(out string json, bool indented = false)` reads the INI file and returns a JSON object.
- `ToJson(bool indented = false)` serializes the currently loaded settings.
- `TryFromJson(string json, out ...)` rebuilds the section dictionary from JSON.
- `FromJson(string json)` returns the rebuilt dictionary or an empty dictionary on failure.

The JSON object uses section names as keys and string arrays as values. Section names
are matched case-insensitively; value strings preserve their original casing and order.
## Next Steps

Replace the `TODO` in `ReceiveSample` with the `SettingsJson` transformation required by the
business use case.
