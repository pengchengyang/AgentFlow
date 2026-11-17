# Node.AutoStop

## Purpose

`Node.AutoStop` is a small workflow-control plugin. It accepts a JSON string on
`input_string_stop`, checks whether the string contains the exact stop request fragment,
broadcasts that stop payload, and requests a normal workflow stop from the host. It has one
input pin and no output pins.

## Input Contract

The input is a complete JSON string. Semicolon framing and stream buffering are not handled by
this node.

The input triggers a stop when it contains this exact text fragment:

```json
{"Request":"Stop"}
```

Matching uses ordinal text comparison. Whitespace variants such as
`{ "Request": "Stop" }` do not match. Any input that does not contain the fragment is ignored
at `TRACE` level.

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
`Running = true`. `Stop` clears `Running` and releases cached services. `ReceiveSample` checks
one complete input string for the stop fragment and publishes the stop request when matched.

## Build

```powershell
dotnet build "plugins/Node.AutoStop/Node.AutoStop.csproj" -c Debug -p:Platform=x64
```
