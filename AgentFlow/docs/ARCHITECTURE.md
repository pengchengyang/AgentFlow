# AgentFlow Architecture

## Purpose

AgentFlow is a desktop workflow editor and runtime. The editor is responsible for
drawing the graph; reusable logic lives in Contracts and Core so it can run from
desktop, CLI, headless, or future web hosts.

## Projects

| Project | Responsibility |
| --- | --- |
| `AgentFlow.Contracts` | Node, pin, sample, context, lifecycle, and parameter contracts. Has no UI dependency. |
| `AgentFlow.Core` | Plugin loading, node instance management, wiring, validation, workflow execution, logic serialization/deserialization, and the GUI-neutral `WorkflowHost` facade. Has no Avalonia dependency. |
| `AgentFlow` | Avalonia editor: canvas, palettes, dialogs, view-models, graph JSON composition, and UI logging. |
| `AgentFlow.Desktop` | Desktop entry point and host logging setup. |
| `AgentFlow.Cli` | Headless workflow entry point. |
| `Hosting.Logging` | Shared Serilog-compatible logging integration. |
| `FACT.Shared` | GUI-neutral FACT declarations, including shared error/result contracts and broadcast subscription names. |
| `plugins/*` | Dynamically loaded node implementations and demo nodes. |
| `BroadcastKit*` | Optional process-local broadcast transport and shared topic/message contracts, including ordered sample batches and correlated request/reply. |

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

## WorkflowHost Boundary

`WorkflowHost` is the shared facade for workflow consumption and runtime control:

- `IWorkflowReadOnlyHost`: load a workflow, inspect immutable snapshots, validate,
  start/stop execution, execute node commands, and read per-node health.
- `IWorkflowEditorHost`: adds graph mutation operations used only by the AgentFlow
  editor, such as create/remove node, connect/disconnect, update node state, and clear.

Product-specific GUIs must use only `IWorkflowReadOnlyHost`. They do not render
nodes, pins, or connections and do not mutate workflow documents. They read
per-node/runtime health and display their own product data through separate
domain services.

## Broadcast Request/Reply

`BroadcastKit` keeps `PublishAsync` as a one-way, no-reply operation. The optional
`RequestAsync` API adds correlated request/reply behavior without changing existing
publishers:

- `RequestAsync(requestTopic, replyTopic, payload, timeout, ct)` subscribes to the
  reply topic before publishing the request.
- Request messages carry `CorrelationId` and `ReplyTo`; only the first reply with the
  matching correlation id completes the request.
- `ReplyAsync(request, payload, ct)` publishes the response to `request.ReplyTo` with
  the original correlation id.
- The default request timeout is five seconds. Timeout, cancellation, and disposal
  always remove the temporary subscription and pending request.
- The bus does not inspect payloads; business meaning remains defined by topic contracts.

## CLI Multi-Instance Execution

`AgentFlow.Cli` runs every configured workflow instance independently in one
process. A workflow may be repeated, and different workflow files may be mixed:

```powershell
AgentFlow.Cli run.workflow.json --instances 5

AgentFlow.Cli --plugin-dir plugins `
  --workflow a.workflow.json --instances 3 `
  --workflow b.workflow.json --instances 2
```

`--instances` applies to the most recently declared workflow and defaults to 1.
Each instance receives a fresh `WorkflowGraph`, `WorkflowEngine`, node
instances, contexts, and lifecycle cleanup. One instance failing does not cancel
the others. The CLI prints a result record per instance and exits with 0 only
when every instance succeeds; any failure returns 1, and Ctrl+C returns 130.
The legacy positional form `AgentFlow.Cli <workflow.json> [pluginDir]` remains
supported.
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

### Startup Dependencies

`BaseNode.DependsOn` is a normalized `IReadOnlyList<int>` of node instance ids.
A node with `DependsOn = [1001, 1002, 1003]` is not started until all three
dependency nodes have completed their startup path and still report both
`State == NodeState.Running` and `Running = true`. This is a startup barrier,
not merely a call-order preference:

- Pin connections are runtime data-flow edges and may form legitimate feedback cycles such as
  A -> B -> A; they are not used to calculate startup order.
- `DependsOn` is the only startup-ordering graph and must remain acyclic.
- Root nodes without dependencies start concurrently.
- Multi-dependency nodes start once, after the final dependency becomes ready.
- If any dependency fails, the dependent node does not call `Run()` and Core
  stops the workflow.
- Missing, self-referencing, duplicate instance-id, and cyclic dependency graphs
  are rejected before runtime launch by validation / Core.
- The editable General parameter accepts comma- or semicolon-separated ids
  (for example `1001,1002,1003`). `0` and non-positive values mean no dependency.
- JSON stores dependencies as an array (`{"dependsOn":[1001,1002,1003]}`) while
  old single-number files (`{"dependsOn":1001}`) remain loadable.
