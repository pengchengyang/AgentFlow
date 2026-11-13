# Node.AutoStop

## Purpose

`Node.AutoStop` is a small workflow-control plugin. It accepts one exact exit message on
`input_string_stop` and broadcasts the JSON payload for GUI or workflow consumers. It has
one input pin and no output pins.

## Input Contract

The accepted input is exactly:

```text
;{"Request":"Exit"};
```

Any other value is ignored without logging. Input is compared as an exact ordinal string;
there is no buffering, JSON parsing, slot validation, or workflow-stop request.

## Broadcast

When the exact input is received, the node publishes one `StringSample` with this value:

```json
{"Request":"Exit"}
```

It is sent on `RTSBroadcastSubscriptions.AutoStopExit`, whose wire value is
`Workflow.AutoStop.Exit`. The node does not stop the workflow itself; subscribers decide
how to handle the exit signal.

## Contract Surface

- Display name: `Auto Stop`
- Category: `Control`
- Input pin: `input_string_stop` (`STRING`, optional)
- Output pins: none

## Lifecycle

`Initialize` and `Run` capture the logger. `Run` sets `Running = true`. `Stop` clears
`Running` and releases the cached logger. `ReceiveSample` has one responsibility: recognize
the exact exit message and publish the broadcast.

## Build

```powershell
dotnet build "AgentFlow/plugins/Node.AutoStop/Node.AutoStop.csproj" -c Debug -p:Platform=x64
```
