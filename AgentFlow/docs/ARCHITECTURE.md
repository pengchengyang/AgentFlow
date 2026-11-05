# AgentFlow Architecture

## Purpose

AgentFlow is a desktop workflow editor and runtime. The editor is responsible for
drawing the graph; reusable logic lives in Contracts and Core so it can run from
desktop, CLI, headless, or future web hosts.

## Projects

| Project | Responsibility |
| --- | --- |
| `AgentFlow.Contracts` | Node, pin, sample, context, lifecycle, and parameter contracts. Has no UI dependency. |
| `AgentFlow.Core` | Plugin loading, node instance management, wiring, validation, workflow execution, and logic serialization/deserialization. Has no Avalonia dependency. |
| `AgentFlow` | Avalonia editor: canvas, palettes, dialogs, view-models, graph JSON composition, and UI logging. |
| `AgentFlow.Desktop` | Desktop entry point and host logging setup. |
| `AgentFlow.Cli` | Headless workflow entry point. |
| `Hosting.Logging` | Shared Serilog-compatible logging integration. |
| `plugins/*` | Dynamically loaded node implementations and demo nodes. |
| `BroadcastKit*` | Optional broadcast transport used by demo nodes. |

## Dependency Direction

```text
AgentFlow / AgentFlow.Desktop / AgentFlow.Cli
                    |
                    v
              AgentFlow.Core
                    |
                    v
            AgentFlow.Contracts
```

Core and Contracts must not reference Avalonia or any GUI project. The GUI may
reference Core and Contracts.

## Node Lifecycle

`BaseNode` owns the runtime lifecycle state:

- `Idle`: not started, or cleanly stopped.
- `Connecting`: initialization or resource acquisition is still in progress.
- `Running`: the node has successfully started and remains operational.
- `Failed`: startup, runtime, or teardown failure.

`Running` is an explicit operational flag. A node implementation must set
`Running = true` before `Run()` returns normally. Keeping the flag true means
the node can still accept or process work until `Stop()` is called.

`NodeInstanceManager` enforces the contract in two places:

1. It rejects a normal `Run()` return when `Running` is still false and marks
   the node as failed.
2. A Core health monitor checks every successful node periodically. If any
   node stops reporting `Running = true` while the workflow is active, Core
   marks that node failed and automatically stops the workflow. This applies
   equally to GUI, CLI, and headless hosts.

GUI status indicators are derived from real node state. The bottom status bar
shows `Running` only when every node is both started and still reporting
`Running = true`. The canvas refresh/gear indicator rotates only while the
specific node reports `Running = true`.

Any node failure is terminal for the current run. Core stops the entire
workflow, the status bar reports `Failed`, Run becomes available again, and
Stop returns to its initial disabled state. `NodeFailureMode.Isolate` retains
diagnostic value for the failed node but does not override the workflow-wide
stop invariant.

## Runtime Data Flow

Pins are defined by `BasePin` and owned by `BaseNode`. Output pins hold the
connected input-pin reference; input pins forward incoming samples to
`BaseNode.ReceiveSample`. The editor layer only draws and establishes/removes
connections; runtime delivery is performed by the Core graph and pin objects.

## Logging Policy

User-operation rejections and recoverable editing constraints are logged at `WARN`:

- Trying to drag, delete, add, wire, clear, save, or edit parameters while the workflow is running.
- Connection validation failures such as wrong direction, self-connection, type mismatch, or occupied pins.
- Expected precondition failures and user cancellations.

Actual runtime faults remain at `ERROR`:

- Node initialization, run, and stop exceptions.
- Corrupted workflow JSON or failed serialization.
- File read/write and autosave failures.
- Plugin loading failures.
- Unexpected Core, UI, or graph-state exceptions.

## Serialization

- `LogicSerializer` / `LogicDeserializer` in Core handle node logic and parameter
  JSON without GUI data.
- `GraphSerializer` / `GraphDeserializer` in the Avalonia project merge logic
  JSON with UI-only data such as coordinates and viewport state.

## Build

Build the full solution with Visual Studio MSBuild:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
  "AgentFlow\AgentFlow.sln" /p:Configuration=Debug /m:1 /nr:false /nologo /v:m
```

Build the desktop host:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
  "AgentFlow\AgentFlow.Desktop\AgentFlow.Desktop.csproj" `
  /p:Configuration=Debug /m:1 /nr:false /nologo /v:m
```
