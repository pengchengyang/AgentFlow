# Node.FactCaller

## Purpose

Provides the default node skeleton for a fact lookup workflow. The final fact service integration
is intentionally left for the business implementation phase.

## Contract Surface

- Display name: `Fact Caller`
- Category: `Data`
- Input pin: `request` (`STRING`, optional)
- Output pin: `fact` (`STRING`)
- Parameters: `Endpoint`, `TimeoutSeconds`, `FactKey`

## Lifecycle

`Initialize` captures the node logger. `Run` sets `Running = true`. `Stop` clears the running
state and logger. Incoming request samples are recorded and logged without side effects.

## Next Steps

Replace the `TODO` in `ReceiveSample` with the configured fact-service call and publish the
result through the `fact` output pin.