- Core writes an INFO launch plan, logs whether a node starts independently/asynchronously
  or after named dependencies become ready, and emits TRACE dependency-gate progress.

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

Pins are defined by `BasePin` and owned by `BaseNode`. Output pins own the
authoritative downstream target list (`Targets`) and forward samples to those
input pins. Input pins hold the upstream output-pin reference and forward
incoming samples to `BaseNode.ReceiveSample`. On an output pin,
`ConnectedPin` is a compatibility view of the single connected target; on an
input pin it is the stored upstream reference. The editor layer only draws and
establishes/removes connections; runtime delivery is performed by the Core
graph and pin objects. `BasePin.SendSample` rejects every output send when
`TargetCount == 0`, records a `WARN` through the bound node context, and
returns `false` without invoking downstream delivery. It returns `true` only
after dispatching to one or more targets. Nodes use this result to gate
success logs, so disconnected outputs never produce misleading "sent" traces.
Pin cycles are valid when they represent runtime feedback. For example, an output from A may feed
B while B later feeds A. Only the `DependsOn` graph is required to be acyclic; pin wiring is
validated for endpoints, direction, type compatibility, required inputs, and duplicate targets.
This guard is centralized in the pin contract so every current and future node
follows the same runtime rule.

## Logging Policy

User-operation rejections and recoverable editing constraints are logged at `WARN`:

- Trying to drag, delete, add, wire, clear, save, or edit parameters while the workflow is running.
- Connection validation failures such as wrong direction, self-connection, type mismatch, or occupied pins.
- Expected precondition failures, disconnected optional outputs, and user cancellations.

Output dispatch logging is centralized through `BasePinDispatchLogging.SendSampleWithDispatchLog`:
high-frequency forwarding uses `TRACE`, while low-frequency business outputs such as Sender,
process results, and workflow conversion paths may use `INFO`. The helper logs the start before
synchronous downstream delivery and the completion after delivery returns. Pure receiver/sink
nodes do not add dispatch logs because they do not call `SendSample`.

Actual runtime faults remain at `ERROR`:

- Node initialization, run, and stop exceptions.
- Corrupted workflow JSON or failed serialization.
- File read/write and autosave failures.
- Plugin loading failures.
- Unexpected Core, UI, or graph-state exceptions.

## Undo / Redo History

- `MainViewModel` owns bounded, in-memory undo and redo stacks. Each history entry is a
  complete layered graph document produced by `GraphSerializer`, so logical parameters,
  pins, connections, node coordinates, and viewport state are restored consistently.
- Undo and redo are edit operations: they are available only while the workflow is
  stopped. A restore rebuilds the Core graph through `GraphDeserializer` and
  `LoadGraphFromDocument`, then marks the graph dirty and schedules auto-save.
- Structural edits, connection changes, node deletion, parameter updates, and completed
  node drags are recorded as single undo steps. Loading a workflow clears the history
  because the loaded file becomes the new baseline.
- The F6 toolbar action creates a new in-memory Untitled workflow. It clears the editor,
  detaches the current file/store target, disables auto-save, and clears undo/redo history.
  The previous workflow file is never overwritten; an explicit Save As attaches the new
  document to a path and re-enables auto-save.
- The title-bar Undo / Redo buttons bind to `CanUndo` / `CanRedo`. `Ctrl+Z` and `Ctrl+Y`
  use the same commands; focused text boxes keep their native text-editing shortcuts.

## Serialization

- `LogicSerializer` / `LogicDeserializer` in Core handle node logic and parameter
  JSON without GUI data. Startup dependencies are persisted in the logic blob as
  a normalized `dependsOn` array, with legacy scalar values accepted on load.
- `GraphSerializer` / `GraphDeserializer` in the Avalonia project merge logic
  JSON with UI-only data such as coordinates and viewport state.
- Workflow JSON remains the in-memory runtime format. Persisted workflows use the
  encrypted `.rw` container through `WorkflowFileCrypto`; `.rw` files are decrypted
  to JSON before loading and saved by encrypting the completed JSON string.
- Legacy `.json` workflow files remain readable for migration and developer tooling.
  Normal Save, Save As, AutoSave, and CLI runs use `.rw`; plain JSON is only produced
  by explicit developer conversion.

## Build

Plugin binaries are configuration- and platform-specific. AnyCPU builds read from
`plugins/<PluginName>/bin/<Configuration>/net8.0`, while x64 builds read from
`plugins/<PluginName>/bin/x64/<Configuration>/net8.0`. The host build clears and
repopulates its own `plugins` directory after the plugin projects finish building,
so an old copy from another platform/configuration cannot silently be used.

Build the full solution with Visual Studio MSBuild:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
  "AgentFlow.sln" /p:Configuration=Debug /p:Platform=x64 /m:1 /nr:false /nologo /v:m
