# Node.FACTLauncher

## Purpose

Provides a FACT launcher node for SLOT-aware FSN/IMEI capture, user-settings coordination,
and launcher-settings broadcasts.

## Contract Surface

- Display name: `FACT Launcher`
- Category: `Data`
- Input pins:
  - `str_slotfsnimei_run` (`STRING`, optional)
  - `input_string_settings` (`STRING`, optional)
  - `input_int64_ret` (`INT64`, optional)
- Output pins:
  - `output_string_host` (`STRING`)
  - `output_string_cmd` (`STRING`)
  - `output_int64_ret` (`INT64`, aligned with `input_int64_ret`)
  - `out_str_flatfilepath` (`STRING`)
- Parameters: `TimeoutSeconds`, `FactKey`, `Position` (`A`, `B`, `C`, `D`, `F`)

## Lifecycle

`Initialize` captures the node logger, refreshes its runtime display name, and clears cached
settings and FSN/IMEI state. The launcher does not read the user-settings INI file directly;
`Node.UserSettings` owns that responsibility.

During `Run`, the node sets `Running = true` and waits for settings on
`input_string_settings`. The node does not subscribe to launcher-start request broadcasts.

When `input_string_settings` receives a JSON payload, the launcher validates and caches
`CMD`, `IP`, `SLOT`, and `PORTHUB`. Only a complete payload is accepted. Property values
may be scalar strings or string arrays.

The `SLOT` value must parse to an integer from `1` through `10` and map to this node's
configured `Position`: `1/6 -> A`, `2/7 -> B`, `3/8 -> C`, `4/9 -> D`, and
`5/10 -> F`. A missing, invalid, or mismatched `SLOT` is logged at `ERROR` level and the
payload is rejected without changing cached settings or sending commands. After a valid
payload, the launcher broadcasts the ordered settings samples and sends a pending FACT
command when FSN/IMEI data is already available.

`Stop` clears the running state, logger, and cached settings.

## Run Input Contract

`str_slotfsnimei_run` receives one JSON object per message:

```json
{"SLOT":"1","FSN":"603122002902AB","IMEI":"354360451234561"}
```

The `Position` parameter controls SLOT-group validation. At runtime, `DisplayName` is
rendered as `FACT Launcher {Position}`:

| Position | Runtime DisplayName | Accepted SLOT values |
| --- | --- | --- |
| `A` | `FACT Launcher A` | `1`, `6` |
| `B` | `FACT Launcher B` | `2`, `7` |
| `C` | `FACT Launcher C` | `3`, `8` |
| `D` | `FACT Launcher D` | `4`, `9` |
| `F` | `FACT Launcher F` | `5`, `10` |

A matching payload must contain non-empty string values for `SLOT`, `FSN`, and `IMEI`.
When the group matches the instance letter, the launcher stores the latest FSN/IMEI values.
Invalid JSON, missing fields, invalid SLOT values, or a SLOT belonging to another launcher
group do not overwrite cached values.

## RET Output Contract

When `input_int64_ret` receives an `Int64Sample` with a non-null `Int64Value`, the launcher:

- forwards the original integer through `output_int64_ret`; and
- builds a compact BUSINESS payload for `output_string_host` using the cached
  `SLOT`, `FSN`, and `IMEI`.

The host payload has this shape:

```json
{"TYPE":"BUSINESS","DATA":{"MESSAGES":[{"SLOT":"1","FSN":"603122002902AB","IMEI":"354360451234561","STATUS":"OK"}]}}
```

`STATUS` is `OK` when the RET value is `0`; every non-zero RET value produces `NG`.
The payload is one complete JSON object without a trailing semicolon, so it can be connected
directly to `CFG TCP Client.input_string_message`.

The raw `output_int64_ret` value is forwarded independently of host payload construction and
preserves the original Int64 result for downstream calculations such as `PassFailTotalYield`.
On every RET input the launcher also searches `C:\AC710\OUT\Logs` recursively for the newest
`.xml` file whose name contains the cached FSN. A matching absolute path is sent through
`out_str_flatfilepath`; an unconnected optional output is skipped without an error. After
`output_string_host` successfully dispatches the RET BUSINESS payload, the launcher clears
cached `FSN`, `IMEI`, and the associated SLOT so the next business cycle must provide fresh
identity data. A disconnected or failed host dispatch does not clear the cache.

If cached `SLOT`, `FSN`, or `IMEI` is unavailable, the node logs a warning and skips
`output_string_host` without suppressing the raw RET output. Invalid or null input on this pin
is ignored with a warning. Disconnected outputs only produce dispatch warnings and do not block
other launcher inputs.
## User Settings Integration

Connect the pins as follows:

```text
UserSettings.output_string_slotA/B/C/D/F -> FACTLauncher.input_string_settings
```

`Node.UserSettings` reads `C:\AC710\UserSettings\UserSettings.ini` and routes one
SLOT- and PORTHUB-specific JSON message to each connected slot output. The example below
shows the message sent for SLOT A and PORTHUB 5:1:

```json
{
  "CMD": ["C:\\AC710\\FACT.exe /PC:\\sierra\\example.cfg"],
  "IP": ["127.0.0.1:8900"],
  "SLOT": ["1"],
  "PORTHUB": ["5:1"]
}
```

The first value of each required key is cached. Parse failures or incomplete settings
produce no settings broadcast.

## Launcher Settings Broadcast

The launcher publishes `CFGBroadcastSubscriptions.CFGLauncherSettings` using
`BroadcastTopicMessage.FromSamples(...)` with samples in this order:

| Index | Sample key | Value |
| --- | --- | --- |
| `0` | `CMD` | `_factCommand` |
| `1` | `IP` | `_ip` |
| `2` | `SLOT` | `_slot` |
| `3` | `PORTHUB` | `_porthub` |

`Node.AutoRunStop` owns the `CFGBroadcastSubscriptions.CFGLauncherStop` broadcast.

## Launcher Command Broadcast

When `SendCommand` successfully dispatches the full FACT command to
`output_string_cmd`, the launcher publishes a keyed Sample dictionary to:

`CFGBroadcastSubscriptions.CFGLauncherCommand` (`FACT.Launcher.Command.C3578D71-A049-43B8-AFE1-2482C1D0FFDD`)

The payload is:

```text
BroadcastTopicMessage.SamplesByKey[Position] -> StringSample
StringValue -> complete command
```

`Position` is the launcher's current `A`, `B`, `C`, `D`, or `F` value. The dictionary
contains one entry for the sending launcher, so GUI consumers can identify the module
position without relying on subscription order.

The broadcast is sent only when `SendSampleWithDispatchLog(...)` returns `true`. If the
command output pin is disconnected or dispatch fails, GUI consumers receive no command
broadcast. GUI handlers should subscribe to `FACT.Launcher.Command.C3578D71-A049-43B8-AFE1-2482C1D0FFDD` and read
`BroadcastTopicMessage.SamplesByKey`.

## JSON Key/Value Parsing

`JsonKeyValueParser.cs` is plugin-local and has no dependency on other plugin projects.
The settings parser accepts scalar string values and string-array values in the same JSON
object. Key matching is case-insensitive, while values preserve their original casing,
content, and order.
