# Node.Heartbeat

## Purpose

`Node.Heartbeat` forwards each received input sample immediately and repeatedly publishes
the most recently received sample on a fixed periodic cadence.

## Contract Surface

- Display name: `Heartbeat`
- Category: `General`
- Input pin: `input`
- Output pin: `output`
- Both pins use the same selected data type and default to `STRING`.

## Parameters

- `DataType`: single-select parameter with the same options as `Shrinker`:
  `string`, `int32`, `int64`, `float`, `double`, `binary`, and `bool`.
- `IntervalMilliseconds`: positive integer period in milliseconds. The default is `1000`.

## Behavior

- Every valid input sample is immediately forwarded to `output`.
- The latest input is cached as an independent cloned sample.
- A fixed-cadence timer starts during `Run` and repeatedly sends a cloned copy of the latest
  cached sample. Receiving a new input does not reset the timer.
- Before the first input arrives, the timer runs but sends nothing.
- Periodic sends use Trace-level dispatch logs to avoid flooding the log panel.
- If `IntervalMilliseconds` is zero or negative, a warning is logged, periodic sending is
  disabled, and immediate forwarding remains available.
- `Stop` cancels the timer, releases its resources, and clears the cached sample.

## Sample Ownership

Samples are mutable in AgentFlow. The node clones the received sample before caching it and
clones the cached sample again before every periodic send. Binary payloads are copied into a
new byte array, so later changes to an upstream sample cannot alter the heartbeat snapshot.

## Build

```powershell
dotnet build "Node.Heartbeat.csproj" -c Debug -p:Platform=x64
```