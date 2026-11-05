# Node.FactLogFetcher

## Purpose

Provides the default node skeleton for retrieving logs associated with a fact. The final log
source connector is deferred.

## Contract Surface

- Display name: `Fact Log Fetcher`
- Category: `Logging`
- Input pin: `factId` (`STRING`, optional)
- Output pin: `log` (`STRING`)
- Parameters: `LogSource`, `QueryLimit`, `TimeoutSeconds`

## Lifecycle

`Initialize` captures the node logger. `Run` sets `Running = true`. `Stop` clears the running
state and logger. Incoming fact identifiers are recorded and logged without side effects.

## Next Steps

Replace the `TODO` in `ReceiveSample` with the configured log-source query and publish the
result through the `log` output pin.
