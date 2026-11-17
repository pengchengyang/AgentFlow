# Node.AutoStart

## Purpose

`Node.AutoStart` is a workflow-control plugin. It receives one JSON object containing a
`MESSAGES` array on `input_string_start`, validates each business message, emits each individual
message JSON to a SLOT-specific output pin, and forwards a workflow-start request to a
host-provided service.

The upstream `Node.CFGConfimer` emits the complete envelope `DATA` object. A typical input is:

```json
{"MESSAGES":[{"SLOT":"2","FSN":"603122002902AB","IMEI":"354360451234561"},{"SLOT":"6","FSN":"7G4440003601A1","IMEI":"353990620000005"}]}
```

## Input Contract

The input must be one complete JSON object. Semicolon streaming and frame buffering are not
handled by this node.

- Top-level value: JSON object.
- Required property: `MESSAGES`.
- `MESSAGES` value: JSON array.
- Each array element: JSON object containing non-empty string values for `SLOT`, `FSN`, and `IMEI`.
- Property-name lookup is case-insensitive.
- `FSN` and `IMEI` are preserved exactly, including any leading slash.

Each `SLOT` must contain an integer from `1` through `10`.

## Output Mapping

Each valid array element is emitted as its own compact JSON object through one output pin:

| SLOT | Output pin |
| --- | --- |
| `1` or `6` | `output_string_readyA` |
| `2` or `7` | `output_string_readyB` |
| `3` or `8` | `output_string_readyC` |
| `4` or `9` | `output_string_readyD` |
| `5` or `10` | `output_string_readyF` |

For example, this input element:

```json
{"SLOT":"2","FSN":"603122002902AB","IMEI":"354360451234561"}
```

is sent to `output_string_readyB` as the same individual JSON object, not as part of an outer
`MESSAGES` wrapper.

Multiple groups that map to the same pin are dispatched independently in array order.

## Contract Surface

- Display name: `Auto Start`
- Category: `Control`
- Input pin: `input_string_start` (`STRING`, optional)
- Output pins: `output_string_readyA`, `output_string_readyB`, `output_string_readyC`, `output_string_readyD`, `output_string_readyF` (`STRING`)
- Output payload: one compact JSON business-message object per valid `MESSAGES` element
- Required host service: `IWorkflowStartRequestSink`

## Validation and Error Handling

- Invalid top-level JSON: `ERROR`, no output.
- Missing `MESSAGES`: `ERROR`, no output.
- `MESSAGES` not an array: `ERROR`, no output.
- Empty `MESSAGES` array: `WARNING`, no output.
- Array element not an object: `ERROR` for that item, continue with remaining items.
- Missing or empty `SLOT`, `FSN`, or `IMEI`: `ERROR` for that item, continue.
- `SLOT` outside `1..10`: `ERROR` for that item, continue.

## Lifecycle

`Initialize` and `Run` capture the node context and logger. `Run` sets `Running = true`.
`Stop` clears the cached context and logger. Because each input is a complete JSON object,
the node has no pending-input buffer or batch-start state to reset.

For each valid business message, the node sends the compact item JSON to the mapped output pin
and calls `IWorkflowStartRequestSink.RequestStart`. If the host service is unavailable, the ready
message is still dispatched and a warning is logged.

## Build

```powershell
dotnet build "plugins/Node.AutoStart/Node.AutoStart.csproj" -c Debug -p:Platform=x64
```
