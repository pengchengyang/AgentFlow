# AgentFlow.Desktop

## Purpose

`AgentFlow.Desktop` is the Windows/Linux desktop entry point for the Avalonia editor. It
configures process-wide logging, installs crash handlers, and starts the classic desktop
application lifetime.

## Stability Behavior

- Writes rolling logs through `Hosting.Logging` and mirrors entries into `UiLogSink`.
- Logs unhandled Avalonia dispatcher, AppDomain, and unobserved task exceptions at fatal/error level.
- Flushes the shared logger when the application lifetime ends.

## Build

```powershell
dotnet build 'AgentFlow.Desktop\AgentFlow.Desktop.csproj' -c Debug --no-restore -v:minimal
```
