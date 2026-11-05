# Node.SpecialSettings

## Purpose

Provides the default node skeleton for applying profile-specific settings to a workflow value.

## Contract Surface

- Display name: `Special Settings`
- Category: `Configuration`
- Input pin: `input` (`STRING`, optional)
- Output pin: `output` (`STRING`)
- Parameters: `Profile`, `SettingsJson`, `PassThrough`

## Lifecycle

`Initialize` captures the node logger. `Run` sets `Running = true`. `Stop` clears the running
state and logger. Incoming string samples are recorded and, when `PassThrough` is enabled,
forwarded through the `output` pin.

## Next Steps

Replace the `TODO` in `ReceiveSample` with the `SettingsJson` transformation required by the
business use case.
