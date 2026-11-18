# Node.AutoStart

## Purpose

`Node.AutoStart` is a workflow-control plugin. It receives one JSON object containing a
`MESSAGES` array on `input_string_start`, validates all five records, emits only
`STATUS=TEST` records to SLOT-specific output pins, and requests at most one workflow start
for the batch through a host-provided service.

The upstream `Node.CFGConfimer` emits the complete envelope `DATA` object. A typical input is:

```json
{"MESSAGES":[{"SLOT":"1","FSN":"","IMEI":"","STATUS":"IDLE"},{"SLOT":"2","FSN":"603122002902AB","IMEI":"354360451234561","STATUS":"TEST"},{"SLOT":"3","FSN":"","IMEI":"","STATUS":"IDLE"},{"SLOT":"4","FSN":"","IMEI":"","STATUS":"IDLE"},{"SLOT":"5","FSN":"","IMEI":"","STATUS":"IDLE"}]}
```

## Input Contract

The input must be one complete JSON object. Semicolon streaming and frame buffering are not
handled by this node.

- Top-level value: JSON object.
- Required property: `MESSAGES`.
- `MESSAGES` value: JSON array.
- The array must contain exactly five elements from either SLOT 1-5 or SLOT 6-10.
- Each array element: JSON object containing string fields `SLOT`, `FSN`, `IMEI`, and `STATUS`.
- `FSN` and `IMEI` may both be empty for an idle slot. They must otherwise both be non-empty.
- `STATUS` must be `IDLE` or `TEST`.
- Property-name lookup is case-insensitive.
- `FSN` and `IMEI` are preserved exactly, including any leading slash.

Each `SLOT` must contain an integer from `1` through `10`.

## Output Mapping

Each `STATUS=TEST` record is emitted as its own compact JSON object through one output pin:

| SLOT | Output pin |
| --- | --- |
| `1` or `6` | `output_string_readyA` |
| `2` or `7` | `output_string_readyB` |
| `3` or `8` | `output_string_readyC` |
| `4` or `9` | `output_string_readyD` |
| `5` or `10` | `output_string_readyF` |

For example, this input element:

```json
{"SLOT":"2","FSN":"603122002902AB","IMEI":"354360451234561","STATUS":"TEST"}
```

is sent to `output_string_readyB` as the same individual JSON object, not as part of an outer
`MESSAGES` wrapper.

Records with `STATUS=IDLE` are logged at `WARNING` and are not dispatched. A batch can dispatch multiple TEST records, but it requests at most one workflow start.

## Contract Surface

- Display name: `Auto Start`
- Category: `Control`
- Input pin: `input_string_start` (`STRING`, optional)
- Output pins: `output_string_readyA`, `output_string_readyB`, `output_string_readyC`, `output_string_readyD`, `output_string_readyF` (`STRING`)
- Output payload: one compact JSON TEST record per dispatched `MESSAGES` element
- Required host service: `IWorkflowStartRequestSink`

## Validation and Error Handling

- Invalid top-level JSON: `ERROR`, no output.
- Missing `MESSAGES`: `ERROR`, no output.
- `MESSAGES` not an array: `ERROR`, no output.
- `MESSAGES` array length other than five: `ERROR`, no output.
- Array element not an object: `ERROR`, no output.
- Missing or empty `SLOT`: `ERROR`, no dispatch.
- Missing `FSN` or `IMEI`: `ERROR`, no dispatch; empty strings are allowed when both are empty.
- Missing or invalid `STATUS`: `ERROR`, no dispatch.
- The array is not exactly five records, mixes SLOT groups, contains duplicate slots, or misses a slot: `ERROR`, no dispatch.
- `STATUS=IDLE` records are accepted for array validation but log `WARNING` and produce no output.
- A workflow start is requested once per batch when at least one dispatched record has `STATUS=TEST`.

## Lifecycle

`Initialize` and `Run` capture the node context and logger. `Run` sets `Running = true`.
`Stop` clears the cached context and logger. Because each input is a complete JSON object,
the node has no pending-input buffer or batch-start state to reset.

For each `STATUS=TEST` record, the node sends the compact item JSON to the mapped output pin.
`IWorkflowStartRequestSink.RequestStart` is called at most once per input batch after all TEST
records are dispatched. A batch containing only IDLE records produces warnings and does not
request a workflow start. If the host service is unavailable, the ready messages are still
dispatched and a warning is logged.

## Build

```powershell
dotnet build "plugins/Node.AutoStart/Node.AutoStart.csproj" -c Debug -p:Platform=x64
```
