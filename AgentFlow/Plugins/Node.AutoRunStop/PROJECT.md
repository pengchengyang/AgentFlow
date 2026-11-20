# Node.AutoRunStop

## Purpose

Handles workflow run and stop control messages arriving on one input pin. A valid `TYPE=RUN`
participates in the CFG launcher start handshake and emits a correlated `RUN_ACK`. A valid
`TYPE=DISCONNECT` publishes the stop broadcast and requests a normal workflow stop from the
host.

## Contract Surface

- Display name: `Auto Run Stop`
- Category: `C`
- Input pin: `input_string_sample` (`STRING`, optional)
- Output pin: `output_string_sample` (`STRING`)
- Parameter: `RunReplyTimeoutMilliseconds` (`int`, default `60000`)
- Broadcast subscriptions: `CFGBroadcastSubscriptions.CFGLauncherStartReply`
- Broadcast publications: `CFGBroadcastSubscriptions.CFGLauncherStart` and
  `CFGBroadcastSubscriptions.CFGAutoStopExit`
- Required host service for stop: `IWorkflowStopRequestSink`

## Input Dispatch

The node trims optional semicolon framing and parses one complete JSON object. `RUN` matching
is case-insensitive, while `DISCONNECT` retains the existing exact-match behavior:

- `RUN`: process the launcher start handshake.
- `DISCONNECT`: process the workflow stop request.
- Any other value: log a warning and ignore it.

## RUN Handling

A RUN envelope must contain:

- Valid `GUID`
- Non-empty `LOCATION`
- Non-empty `VERSION`
- Positive numeric or string `SEQ`
- Object `DATA`

On a valid RUN the node:

1. Replaces any previous pending RUN and records `GUID`, `LOCATION`, `VERSION`, and `SEQ`.
2. Publishes `CFGLauncherStart` with `CFGLauncherStartSignals.CFGRequest`.
3. Waits asynchronously for `CFGLauncherStartReply` with
   `CFGLauncherStartSignals.CFGStarted`.
4. Emits `RUN_ACK` on `output_string_sample`.

The acknowledgement preserves the original `GUID`, `LOCATION`, and `VERSION`, uses a new
positive ACK sequence, the current UTC timestamp, and `DATA` values `RESULT=OK`,
`STATE=RUNNING`, and `RUN_SEQ` set to the original RUN sequence.

`RunReplyTimeoutMilliseconds` defaults to `60000`. If no matching reply arrives in time, the
pending RUN is discarded, an error is logged, and no `RUN_ACK` is emitted.

## DISCONNECT Handling

When top-level `TYPE` is `DISCONNECT`, the node:

1. Publishes `CFGBroadcastSubscriptions.CFGAutoStopExit` with the payload
   `{"Request":"Stop"}`.
2. Calls `IWorkflowStopRequestSink.RequestStop(Name, triggerMessage)` when the service is
   registered.

If the stop sink is unavailable, the stop broadcast is still attempted and a warning is logged.

## Lifecycle

`Initialize` captures the logger and topic bus, creates the launcher reply subscription, and
creates the node lifetime cancellation source. `Run` enables input processing. `Stop` and
`Shutdown` cancel pending RUN waits, dispose the reply subscription, and clear runtime
references.

## Build

```powershell
dotnet build "Plugins/Node.AutoRunStop/Node.AutoRunStop.csproj" -c Debug -p:Platform=x64
```
