# Node.FACTFlatFileParser

## Purpose

Provides the skeleton for parsing FACT flat files. Parsing behavior is intentionally deferred.

## Contract Surface

- Display name: `FACT Flat File Parser`
- Category: `Parsing`
- Input pin: `input_string_run` (`STRING`, optional)
- Output pin: `output_string_contents` (`STRING`)
- Parameters: none

## Lifecycle

`Initialize` captures the node logger. `Run` sets `Running = true`. `Stop` clears the running
state and logger. Incoming run requests are logged without parsing or publishing output.

## Next Steps

Implement flat-file parsing in `ReceiveSample` or a dedicated parser service, then publish the
parsed contents through `output_string_contents`.
