# Node.UserSettings

## Purpose

Loads the fixed AgentFlow user-settings INI file during initialization, broadcasts the
complete keyed settings snapshot for GUI consumers, and exposes runtime settings through
output pins. The node has no dependency on other plugin projects; shared broadcast contracts
are provided by the workspace libraries.

## Contract Surface

- Display name: `User Settings`
- Category: `Configuration`
- Input pins: `input_string_ready` and `input_string_settings` (`STRING`, optional)
- Output pins: `output_string_slotA`, `output_string_slotB`, `output_string_slotC`, `output_string_slotD`, `output_string_slotF`, `output_string_endpoint`, `output_string_settings`, `output_string_guid` (`STRING`)
- Parameters: `Profile`, `SettingsJson`
- Broadcast topics: `CFGBroadcastSubscriptions.CFGLauncherSettings` and `CFGBroadcastSubscriptions.CFGUserSettingsGuid`

## Lifecycle

`Initialize` captures the node logger, loads and parses the fixed INI file, requires a
valid `[GUID]` section, caches the parsed sections on the node instance, publishes the parsed
settings JSON through `output_string_settings` when connected, and publishes a complete
initialization snapshot to
`FACT.Launcher.Settings.48D865B9-AB18-4DD0-A7A8-0F79D63EEDBB`. The snapshot is a
`SampleListsByKey` dictionary whose outer keys are INI section names and whose values are
ordered `StringSample` lists. The `GUID` section is excluded from this broadcast and from
the existing settings JSON outputs because the validated GUID is published only through
`output_string_guid`. A GUI must subscribe before workflow startup to receive this one-shot
initialization broadcast.

A missing, unreadable, or invalid settings file fails initialization and therefore prevents
the node from entering `Run`. The `[GUID]` section is mandatory, must contain one non-empty
valid GUID, and is normalized to uppercase `8-4-4-4-12` form. A broadcast failure or the
absence of GUI subscribers does not fail initialization; it is logged while the cached
settings remain available for runtime use. After a successful broadcast, the settings
snapshot without the GUID section is serialized as JSON and logged at `INFO` level for
diagnostics and GUI-payload verification.

`Run` does not read the INI file again. It requires the settings cached by `Initialize`,
sets `Running = true`, publishes the first non-empty endpoint from the `IP` section through
`output_string_endpoint`, routes one JSON message to each connected SLOT output pin, and
attempts to publish the cached GUID through `output_string_guid`. The GUID is sent at most
once after a successful dispatch. A disconnected pin or dispatch failure is logged at
`ERROR` level and does not mark the GUID as sent, so the next `Run` can retry.
SLOT values `1` through `5` map to A/B/C/D/F, and values `6` through `10` map to the same
outputs respectively:

| SLOT | Output pin |
| --- | --- |
| `1` or `6` | `output_string_slotA` |
| `2` or `7` | `output_string_slotB` |
| `3` or `8` | `output_string_slotC` |
| `4` or `9` | `output_string_slotD` |
| `5` or `10` | `output_string_slotF` |

Each SLOT output receives the settings JSON without the `GUID` section, with `SLOT` and
`PORTHUB` replaced by the values at the same position in their respective comma-separated sections. For an INI
value `SLOT=1,2,3,4,5` and `PORTHUB=5:1,23:1,7:2,8:2,9:2`, A receives
`SLOT: ["1"]` with `PORTHUB: ["5:1"]`, B receives `["2"]` with `["23:1"]`, and so on.
A missing or empty matching PORTHUB value prevents that output from being sent, and extra
PORTHUB values without matching SLOT entries are ignored with a warning. Duplicate values
targeting an already assigned output are also ignored with a warning.

`input_string_ready` remains available as an optional integration trigger and only records
the received readiness value. `Stop` clears the running state, cached settings, cached GUID,
GUID-sent state, and logger.

A missing directory is created automatically. A missing `UserSettings.ini` file is never
created implicitly and is reported at `ERROR` level.

## User Settings File

The fixed path is:

```text
C:\AC710\UserSettings\UserSettings.ini
```

INI section names are dictionary keys. Every non-empty content line in a section is stored
as one ordered value. Section names are case-insensitive; string values preserve their
original casing and order except that the validated GUID output is normalized to uppercase.
The file must include a `[GUID]` section with one valid GUID value.

## Initialization Broadcast Contract

`Initialize` publishes `BroadcastTopicMessage.FromSampleListDictionary(...)` on
`CFGBroadcastSubscriptions.CFGLauncherSettings`. For example, the following INI content:

```ini
[GUID]
CA7EFFA7-A2EB-4AC0-864A-1E1100D40B90
[CMD]
C:\AC710\FACT.exe /PC:\sierra\example.cfg
[IP]
127.0.0.1:8900
[SLOT]
1,2,3,4,5
[PORTHUB]
5:1,23:1,7:2,8:2,9:2
```

is represented conceptually as:

```text
CMD     -> [StringSample("C:\AC710\FACT.exe /PC:\sierra\example.cfg")]
IP      -> [StringSample("127.0.0.1:8900")]
SLOT    -> [StringSample("1,2,3,4,5")]
PORTHUB -> [StringSample("5:1,23:1,7:2,8:2,9:2")]
```

The `GUID` section is intentionally not included in this broadcast payload.

Consumers select sections by key and validate `Sample` types and value counts according to
their own topic contract. TopicBus does not parse, merge, or normalize the payload.

## Settings JSON Output

During `Initialize`, the node serializes the parsed section dictionary without the
`GUID` section and sends it through `output_string_settings` as one `StringSample`. The
JSON uses section names as keys and string arrays as values, matching the existing
`SettingsJson` representation.

The output is sent once per initialization. If the pin is not connected, the node logs a
warning and continues initialization and the existing launcher-settings broadcast.
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

## Settings Broadcast Input

The optional `input_string_settings` pin accepts a JSON dictionary such as:

```json
{
  "CFG-1": "11111111-1111-1111-1111-111111111111",
  "CFG-2": "22222222-2222-2222-2222-222222222222"
}
```

A successful parse publishes the dictionary on
`CFGBroadcastSubscriptions.CFGUserSettingsGuid`:

```text
CFG.UserSettings.Guid.D7D7367B-61A4-4CE7-B41B-E2E1E4B55F91
```

The payload is a `BroadcastTopicMessage.SamplesByKey` dictionary. Each dictionary key becomes
the Sample key and each string value becomes a `StringSample`. Subscribers must be registered
before the input pin receives data because TopicBus does not retain messages.

## Integration

Connect one of `Node.UserSettings.output_string_slotA/B/C/D/F` to the corresponding
`Node.FACTLauncher.input_string_settings` pin. `Node.UserSettings` publishes these Pin values
during its own `Run`; `input_string_ready` remains available as an optional integration
trigger. `UserSettings.output_string_endpoint` can provide the configured `IP` endpoint for
nodes that need the runtime address. `output_string_guid` publishes the validated uppercase
GUID once per node lifecycle after a successful dispatch.

GUI consumers subscribe to `fact.launcher.settings` before workflow startup and receive the
complete keyed initialization snapshot from `Initialize`. Because TopicBus does not retain
messages, a GUI that subscribes after initialization must use a separate current-state query
mechanism or reload the workflow.

