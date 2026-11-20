# Node.UserSettings

## Purpose

Consumes the JSON dictionary produced by `Node.ReadIniFile`, validates the GUID, caches the
settings, and publishes the same runtime and broadcast values that the node previously
derived from `UserSettings.ini`. The node no longer reads an INI file directly.

## Contract Surface

- Display name: `User Settings`
- Category: `C`
- Input pin: `input_string_settings` (`STRING`, optional)
- Output pins: `output_string_slotA`, `output_string_slotB`, `output_string_slotC`,
  `output_string_slotD`, `output_string_slotF`, `output_string_endpoint`,
  `output_string_settings`, and `output_string_guid` (`STRING`)
- Parameters: `Profile` and `SettingsJson`
- Broadcast topics: `CFGBroadcastSubscriptions.CFGLauncherSettings` and
  `CFGBroadcastSubscriptions.CFGUserSettingsGuid`

## Input Contract

`input_string_settings` accepts a JSON object whose keys are section names and whose values
are ordered arrays of strings. This is the format emitted by `Node.ReadIniFile`.

```json
{
  "GUID": ["CA7EFFA7-A2EB-4AC0-864A-1E1100D40B90"],
  "CMD": ["C:\\AC710\\FACT.exe /PC:\\sierra\\Pri_Files\\IMEI_track_for_5G_common.cfg /G"],
  "IP": ["127.0.0.1:8900"],
  "SLOT": ["6,7,8,9,10"],
  "PORTHUB": ["5:1,23:1,7:2,8:2,9:2"]
}
```

Section names are case-insensitive. Values preserve their original casing and order. The
`GUID` section is mandatory. A GUID value may be a raw GUID or a named form such as
`CFG-1=11111111-1111-1111-1111-111111111111`; the first non-empty valid GUID is normalized
to uppercase `8-4-4-4-12` form for `output_string_guid`.

Invalid JSON, an empty payload, a missing `GUID` section, or an invalid GUID logs `ERROR`
and sets `Running = false`.

## Lifecycle

`Initialize` captures the logger and topic bus, clears cached settings, and waits for
`input_string_settings`. It does not access the file system and does not fail when settings
have not arrived yet.

`Run` sets `Running = true`. If settings were already received, it publishes the runtime
outputs once for that settings payload; otherwise it logs that the node is waiting. A valid
settings payload received while the node is running publishes the outputs immediately.

`Stop` clears cached settings, GUID state, publication state, the topic bus, and the logger.

## Runtime Outputs

For each accepted settings payload, the node publishes when running:

1. `output_string_settings`: complete settings JSON without the `GUID` section.
2. `output_string_endpoint`: the first non-empty `IP` value.
3. One SLOT output for each valid `SLOT`/`PORTHUB` pair.
4. `output_string_guid`: the normalized GUID, at most once for the current settings payload.

SLOT mapping is unchanged:

| SLOT | Output pin |
| --- | --- |
| `1` or `6` | `output_string_slotA` |
| `2` or `7` | `output_string_slotB` |
| `3` or `8` | `output_string_slotC` |
| `4` or `9` | `output_string_slotD` |
| `5` or `10` | `output_string_slotF` |

Each SLOT output receives the settings JSON without `GUID`, with `SLOT` and `PORTHUB`
replaced by the corresponding values from their comma-separated arrays. Invalid SLOT values,
missing PORTHUB values, and duplicate assignments are logged and skipped.

## Broadcast Outputs

After a valid settings payload is accepted, `CFGLauncherSettings` receives the complete
keyed settings snapshot as `BroadcastTopicMessage.SampleListsByKey`. The `GUID` section is
excluded from this snapshot to preserve the existing contract.

After a valid settings payload is accepted, `CFGUserSettingsGuid` receives
`BroadcastTopicMessage.SamplesByKey` with a representative string sample per key. GUID
entries keep their raw GUID as the sample key when no `NAME=GUID` prefix is present, or use
the prefix name when present.

## SLOT Output Example

For `SLOT` value `6,7,8,9,10` and `PORTHUB` value `5:1,23:1,7:2,8:2,9:2`, A receives:

```json
{
  "CMD": ["C:\\AC710\\FACT.exe /PC:\\sierra\\Pri_Files\\IMEI_track_for_5G_common.cfg /G"],
  "IP": ["127.0.0.1:8900"],
  "SLOT": ["6"],
  "PORTHUB": ["5:1"]
}
```

## Integration

Connect `Node.ReadIniFile.output_string_sample` to
`Node.UserSettings.input_string_settings`. Connect the SLOT outputs to the corresponding
`Node.FACTLauncher.input_string_settings` pins. Connect `output_string_endpoint` to nodes
that require the runtime address. Settings publication is triggered only by
`input_string_settings`.
