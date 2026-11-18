# Node.CFGSHandler

## Purpose

`Node.CFGSHandler` is the contract-only scaffold for the future CFGS launch and stop handler.
The node currently exposes its input and output pins but does not parse, forward, transform,
launch, or stop anything.

## Contract Surface

- Display name: `CFGS Handler`
- Category: `Protocol`
- Input pins:
  - `in_str_raw_from_cfg` (`STRING`, optional)
  - `in_str_from_upper` (`STRING`, optional)
  - `in_str_user_settings` (`STRING`, optional)
  - `in_int64_cfgstart_ret` (`INT64`, optional)
  - `in_int64_cfgexit_ret` (`INT64`, optional)
  - `in_str_cfgstart_ret` (`STRING`, optional)
  - `in_str_cfgexit_ret` (`STRING`, optional)
- Output pins:
  - `out_str_launch_cfg` (`STRING`)
  - `out_stop_cfg` (`STRING`)
  - `out_str_confirmcfg_exit` (`STRING`)

## Lifecycle

`Initialize` and `Shutdown` currently perform no work. `Run` sets `Running = true`, and `Stop`
sets `Running = false`. Input samples are ignored until the business behavior is implemented.

## Next Steps

Define how `in_str_raw_from_cfg`, `in_str_from_upper`, and `in_str_user_settings` are combined
to produce `out_str_launch_cfg` and `out_stop_cfg`, then implement dispatch checks and logging.

## Build

```powershell
dotnet build "Plugins/Node.CFGSHandler/Node.CFGSHandler.csproj" -c Debug -p:Platform=x64
```