# Node.FACTLauncher

## Purpose

Provides the default node skeleton for a FACT launch workflow. The final FACT service integration
is intentionally left for the business implementation phase.

## Contract Surface

- Display name: `FACT Launcher`
- Category: `Data`
- Input pins: `input_string_run` (`STRING`, optional), `input_string_settings` (`STRING`, optional), `input_int64_ret` (`INT64`, optional)
- Output pins: `output_string_host` (`STRING`), `output_string_settings` (`STRING`), `output_string_cmd` (`STRING`)
- Parameters: `Endpoint`, `TimeoutSeconds`, `FactKey`

## Lifecycle

`Initialize` captures the node logger. `Run` sets `Running = true`, captures the logger, and
clears cached input data without sending anything. After `input_string_run` receives a valid
payload, the node requests user settings through `output_string_settings`. If that output is
not connected, or sending fails, the node logs an error. `Stop` clears the running state and
logger. The cached FSN and IMEI values are cleared during initialize, run, and stop.

## Run Input Contract

`input_string_run` receives one JSON object per message:

```json
{"SLOT":"1","FSN":"603122002902AB","IMEI":"354360451234561"}
```

The node uses the instance `DisplayName` to validate the SLOT group before saving metadata:

| DisplayName | Accepted SLOT values |
| --- | --- |
| `FACT Launcher A` | `1`, `6` |
| `FACT Launcher B` | `2`, `7` |
| `FACT Launcher C` | `3`, `8` |
| `FACT Launcher D` | `4`, `9` |
| `FACT Launcher F` | `5`, `10` |

A matching payload must contain non-empty string values for `SLOT`, `FSN`, and `IMEI`.
When the group matches the instance letter and all values are valid, the node stores the
latest non-empty values in its private `_fsn` and `_imei` fields. Invalid JSON, missing
fields, invalid SLOT values, or a SLOT that belongs to another launcher group do not
overwrite the cached values.

## Next Steps

Replace the `TODO` in `ReceiveSample` with the configured FACT service call and publish the
result through the `output_string_host` output pin.
## JSON Key/Value Parsing

`JsonKeyValueParser.cs` provides plugin-local parsing for JSON key/value payloads. It has no dependency on other plugin projects.
The parser supports string-array payloads through `TryParse` and scalar string payloads through `TryParseStringValues`.

```json
{
  "CMD": ["C:\\AC710\\FACT.exe /PC:\\sierra\\Pri_Files\\example.cfg"],
  "IP": ["127.0.0.1:8900"],
  "SLOT": ["1,2,3,4,5"],
  "PORTHUB": ["5:1,23:1,7:2,8:2,9:2"]
}
```

Parser behavior:

- Key names are case-insensitive.
- Values preserve their original casing, contents, and order.
- Every key is returned as an ordered string collection.
- `TryParse` returns `false` for invalid JSON or an invalid payload shape.
- `TryGetValues` and `GetSingleValue` provide case-insensitive key lookup.

The node validates JSON received on `input_string_settings` and `input_string_run` and logs parse failures at
`Error` level. The raw settings payload is still forwarded through `output_string_settings`.

### Settings cache

When `input_string_settings` receives a valid JSON object, the node stores the first value for each
supported key in private cache fields for later process-launch use:

| JSON key | Private field |
| --- | --- |
| `CMD` | `_factCommand` |
| `IP` | `_ip` |
| `SLOT` | `_slot` |
| `PORTHUB` | `_porthub` |

Invalid JSON does not overwrite the cached settings. These fields, together with `_fsn` and `_imei`,
are cleared during node initialization, run startup, and stop.





