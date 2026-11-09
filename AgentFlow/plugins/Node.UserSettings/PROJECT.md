# Node.UserSettings

## Purpose

Reads the fixed AgentFlow user-settings INI file and exposes its content as JSON through a
standalone configuration node. The node has no dependency on other plugin projects.

## Contract Surface

- Display name: `User Settings`
- Category: `Configuration`
- Input pin: `input_string_ready` (`STRING`, optional)
- Output pins: `output_string_slotA`, `output_string_slotB`, `output_string_slotC`, `output_string_slotD`, `output_string_slotF`, `output_string_endpoint` (`STRING`)
- Parameters: `Profile`, `SettingsJson`

## Lifecycle

`Initialize` captures the node logger. `Run` reads and parses the fixed INI file first;
only a successful parse sets `Running = true`. A missing, unreadable, or invalid settings
file fails `Run` without publishing settings or reporting a successful running state.

After a successful parse, the node publishes the first non-empty endpoint from the
`IP` section through `output_string_endpoint`. It then reads the comma-separated `SLOT`
section and routes one JSON message to each connected SLOT output pin. SLOT values `1`
through `5` map to A/B/C/D/F, and values `6` through `10` map to the same outputs
respectively:

| SLOT | Output pin |
| --- | --- |
| `1` or `6` | `output_string_slotA` |
| `2` or `7` | `output_string_slotB` |
| `3` or `8` | `output_string_slotC` |
| `4` or `9` | `output_string_slotD` |
| `5` or `10` | `output_string_slotF` |

Each output receives the complete settings JSON with `SLOT` and `PORTHUB` replaced by
the values at the same position in their respective comma-separated sections. For an INI
value `SLOT=1,2,3,4,5` and `PORTHUB=5:1,23:1,7:2,8:2,9:2`, A receives
`SLOT: ["1"]` with `PORTHUB: ["5:1"]`, B receives `["2"]` with `["23:1"]`, and so on.
A missing or empty matching PORTHUB value prevents that output from being sent, and extra
PORTHUB values without matching SLOT entries are ignored with a warning. Duplicate values
targeting an already assigned output are also ignored with a warning. `Stop` clears the
running state and logger. `input_string_ready` remains available as an integration trigger,
but settings publication is owned by `Run`.

A missing directory is created automatically. A missing `UserSettings.ini` file is never
created implicitly and is reported at `ERROR` level.

## User Settings File

The fixed path is:

```text
C:\AC710\UserSettings\UserSettings.ini
```

INI section names are dictionary keys. Every non-empty content line in a section is stored
as one ordered value. Section names are case-insensitive; string values preserve their
original casing and order.

## JSON Transport

`UserSettingsParser` supports:

- `TryLoad(out settings)` to parse the INI file.
- `TryLoadAsJson(out json, bool indented = false)` to parse and serialize the INI file.
- `ToJson(bool indented = false)` to serialize the most recently loaded settings.
- `TryFromJson(json, out settings)` to rebuild the section dictionary from JSON.
- `FromJson(json)` to return the rebuilt dictionary or an empty dictionary on failure.

JSON uses section names as keys and string arrays as values. The example below represents
the message sent to `output_string_slotA`; other SLOT outputs use their assigned single
slot number:

```json
{
  "CMD": ["C:\\AC710\\FACT.exe /PC:\\sierra\\example.cfg"],
  "IP": ["127.0.0.1:8900"],
  "SLOT": ["1"],
  "PORTHUB": ["5:1"]
}
```

## Integration

Connect one of `Node.UserSettings.output_string_slotA/B/C/D/F` to the corresponding
`Node.FACTLauncher.input_string_settings` pin. `Node.UserSettings` publishes settings during
its own `Run`; `input_string_ready` remains available as an optional integration trigger.
`UserSettings.output_string_endpoint` can provide the configured `IP` endpoint for nodes
that need the runtime address. This keeps file access and INI parsing owned by the
configuration node while downstream nodes only consume the resulting values.
