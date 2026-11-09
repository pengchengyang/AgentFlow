# Node.ProcessRunner

## Purpose

Executes a Windows command supplied on `input_string_cmd` without requesting a new UAC
elevation. AgentFlow must already run with an elevated administrator token, so the started
`cmd.exe` process and its child process tree inherit the elevated token.

## Contract Surface

- Display name: `Process Runner`
- Category: `System`
- Input pin: `input_string_cmd` (`STRING`, optional)
- Output pins:
  - `output_string_ret` (`STRING`)
  - `output_int64_ret` (`INT64`)

## Runtime Behavior

- Rejects non-Windows execution with exit code `-1`.
- Rejects execution when the AgentFlow host is not elevated with exit code `-1`.
- Writes the received command to a temporary `.cmd` file.
- Starts `cmd.exe /d /s /c "<command.cmd>"` with `UseShellExecute = false`.
- Reads stdout and stderr concurrently while the process is running.
- Logs every non-empty output line at `INFORMATION` level with a `STDOUT` or `STDERR` marker.
- Publishes the combined captured output only when non-empty.
- Always publishes the process exit code.
- Stops the active process tree when the node is stopped.
- Removes its temporary directory after execution.

## Elevation Policy

The node no longer uses `ProcessStartInfo.Verb = "runas"`. It does not create UAC prompts.
Automation must launch AgentFlow itself with administrator privileges, or delegate privileged
execution to a separately installed Windows Service or Scheduled Task.