```

Build the desktop host:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
  "AgentFlow\AgentFlow.Desktop\AgentFlow.Desktop.csproj" `
  /p:Configuration=Debug /p:Platform=x64 /m:1 /nr:false /nologo /v:m
```

Before rebuilding or starting a test run, close the running AgentFlow desktop
process. A loaded plugin DLL is locked on Windows; the build must fail rather than
keep serving an older assembly. After a successful build, the source plugin DLL
and `AgentFlow.Desktop/bin/x64/Debug/net8.0/plugins` copy should have the same
SHA-256 hash. Release x64 follows the same layout under `bin/x64/Release`.

The desktop publish profile uses the `win-x64` runtime identifier and self-contained
single-file publishing. `AgentFlow.Desktop.csproj` therefore declares `win-x64`
in `RuntimeIdentifiers`, so a normal restore produces the runtime-specific assets
required by the publish profile. The published `plugins` directory remains beside
the executable and is refreshed with the same plugin assemblies used by the build.

During publish, the host explicitly builds every plugin project with the active
configuration and platform before copying plugin assemblies. This is required because
Visual Studio's publish pipeline can evaluate project references separately, and it
must not depend on plugin binaries being rebuilt by the host build phase alone.

## Bundled Plugin Skeletons

The following plugins provide the standard construction, parameters, pins, lifecycle,
and receive hooks without implementing final business behavior yet:

| Plugin | Role | Default pins |
| --- | --- | --- |
| `Node.FACTLauncher` | FACT launch entry point with SLOT-aware FSN/IMEI capture, pin-based user-settings coordination, cached CMD/IP/SLOT/PORTHUB state, and launcher-settings broadcasts | `str_slotfsnimei_run`, `input_string_settings`, and `input_int64_ret` inputs; `output_string_host` and `output_string_cmd` outputs |
| `Node.UserSettings` | Standalone INI settings reader that returns section values as JSON for configuration consumers | `input_string_ready` string input; `output_string_slotA/B/C/D/F` string outputs |
| `Node.FACTLogCollector` | FACT log collection | `factId` string input; `log` string output |
| `Node.FACTFlatFileParser` | Parses FACT XML logs and publishes original XML plus normalized JSON with the legacy FlatFile row split into indexed fields | `input_string_path` string input; `output_string_raw_xml` and `output_string_json` string outputs |
| `Node.WorkflowFileConverter` | Developer-only conversion between encrypted `.rw` workflows and readable JSON files | `input_string_rw` and `input_string_json` string inputs; `output_string_json` and `output_string_rw` string outputs |
| `Node.AutoStart` | Workflow start trigger and SLOT router; validates input and routes valid groups to SLOT-specific outputs | `input_string_start` string input; `output_string_readyA/B/C/D/F` string outputs |
| `Node.AutoStop` | Workflow stop trigger with input validation and launcher-stop publication | `input_string_stop` string input; no output pins |
| `Node.DatabaseWriter` | Database persistence sink | `data` string input; `status` string output |
| `Node.ProcessRunner` | Windows command runner using the already-elevated AgentFlow host token; streams stdout/stderr lines to the log panel in real time and publishes the final combined output | `input_string_cmd` string input; `output_string_ret` string output and `output_int64_ret` int64 output |

`Node.UserSettings` owns the fixed `C:\AC710\UserSettings\UserSettings.ini` parser. `Node.FACTLauncher` requests settings through pins and does not contain a duplicate parser.

Business-specific service calls, SQL persistence, and settings transformation remain explicit
`TODO` items in each node implementation.

`Node.AutoStart` buffers semicolon-terminated `SLOT`/`FSN`/`IMEI` JSON groups from
`input_string_start`, validates each group, sends the corresponding JSON group to the output
mapped from `SLOT`, and emits a host-neutral start request through
`IWorkflowStartRequestSink`. A product-specific host registers the service implementation;
the plugin has no Avalonia dependency.

`Node.AutoStop` validates semicolon-terminated groups on `input_string_stop`, publishes the
shared LauncherStop broadcast, and requests a normal workflow stop through
`IWorkflowStopRequestSink`. It has no output pins. The service is implemented by the consuming
product host, not by the generic AgentFlow editor GUI; it does not invoke final application
`Shutdown`.

The TCP demo nodes expose a raw byte-stream interface. `TCP Client` and `TCP Server`
each expose `input_string`, `input_binary`, `output_string`, and `output_binary`.
`TCP Client` additionally exposes `input_string_endpoint` for runtime-only `host:port`
changes without modifying persisted workflow parameters. Text inputs are UTF-8 encoded,
binary inputs are sent unchanged, and received byte blocks are published without adding
framing or automatic replies. See `docs/tcp-transport.md`.




