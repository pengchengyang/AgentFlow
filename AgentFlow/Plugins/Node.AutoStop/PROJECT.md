# Node.AutoStop

## Purpose

`Node.AutoStop` is a small workflow-control plugin. It accepts a JSON string on
`input_string_stop`, parses the complete object, and triggers only when the top-level string
property `TYPE` is exactly `DISCONNECT`. When triggered, it broadcasts the existing stop
payload and requests a normal workflow stop from the host. It has one input pin and no output
pins.

## Input Contract

The input must be one complete JSON object. Leading and trailing whitespace and optional
surrounding semicolons are removed before parsing. The node does not split semicolon-delimited
frames or buffer partial input.

The input triggers a stop only when it contains a string property named `TYPE` whose value is
exactly `DISCONNECT`, using case-sensitive comparison:

```json
{"GUID":"11111111-1111-1111-1111-111111111111","LOCATION":"CFG-1","TYPE":"DISCONNECT","VERSION":"1.0","SEQ":10,"TIMESTAMP":"2026-11-18T02:43:00.3890000Z","DATA":{}}
```

Invalid JSON, a non-object root, a missing or non-string `TYPE`, and any other `TYPE` value are
ignored with a `WARNING` log. No broadcast is published and no workflow stop is requested.

## Broadcast

When the fragment is found, the node publishes one `StringSample` with this value:

```json
{"Request":"Stop"}
```

It is sent on `CFGBroadcastSubscriptions.CFGAutoStopExit`, whose wire value is
`Workflow.AutoStop.Exit.5526559F-0CEF-4728-90F2-C87943496AA8`.

The original received input is passed to `IWorkflowStopRequestSink.RequestStop` as the trigger
message. The node does not directly finalize application shutdown; the workflow host decides
how to handle the stop request.

## Contract Surface

- Display name: `Auto Stop`
- Category: `Control`
- Input pin: `input_string_stop` (`STRING`, optional)
- Output pins: none

## Lifecycle

`Initialize` and `Run` capture the logger, topic bus, and workflow stop sink. `Run` sets
`Running = true`. `Stop` clears `Running` and releases cached services. `ReceiveSample` trims
optional semicolon framing, parses one complete JSON object, validates the top-level `TYPE`,
and publishes the stop request only for `TYPE=DISCONNECT`.

## Build

```powershell
dotnet build "Plugins/Node.AutoStop/Node.AutoStop.csproj" -c Debug -p:Platform=x64
```
