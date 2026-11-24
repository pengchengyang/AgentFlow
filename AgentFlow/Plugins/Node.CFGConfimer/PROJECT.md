# Node.CFGConfimer

## Purpose

`Node.CFGConfimer` validates complete CFG protocol envelopes for the current local GUID and settings-derived location.

- `TYPE` equal to `BUSINESS` routes the validated `DATA.MESSAGES` payload through `output_string_messages`.
- Any other `TYPE`, including `RUN` and `RUN_ACK`, routes the complete original envelope through
  `output_string_others`.
- Any validation failure logs an error and sets `Running = false`.

Each transport frame carries exactly one envelope. `VERSION` and `SEQ` are ignored. Semicolons remain transport frame delimiters and are not interpreted inside the node.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `in_string_local_guid` | Input | STRING | Supplies the current local GUID. A changed valid GUID clears the cached BUSINESS timestamp. |
| `in_str_user_settings` | Input | STRING | Accepts the complete JSON object emitted by `Node.ReadIniFile`. The active `SLOT` values derive `CFG-1` or `CFG-2`. |
| `in_string_messages` | Input | STRING | Accepts one complete protocol envelope without the outer semicolon frame. |
| `output_string_messages` | Output | STRING | Emits a compact JSON wrapper containing the validated BUSINESS `MESSAGES` array. |
| `output_string_others` | Output | STRING | Emits the complete original envelope when `TYPE` is not `BUSINESS`. |

The three input pins are added in the order shown above, matching their visual alignment in the node editor.

## Local Location

There is no `LocalLocation` parameter. The node derives the local location from the complete UserSettings JSON received on `in_str_user_settings`:

- active `SLOT` values exactly equal to `1,2,3,4,5` derive `CFG-1`
- active `SLOT` values exactly equal to `6,7,8,9,10` derive `CFG-2`

Comment lines starting with `#` or `;` are ignored by the INI parser before the JSON reaches this node. Missing, empty, incomplete, duplicate, mixed-range, or otherwise invalid SLOT values log an error, clear the derived location, and stop the node.

The local GUID is supplied by a pin rather than a parameter. The UserSettings and local GUID providers should run first or be connected through `DependsOn`.

## Routing and Validation

Every incoming envelope is checked for:

- A valid target `GUID` equal to the current local GUID.
- A top-level `LOCATION` equal to the location derived from `in_str_user_settings`.
- A non-empty `TYPE`.

### BUSINESS envelopes

A `BUSINESS` envelope is additionally checked for:

- A parseable `TIMESTAMP`.
- A strictly newer `TIMESTAMP` than the last successfully dispatched BUSINESS timestamp.
- `DATA` as a JSON object.
- `DATA.MESSAGES` as exactly five records for the configured location: SLOT 1-5 for `CFG-1` and SLOT 6-10 for `CFG-2`.
- Each record containing `SLOT`, `FSN`, `IMEI`, and `STATUS` (`IDLE` or `TEST`), with unique and complete slots.

Timestamp comparison is performed in UTC. Equal, older, missing, or invalid timestamps are rejected. Successful BUSINESS output is wrapped as `{"MESSAGES":[...]}` so `Node.MessagesDispatcher` can continue parsing the array.

### Other envelope types

Non-`BUSINESS` envelopes do not require `DATA.MESSAGES` and are not compared against the BUSINESS timestamp cache. The complete original envelope is emitted as compact JSON through `output_string_others`. `RUN` is logged as a startup control message; `RUN_ACK` is handled by the CFG TCP client transport and is forwarded unchanged if it reaches this node.

Any validation failure logs an `ERROR` and sets `Running = false`:

- Missing local GUID or missing/invalid `in_str_user_settings` SLOT configuration.
- Invalid envelope JSON.
- Missing or invalid target `GUID`.
- GUID mismatch.
- Missing or mismatched `LOCATION`.
- Missing `TYPE`.
- BUSINESS-only failures involving `TIMESTAMP`, `DATA`, or `DATA.MESSAGES`.

A disconnected output produces the normal dispatch warning. The BUSINESS timestamp is cached only after `output_string_messages` dispatches successfully, allowing a failed dispatch to be retried.

## Output Contract

BUSINESS output:

```json
{"MESSAGES":[{"SLOT":"1","FSN":"","IMEI":"","STATUS":"IDLE"},{"SLOT":"2","FSN":"603122002902AB","IMEI":"354360451234561","STATUS":"TEST"},{"SLOT":"3","FSN":"","IMEI":"","STATUS":"IDLE"},{"SLOT":"4","FSN":"","IMEI":"","STATUS":"IDLE"},{"SLOT":"5","FSN":"","IMEI":"","STATUS":"IDLE"}]}
```

Other `TYPE` output is the original envelope with all top-level fields preserved.

## Build

```powershell
dotnet build "Plugins/Node.CFGConfimer/Node.CFGConfimer.csproj" -c Debug -p:Platform=x64
```
