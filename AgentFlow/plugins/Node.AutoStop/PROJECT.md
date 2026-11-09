# Node.AutoStop

## Purpose

`Node.AutoStop` is a workflow-control plugin. It receives semicolon-terminated JSON groups
on `input_string_stop`, validates each group, and forwards a workflow-stop request to a
host-provided service. The node has one input pin and no output pins.

## Input Contract

The input is a text buffer containing one or more JSON objects separated by English
semicolons. The final object must also be followed by a semicolon so the node can identify
the end of the batch while TCP data is still arriving in arbitrary chunks.

```text
{"SLOT":"1","FSN":"603122002902AB","IMEI":"354360451234561"};{"SLOT":"2","FSN":"/F603122002902AC","IMEI":"/I354360451234562"};
```

Each JSON object must contain:

- `SLOT`: string containing an integer from `1` through `10`
- `FSN`: non-empty string
- `IMEI`: non-empty string

Field-name lookup is case-insensitive. `FSN` and `IMEI` are preserved exactly, including
any leading slash.

## Stop Request

When a group is parsed successfully, the node broadcasts a single `STRING` sample with value
`Stop` on `FactBroadcastSubscriptions.LauncherStop` and sends the original validated JSON
group to `IWorkflowStopRequestSink.RequestStop`. It does not dispatch the group to output pins.

## Contract Surface

- Display name: `Auto Stop`
- Category: `Control`
- Input pin: `input_string_stop` (`STRING`, optional)
- Output pins: none
- Stop trigger payload: the validated JSON group without its trailing semicolon
- Required host service: `IWorkflowStopRequestSink`

## Streaming Behavior

TCP does not preserve message boundaries. The node keeps a pending input buffer across
`StringSample` calls and processes a group only after a semicolon has been received.
Incomplete trailing content remains buffered until later input arrives or the node stops.

An empty segment between consecutive semicolons is ignored. Invalid JSON, missing fields,
or an out-of-range `SLOT` is logged as an error and does not trigger a stop request.

The pending buffer is capped at 64 KiB to prevent unbounded growth from malformed input.

## Lifecycle

`Initialize` and `Run` capture the node context and logger and clear pending input.
`Run` sets `Running = true` and waits for JSON groups. `Stop` clears pending input and
releases the cached context and logger.

For each valid group, the node publishes the shared LauncherStop signal and calls
`IWorkflowStopRequestSink.RequestStop`. If the host service is unavailable, no workflow-stop
request is sent and a warning is logged.

## JSON Key/Value Parsing

`JsonKeyValueParser.cs` provides plugin-local parsing. It supports both the existing
string-array value format and scalar string values used by the AutoStop input contract.
The parser has no dependency on other plugin projects.

## Build

```powershell
dotnet build "AgentFlow/plugins/Node.AutoStop/Node.AutoStop.csproj" -c Debug -p:Platform=x64
```

