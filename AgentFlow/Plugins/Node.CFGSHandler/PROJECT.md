# Node.CFGSHandler

## Purpose

`Node.CFGSHandler` converts an upper-workflow `SLOT`/`FSN`/`IMEI` batch into CFGS startup control
and `BUSINESS` envelopes. The first non-empty batch sends one `RUN` envelope to each configured
CFG, then sends `BUSINESS` to the groups that contain test data. `RUN_ACK` messages received
through `in_str_raw_from_cfg` are validated and logged. Launch, stop, and confirmation pin behavior
remains reserved for future CFGS control flow.

## Contract Surface

- Display name: `CFGS Handler`
- Category: `Protocol`
- Input pins:
  - `in_str_raw_from_cfg` (`STRING`, optional): accepts complete CFG envelopes and consumes `RUN_ACK`.
  - `in_str_from_upper` (`STRING`, optional)
  - `in_str_user_settings` (`STRING`, optional)
  - `int64_run_ret` (`INT64`, optional, reserved)
  - `int64_exit_ret` (`INT64`, optional, reserved)
  - `str_run_ret` (`STRING`, optional, reserved)
  - `str_exit_ret` (`STRING`, optional, reserved)
  - `str_stop_ret` (`STRING`, optional, reserved)
  - `int64_stop_ret` (`INT64`, optional, reserved)
- Output pins:
  - `cmd_run_cfg` (`STRING`, reserved)
  - `cmd_stop_cfg` (`STRING`, reserved)
  - `cmd_exit_cfg` (`STRING`, reserved)
  - `cmd_sure_exit_cfg` (`STRING`, reserved)
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

Before the first accepted batch is sent, the node emits one `TYPE=RUN` envelope for `CFG-1` and
one for `CFG-2`, using the configured GUIDs and locations. `RUN` is sent only once until the node
is stopped, reinitialized, or receives a different GUID mapping.

Each accepted batch builds two fixed five-record `DATA.MESSAGES` arrays:

- `CFG-1`: slots `1` through `5`
- `CFG-2`: slots `6` through `10`

Each present slot uses `STATUS=TEST`; missing or incomplete slots use `STATUS=IDLE`. A group
whose five records are all `IDLE` is not sent. Non-empty groups are sent in order through
`output_string_message`. `TYPE`, `VERSION`, `SEQ`, and `TIMESTAMP` are generated internally.

## Lifecycle

`Initialize` resets cached GUIDs, sequence counters, and RUN acknowledgement state. `Run`
sets `Running = true`. `Stop` sets `Running = false` and clears runtime-only state. Validation
failures also set `Running = false` as defined above. A valid `RUN_ACK` must match the configured
GUID and location and carry `DATA.RESULT=OK`; success and failure are logged at information
and error level respectively.

## Build

```powershell
dotnet build "Plugins/Node.CFGSHandler/Node.CFGSHandler.csproj" -c Debug -p:Platform=x64
```