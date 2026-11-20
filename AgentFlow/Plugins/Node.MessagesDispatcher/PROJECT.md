# Node.MessagesDispatcher

## Purpose

Validates a JSON object containing a fixed five-record `MESSAGES` array, dispatches each
`STATUS=TEST` record to its SLOT-specific output pin, and asks the host to start the workflow
once when the batch contains test data. RUN protocol handshaking belongs to
`Node.AutoRunStop`.

## Contract Surface

- Display name: `Messages Dispatcher`
- Category: `C`
- Input pin: `in_string_messages` (`STRING`, optional)
- Output pins: `output_string_readyA`, `output_string_readyB`, `output_string_readyC`,
  `output_string_readyD`, and `output_string_readyF` (`STRING`)
- Required host service: `IWorkflowStartRequestSink`

## Input Contract

The input must be one complete JSON object. Semicolon stream framing is not handled here.

```json
{"MESSAGES":[{"SLOT":"1","FSN":"","IMEI":"","STATUS":"IDLE"},{"SLOT":"2","FSN":"603122002902AB","IMEI":"354360451234561","STATUS":"TEST"},{"SLOT":"3","FSN":"","IMEI":"","STATUS":"IDLE"},{"SLOT":"4","FSN":"","IMEI":"","STATUS":"IDLE"},{"SLOT":"5","FSN":"","IMEI":"","STATUS":"IDLE"}]}
```

Validation rules:

- Top-level value must be a JSON object.
- `MESSAGES` must exist and be a JSON array containing exactly five records.
- All records must belong to either SLOT `1-5` or SLOT `6-10`.
- Each SLOT in the selected group must occur exactly once.
- Each record must contain string fields `SLOT`, `FSN`, `IMEI`, and `STATUS`.
- `FSN` and `IMEI` must both be empty or both be non-empty.
- `STATUS` must be `IDLE` or `TEST`.
- Empty `FSN`/`IMEI` requires `STATUS=IDLE`.
- Property-name lookup is case-insensitive.

## Output Mapping

Each `STATUS=TEST` record is emitted as its own compact JSON object:

| SLOT | Output pin |
| --- | --- |
| `1` or `6` | `output_string_readyA` |
| `2` or `7` | `output_string_readyB` |
| `3` or `8` | `output_string_readyC` |
| `4` or `9` | `output_string_readyD` |
| `5` or `10` | `output_string_readyF` |

`STATUS=IDLE` records validate normally but produce no output.

## Launcher Start Broadcast Compatibility

The node subscribes to `CFGBroadcastSubscriptions.CFGLauncherStart` so a non-RUN JSON payload
containing `MESSAGES` can still use the same dispatch path. The exact RUN request payload
`CFGLauncherStartSignals.CFGRequest` is ignored by this node and owned by
`Node.AutoRunStop`.

## Lifecycle and Errors

`Initialize` creates the launcher-start subscription. `Run` enables input processing.
`Stop` and `Shutdown` dispose the subscription and clear runtime references.

Invalid JSON, missing or invalid `MESSAGES`, invalid records, duplicate slots, mixed slot
groups, and incomplete slot coverage are logged at `ERROR` and produce no output.

When at least one TEST record is dispatched, `MessagesDispatcher` calls
`IWorkflowStartRequestSink.RequestStart(Name, triggerMessage)` once for that input.

## Build

```powershell
dotnet build "Plugins/Node.MessagesDispatcher/Node.MessagesDispatcher.csproj" -c Debug -p:Platform=x64
```
