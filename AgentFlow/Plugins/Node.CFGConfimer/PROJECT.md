# Node.CFGConfimer

## Purpose

`Node.CFGConfimer` validates complete CFG protocol envelopes for the current local GUID and configured location.

- `TYPE` equal to `BUSINESS` routes the validated `DATA.MESSAGES` payload through `output_string_messages`.
- Any other `TYPE` routes the complete original envelope through `output_string_others`.
- Any validation failure logs an error and sets `Running = false`.

Each transport frame carries exactly one envelope. `VERSION` and `SEQ` are ignored. Semicolons remain transport frame delimiters and are not interpreted inside the node.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_local_guid` | Input | STRING | Supplies the current local GUID. A changed valid GUID clears the cached BUSINESS timestamp. |
| `input_string_messages` | Input | STRING | Accepts one complete protocol envelope without the outer semicolon frame. |
| `output_string_messages` | Output | STRING | Emits a compact JSON wrapper containing the validated BUSINESS `MESSAGES` array. |
| `output_string_others` | Output | STRING | Emits the complete original envelope when `TYPE` is not `BUSINESS`. |

The two input pins are added in the order shown above, matching their visual alignment in the node editor.

## Parameters

| Parameter | Default | Behavior |
| --- | --- | --- |
| `LocalLocation` | empty | Must be configured as `CFG-1` or `CFG-2`. Incoming top-level `LOCATION` is required and must match this value. |

`LocalGuid` is supplied by a pin rather than a parameter. The local GUID provider should run first or be connected through `DependsOn`.

## Routing and Validation

Every incoming envelope is checked for:

- A valid target `GUID` equal to the current local GUID.
- A top-level `LOCATION` equal to `LocalLocation`.
- A non-empty `TYPE`.

### BUSINESS envelopes

A `BUSINESS` envelope is additionally checked for:

- A parseable `TIMESTAMP`.
- A strictly newer `TIMESTAMP` than the last successfully dispatched BUSINESS timestamp.
- `DATA` as a JSON object.
- `DATA.MESSAGES` as exactly five records for the configured location: SLOT 1-5 for `CFG-1` and SLOT 6-10 for `CFG-2`.
- Each record containing `SLOT`, `FSN`, `IMEI`, and `STATUS` (`IDLE` or `TEST`), with unique and complete slots.

Timestamp comparison is performed in UTC. Equal, older, missing, or invalid timestamps are rejected. Successful BUSINESS output is wrapped as `{"MESSAGES":[...]}` so `Node.AutoStart` can continue parsing the array.

### Other envelope types

Non-`BUSINESS` envelopes do not require `DATA.MESSAGES` and are not compared against the BUSINESS timestamp cache. The complete original envelope is emitted as compact JSON through `output_string_others`.

Any validation failure logs an `ERROR` and sets `Running = false`:

- Missing local GUID or incomplete `LocalLocation` configuration.
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