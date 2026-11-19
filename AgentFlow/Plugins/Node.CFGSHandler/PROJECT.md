# Node.CFGSHandler

## Purpose

`Node.CFGSHandler` converts an upper-workflow `SLOT`/`FSN`/`IMEI` batch into two complete
CFGS `BUSINESS` envelopes, one for `CFG-1` slots 1-5 and one for `CFG-2` slots 6-10. Launch,
stop, and confirmation pin behavior remains reserved for future CFGS control flow.

## Contract Surface

- Display name: `CFGS Handler`
- Category: `Protocol`
- Input pins:
  - `in_str_raw_from_cfg` (`STRING`, optional, reserved)
  - `in_str_from_upper` (`STRING`, optional)
  - `in_str_user_settings` (`STRING`, optional)
  - `in_int64_cfgstart_ret` (`INT64`, optional, reserved)
  - `in_int64_cfgexit_ret` (`INT64`, optional, reserved)
  - `in_str_cfgstart_ret` (`STRING`, optional, reserved)
  - `in_str_cfgexit_ret` (`STRING`, optional, reserved)
- Output pins:
  - `out_str_launch_cfg` (`STRING`, reserved)
  - `out_stop_cfg` (`STRING`, reserved)
  - `out_str_confirmcfg_exit` (`STRING`, reserved)
  - `output_string_message` (`STRING`)

## Message Input

`in_str_from_upper` accepts one JSON object:

```json
{
  "MESSAGES": [
    {"SLOT":"1","FSN":"603122002902AB","IMEI":"354360451234561"},
    {"SLOT":"6","FSN":"7G4440003601A1","IMEI":"353990620000005"}
  ]
}
```

- `MESSAGES` must contain between one and ten records.
- `SLOT` is an integer string from `1` through `10`.
- Upstream `STATUS` is not required or consumed.
- Missing slots are emitted as blank `FSN`/`IMEI` with `STATUS=IDLE`.
- A record with only `FSN` or only `IMEI` is emitted as `IDLE`.
- A duplicate `SLOT` logs a warning, retains the last value, and stops the node.
- An invalid or out-of-range `SLOT` logs an error and stops the node.

## User Settings Input

`in_str_user_settings` accepts:

```json
{
  "GUID": {
    "CFG-1": "11111111-1111-1111-1111-111111111111",
    "CFG-2": "22222222-2222-2222-2222-222222222222"
  }
}
```

The node caches both validated GUIDs. An input batch is rejected with an error and stops the
node when either GUID has not been supplied.

## Output

Each accepted batch builds two fixed five-record `DATA.MESSAGES` arrays:

- `CFG-1`: slots `1` through `5`
- `CFG-2`: slots `6` through `10`

Each present slot uses `STATUS=TEST`; missing or incomplete slots use `STATUS=IDLE`. A group
whose five records are all `IDLE` is not sent. Non-empty groups are sent in order through
`output_string_message`. `TYPE`, `VERSION`, `SEQ`, and `TIMESTAMP` are generated internally.

## Lifecycle

`Initialize` resets cached GUIDs and sequence counters. `Run` sets `Running = true`. `Stop`
sets `Running = false` and clears runtime-only state. Validation failures also set
`Running = false` as defined above.

## Build

```powershell
dotnet build "Plugins/Node.CFGSHandler/Node.CFGSHandler.csproj" -c Debug -p:Platform=x64
```