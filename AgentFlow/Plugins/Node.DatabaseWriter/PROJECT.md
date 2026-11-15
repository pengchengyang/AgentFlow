# Node.DatabaseWriter

## Purpose

Provides the default node skeleton for a database persistence sink. Database access and
transaction behavior are deferred to the business implementation.

## Contract Surface

- Display name: `Database Writer`
- Category: `Database`
- Input pin: `data` (`STRING`, optional)
- Output pin: `status` (`STRING`)
- Parameters: `ConnectionString`, `TableName`, `BatchSize`, `TimeoutSeconds`

## Lifecycle

`Initialize` captures the node logger. `Run` sets `Running = true`. `Stop` clears the running
state and logger. Incoming data samples are recorded and logged without side effects.

## Next Steps

Replace the `TODO` in `ReceiveSample` with database persistence and publish the write status
through the `status` output pin.
