# Node.UserSettings

## Purpose

Loads the fixed AgentFlow user-settings INI file during initialization, broadcasts the
complete keyed settings snapshot for GUI consumers, and exposes runtime settings through
output pins. The node has no dependency on other plugin projects; shared broadcast contracts
are provided by the workspace libraries.

## Contract Surface

- Display name: `User Settings`
- Category: `Configuration`
- Input pin: `input_string_ready` (`STRING`, optional)
- Output pins: `output_string_slotA`, `output_string_slotB`, `output_string_slotC`, `output_string_slotD`, `output_string_slotF`, `output_string_endpoint` (`STRING`)
- Parameters: `Profile`, `SettingsJson`
- Broadcast topic: `fact.launcher.settings`

## Lifecycle

`Initialize` captures the node logger, loads and parses the fixed INI file, caches the
parsed sections on the node instance, and publishes a complete initialization snapshot to
`fact.launcher.settings`. The snapshot is a `SampleListsByKey` dictionary whose outer keys
are INI section names and whose values are ordered `StringSample` lists. A GUI must subscribe
before workflow startup to receive this one-shot initialization broadcast.

A missing, unreadable, or invalid settings file fails initialization and therefore prevents
the node from entering `Run`. A broadcast failure or the absence of GUI subscribers does not
fail initialization; it is logged while the cached settings remain available for runtime use.
After a successful broadcast, the complete settings snapshot is serialized as JSON and
logged at `INFO` level for diagnostics and GUI-payload verification.

`Run` does not read the INI file again. It requires the settings cached by `Initialize`,
sets `Running = true`, publishes the first non-empty endpoint from the `IP` section through
`output_string_endpoint`, and routes one JSON message to each connected SLOT output pin.
SLOT values `1` through `5` map to A/B/C/D/F, and values `6` through `10` map to the same
outputs respectively:

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
targeting an already assigned output are also ignored with a warning.

`input_string_ready` remains available as an optional integration trigger and only records
the received readiness value. `Stop` clears the running state, cached settings, and logger.

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

## Initialization Broadcast Contract

`Initialize` publishes `BroadcastTopicMessage.FromSampleListDictionary(...)` on
`FlashForgeHmiBroadcastSubscriptions.LauncherSettings`. For example, the following INI content:

```ini
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

Consumers select sections by key and validate `Sample` types and value counts according to
their own topic contract. TopicBus does not parse, merge, or normalize the payload.

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
`Node.FACTLauncher.input_string_settings` pin. `Node.UserSettings` publishes these Pin values
during its own `Run`; `input_string_ready` remains available as an optional integration
trigger. `UserSettings.output_string_endpoint` can provide the configured `IP` endpoint for
nodes that need the runtime address.

GUI consumers subscribe to `fact.launcher.settings` before workflow startup and receive the
complete keyed initialization snapshot from `Initialize`. Because TopicBus does not retain
messages, a GUI that subscribes after initialization must use a separate current-state query
mechanism or reload the workflow.