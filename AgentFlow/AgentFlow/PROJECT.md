# AgentFlow

## Purpose

`AgentFlow` is the Avalonia desktop editor and workflow composition layer. It renders the
canvas, palette, dialogs, log panel, and runtime controls while delegating workflow state and
execution to `AgentFlow.Core`.

## Main Modules

- `MainViewModel`: graph editing, undo/redo, auto-load/auto-save, run status, and lifecycle coordination.
- `GraphSerializer` / `GraphDeserializer`: merges Core logic with editor-only node coordinates and viewport state.
- `LogPanelViewModel` and `UiLogSink`: bounded in-memory log display and filtering.
- `MainWindow`: window chrome, close flow, shutdown ordering, and snap behavior.
- `Services/IWorkflowStore`: local or HTTP persistence adapters.

## Auto-Save and Close Safety

Startup loading and auto-save are exposed as awaitable `Task` operations. Debounced saves
coalesce concurrent requests, permit draft graphs that are not yet runnable, and block the
close flow until startup loading and pending persistence finish. Run-time validation remains
separate. The log store retains at most 2000 entries and 50 errors per run.

## Build

```powershell
dotnet build 'AgentFlow\AgentFlow.csproj' -c Debug --no-restore -v:minimal
```
