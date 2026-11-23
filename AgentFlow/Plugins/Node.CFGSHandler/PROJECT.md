# Node.CFGSHandler

## Purpose

`Node.CFGSHandler` converts an upper-workflow `SLOT`/`FSN`/`IMEI` batch into CFGS startup control
and `BUSINESS` envelopes. The first non-empty batch sends one `RUN` envelope to each configured
CFG, then sends `BUSINESS` to the groups that contain test data. `RUN_ACK` messages received
through `input_str_message` are validated and logged. User settings are also used to prepare
the local PowerShell startup command for CFG-1, the PsExec startup command for CFG-2, and the
exit-confirmation commands.

## Contract Surface

- Display name: `CFGS Handler`
- Category: `Protocol`
- Input pins:
  - `input_str_message` (`STRING`, optional): accepts complete CFG envelopes and consumes `RUN_ACK`.
  - `in_str_from_upper` (`STRING`, optional)
  - `in_str_user_settings` (`STRING`, optional)
  - `str_start_ret1` (`STRING`, optional, reserved)
  - `int64_start_ret1` (`INT64`, optional, reserved)
  - `str_sure_exit1` (`STRING`, optional): accepts CFG-1 PID query, taskkill, and verification output.
  - `int64_sure_exit1` (`INT64`, optional): command-completion trigger carrying the CFG-1 exit code.
  - `str_start_ret2` (`STRING`, optional, reserved)
  - `int64_start_ret2` (`INT64`, optional, reserved)
  - `str_sure_exit2` (`STRING`, optional): accepts CFG-2 remote PID query, taskkill, and verification output.
  - `int64_sure_exit2` (`INT64`, optional): command-completion trigger carrying the CFG-2 exit code.
- Output pins:
  - `cmd_start_cfg1` (`STRING`, reserved)
  - `cmd_sure_exit_cfg1` (`STRING`): drives the CFG-1 PID query/kill/verify sequence.
  - `cmd_start_cfg2` (`STRING`, reserved)
  - `cmd_sure_exit_cfg2` (`STRING`): drives the CFG-2 remote PID query/kill/verify sequence.
  - `output_string_message` (`STRING`)

## Message Input

`in_str_from_upper` accepts a complete semicolon-delimited JSON frame:

```text
;{"MESSAGES":[{"SLOT":"1","FSN":"603122002901AB","IMEI":"354360451234561","STATUS":"IDLE"}, ... total 10 records ...]};
```

- The frame must begin and end with `;`.
- `MESSAGES` must contain exactly ten records.
- `SLOT` is an integer string from `1` through `10`; every slot must appear exactly once.
- `STATUS` must be `IDLE` or `TEST`.
- A `TEST` record requires both `FSN` and `IMEI`.
- Duplicate, missing, out-of-range, malformed, or unsupported records stop the node with an error.

The parser builds two independent groups:

- `CFG-1`: SLOT `1` through `5`
- `CFG-2`: SLOT `6` through `10`

A group is sent only when it contains at least one `STATUS=TEST` record. An all-`IDLE` group
does not emit RUN or BUSINESS messages. For a sent group, `TEST` records retain their FSN,
IMEI, and STATUS; `IDLE` records are emitted as empty FSN/IMEI with `STATUS=IDLE`.

## User Settings Input

`in_str_user_settings` accepts:

```json
{
  "GUID.CFG-1": ["CA7EFFA7-A2EB-4AC0-864A-1E1100D40B90"],
  "GUID.CFG-2": ["CA7EFFA7-A2EB-4AC0-864A-1E1100D40B92"],
  "PORT.LISTEN": ["9000"],
  "SLOT.SLOTS": ["1,2,3,4,5,6,7,8,9,10"],
  "IP.CFG-1": ["127.0.0.1"],
  "IP.CFG-2": ["10.22.43.62"],
  "NAME.CFG-1": ["admin"],
  "NAME.CFG-2": ["damon"],
  "PASSWORD.CFG-1": ["Abc911226."],
  "PASSWORD.CFG-2": ["Abc911226."],
  "ROOTDIR.RTS": ["C:\\RTSResources"]
}
```

The node stores every top-level `SECTION.KEY` entry as a case-insensitive dictionary whose
values remain string arrays. It validates and caches `GUID.CFG-1` and `GUID.CFG-2` for message
routing. Other current or future keys are retained for later use. An input batch is rejected
with an error and stops the node when either GUID has not been supplied.

