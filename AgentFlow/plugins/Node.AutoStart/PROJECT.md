# Node.AutoStart

## Purpose

`Node.AutoStart` is a workflow-control plugin. It receives semicolon-terminated JSON groups
on `input_string_start`, validates each group, emits the original group JSON to a slot-specific
output pin, and forwards a workflow-start request to a host-provided service.

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

## Output Mapping

When a group is parsed successfully, the node sends the original validated JSON group to one output pin:

| SLOT | Output pin |
| --- | --- |
| `1` or `6` | `output_string_readyA` |
| `2` or `7` | `output_string_readyB` |
| `3` or `8` | `output_string_readyC` |
| `4` or `9` | `output_string_readyD` |
| `5` or `10` | `output_string_readyF` |

Multiple groups that map to the same pin are dispatched independently in input order.

## Contract Surface

- Display name: `Auto Start`
- Category: `Control`
- Input pin: `input_string_start` (`STRING`, optional)
- Output pins: `output_string_readyA`, `output_string_readyB`, `output_string_readyC`, `output_string_readyD`, `output_string_readyF` (`STRING`)
- Output payload: the validated JSON group without its trailing semicolon
- Required host service: `IWorkflowStartRequestSink`

## Streaming Behavior

TCP does not preserve message boundaries. The node keeps a pending input buffer across
`StringSample` calls and processes a group only after a semicolon has been received.
Incomplete trailing content remains buffered until later input arrives or the node stops.

An empty segment between consecutive semicolons is ignored. Invalid JSON, missing fields,
or an out-of-range `SLOT` is logged as an error and does not produce output.

The pending buffer is capped at 64 KiB to prevent unbounded growth from malformed input.

## Lifecycle

`Initialize` and `Run` capture the node context and logger and clear pending input.
`Run` sets `Running = true` and waits for JSON groups. `Stop` clears pending input and
releases the cached context and logger.

For each valid group, the node sends the original JSON group to the mapped output pin and calls
`IWorkflowStartRequestSink.RequestStart`. If the host service is unavailable, the ready
message is still dispatched and a warning is logged.

## JSON Key/Value Parsing

`JsonKeyValueParser.cs` provides plugin-local parsing. It supports both the existing
string-array value format and scalar string values used by the AutoStart input contract.
The parser has no dependency on other plugin projects.

## Build

```powershell
dotnet build "AgentFlow/plugins/Node.AutoStart/Node.AutoStart.csproj" -c Debug -p:Platform=x64
```

