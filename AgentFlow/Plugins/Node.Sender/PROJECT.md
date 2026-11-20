# Node.Sender

## Purpose

`Node.Sender` is a command-driven source node. It sends one sample or repeatedly sends the
configured sample through its `output` pin.

## Parameters

- `DataType`: selects the sample type emitted by the output pin.
- `Value`: source value parsed according to `DataType`.
- `IntervalMs`: delay between continuous sends.
- `ContinuousSendCount`: runtime-only int64 send-count setting shown in the node dialog.

`ContinuousSendCount` is marked non-persistent. It is available while editing the node in the
parameter dialog, but it is not written to workflow JSON.

## Context Menu

The right-click command labels remain unchanged:

- `Send Once`
- `Continuous Send`
- `Stop Continuous Send`

`Continuous Send` snapshots the current parameters before starting. A count greater than zero
stops automatically after that many sends. A count less than or equal to zero keeps the original
unlimited continuous-send behavior. `Stop Continuous Send` cancels either mode.

## Build

```powershell
dotnet build Plugins\Node.Sender\Node.Sender.csproj -c Debug
```