After the settings are accepted, `CFGSCommandBuilder` prepares the two startup commands:

- `CFG-1` starts the local RTS process through PowerShell:
  ```text
  powershell -NoProfile -Command "$p = Start-Process -FilePath 'C:\RTSResources\RTS\RTS.exe' -ArgumentList '--CFG' -WorkingDirectory 'C:\RTSResources\RTS' -PassThru; Write-Output ('PID=' + $p.Id)"
  ```
- `CFG-2` uses the existing PsExec command and requires
  `ROOTDIR.RTS\RTS\RemoteCallerTool\PsExec64.exe` to exist.

Missing required settings or the CFG-2 PsExec executable stop the node with an error. The
prepared commands are cached for later use by the command output pins.

## Command Outputs

`cmd_start_cfg1` emits the prepared local PowerShell startup command for CFG-1.
`cmd_start_cfg2` emits the prepared PsExec startup command for CFG-2.
`cmd_sure_exit_cfg1` drives the CFG-1 PID exit sequence described below.
`cmd_sure_exit_cfg2` drives the CFG-2 remote PID query, taskkill, and verification sequence
described below with a caller-supplied PID. The current implementation prepares and caches the
startup commands and provides send helpers, but does not send startup commands automatically.

## CFG-1 Exit Sequence

`SendSureExitCommand(CFG-1, pid)` starts a three-step local process-exit sequence:

1. Publish `tasklist /FI "PID eq {PID}"` through `cmd_sure_exit_cfg1`.
2. If the process is already absent, finish successfully without killing anything.
3. If the process exists, publish `taskkill /PID {PID} /F /T`.
4. After taskkill completes, publish the same PID query again.
5. If the verification query reports the PID absent, finish successfully.
6. If the PID is still present, or the tasklist output cannot be classified, log an error and set
   `Running = false`.

`str_sure_exit1` buffers the non-empty tasklist/taskkill output. `int64_sure_exit1` is the command
completion trigger and always supplies the exit code. The parser recognizes both Chinese and
English tasklist "not found" messages and detects target PIDs by their standalone numeric token.

## CFG-2 Exit Sequence

`SendSureExitCommand(CFG-2, pid)` starts a remote process-exit sequence through PsExec:

1. Publish `tasklist /FI "PID eq {PID}"` through `cmd_sure_exit_cfg2`.
2. If the remote process is already absent, finish successfully without killing anything.
3. If the process exists, publish `taskkill /PID {PID} /F /T` with PsExec `-d`, so PsExec only
   confirms that the remote taskkill command was started.
4. After taskkill completion, publish the same PID query as the first verification attempt.
5. If the PID is still present, wait 750 ms and retry the verification query.
6. Verify at most three times total; finish successfully as soon as a query reports the PID absent.
7. If the final attempt still finds the PID, or output cannot be classified, log an error and set
   `Running = false`.

`str_sure_exit2` buffers each remote command output and `int64_sure_exit2` supplies the matching
exit code. Verification retries are cancelled when the node is reinitialized or stopped.

## Protocol Output

For each group that contains at least one `TEST` record, the node emits a `TYPE=RUN` envelope
for that group's configured GUID and location before the first BUSINESS message. Each RUN is sent
only once per node lifecycle or until a replacement settings payload resets the state.

Each sent group contains a fixed five-record `DATA.MESSAGES` array:

- `CFG-1`: slots `1` through `5`
- `CFG-2`: slots `6` through `10`

TEST records retain their input FSN/IMEI and use `STATUS=TEST`; IDLE records use empty FSN/IMEI
and `STATUS=IDLE`. Non-empty groups are sent in order through `output_string_message`. `TYPE`,
`VERSION`, `SEQ`, and `TIMESTAMP` are generated internally.

## Lifecycle

`Initialize` resets cached GUIDs, sequence counters, and RUN acknowledgement state. `Run`
sets `Running = true`. `Stop` sets `Running = false` and clears runtime-only state. Validation
failures also set `Running = false` as defined above. A valid `RUN_ACK` must match the configured
GUID and location and carry `DATA.RESULT=OK`; success and failure are logged at information
and error level respectively.

## Build

```powershell
dotnet build "Plugins/Node.CFGSHandler/Node.CFGSHandler.csproj" -c Debug -p:Platform=x64
```