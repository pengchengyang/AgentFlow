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
| `AgentFlow.Shared` | GUI-neutral shared declarations grouped per concrete project (for example `RTS/CFG/Broadcasts`, `RTS/CFG/Errors`, `RTS/CFG/Results`), including error/result contracts and broadcast subscription names. |
| `Plugins/*` | Dynamically loaded node implementations and demo nodes. |
| `BroadcastKit*` | Optional process-local broadcast transport and shared topic/message contracts, including ordered sample batches, keyed Sample dictionaries, keyed Sample-list dictionaries, and correlated request/reply. |

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
  initialize/start/stop execution, execute node commands, and read per-node health.
  `InitializeAsync` prepares node resources; `StartAsync` only launches a node set
  that is currently initialized.
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

`BroadcastTopicMessage` also supports `SamplesByKey` for one Sample per string key and
`SampleListsByKey` for an ordered Sample list per string key. TopicBus transports both
representations without interpreting key names, Sample types, ordering, or precedence.
Publishers and subscribers own the topic contract and malformed-payload handling. Both
dictionaries and nested lists are copied to read-only snapshots when the envelope is created.
The bus does not inspect payloads; business meaning remains defined by topic contracts.

## CLI Multi-Instance Execution

`AgentFlow.Cli` runs every configured workflow instance independently in one
process. A workflow may be repeated, and different workflow files may be mixed:

```powershell
AgentFlow.Cli run.workflow.json --instances 5

AgentFlow.Cli --plugin-dir Plugins `
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

`NodeInstanceManager` separates lifecycle preparation from execution. Each lifecycle method
always covers every node in the current loaded workflow; state checks are preconditions for the
whole call and never select a subset of nodes:

- `InitializeAsync` initializes nodes in `DependsOn` forward topological layers.
  Nodes in the same layer may initialize concurrently. A later layer starts only
  after every dependency in the earlier layer initialized successfully.
  Initialization never calls `Run()` and leaves nodes `Idle`.
- `StartAsync` requires valid initialization for the exact current node set. It
  rejects stale, missing, or changed initialization instead of silently preparing
  again. It then launches every current node in dependency order, and a dependent
  node starts only after every dependency has reached `State == Running` and
  `Running = true`.
- `StopAsync` calls `Stop` for every current node in reverse `DependsOn` topological
  order, so dependents stop before their dependencies. Node state is not used to
  skip callbacks; `preserveFailure` only controls post-stop state retention.
- `ShutdownAsync` calls `Shutdown` for every current node in reverse `DependsOn`
  topological order.
- Workflow structure, parameters, bindings, and loaded documents invalidate the
  prepared initialization state. The next run must call `InitializeAsync` again.

`NodeInstanceManager` additionally enforces the runtime contract:

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
- Root nodes in the same initialization layer may initialize and start concurrently.
- Multi-dependency nodes initialize only after all dependencies initialized, and start
  once after the final dependency reaches `Running = true`.
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
- `F4` and `Ctrl+S` bind to the normal Save command. A workflow with an associated file path
  is written in place; Save As opens only for a truly detached Untitled document. `F5` remains
  the explicit Save As shortcut. Startup auto-load also associates a local workflow with its
  normalized `.rw` path so the first Save updates that file without prompting.

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

## Stability Safeguards

- Editor persistence supports draft auto-save even when the graph is temporarily not runnable.
  Run-time validation remains the authority for launch preflight checks.
- Workflow writes use a unique temporary file in the destination directory, flush to disk,
  replace the destination atomically, and retain the previous workflow as `<file>.bak`.
- Workflow loading validates the complete document through `EditorJsonLoader` before the
  live graph is cleared, so malformed JSON, unknown node types, invalid pin ids, and bad
  connections do not destroy the currently loaded graph.
- Plugin discovery is staged in a temporary registry and external Worker runtime. The live
  node catalog and Worker runtime are swapped only after the scan completes.
- External node release has a bounded cooperative timeout. Unresponsive Worker processes are
  force-terminated by process id and reported at `ERROR`.
- UI log retention is bounded to 2000 entries and 50 retained errors per run.
- Desktop installs handlers for Avalonia dispatcher, AppDomain, and unobserved task exceptions;
  fatal failures are written to the rolling log established by `AppLog`.
- `Node.ProcessRunner` always attempts temporary-file cleanup and terminates the active process
  tree on cancellation or failure.
## Build

Plugin binaries are configuration- and platform-specific. AnyCPU builds read from
`Plugins/<PluginName>/bin/<Configuration>/net8.0`, while x64 builds read from
`Plugins/<PluginName>/bin/x64/<Configuration>/net8.0`. The host build clears and
repopulates its own `Plugins` directory after the plugin projects finish building,
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
and `AgentFlow.Desktop/bin/x64/Debug/net8.0/Plugins` copy should have the same
SHA-256 hash. Release x64 follows the same layout under `bin/x64/Release`.

The desktop publish profile still defaults to `win-x64` and self-contained
single-file publishing. `AgentFlow.Desktop.csproj` and `AgentFlow.Cli.csproj`
declare the standard RIDs `win-x64;win-arm64;linux-x64;linux-arm64;linux-arm`
so runtime-specific assets can be restored for the target host. External-language
packages are copied to `Plugins/External/<RID>` and the current process only scans
the matching RID directory. The published `Plugins` directory remains beside the
executable and is refreshed with the same plugin assemblies used by the build.

During publish, the host explicitly builds every plugin project with the active
configuration and platform before copying plugin assemblies. This is required because
Visual Studio's publish pipeline can evaluate project references separately, and it
must not depend on plugin binaries being rebuilt by the host build phase alone.

### Binary Version Metadata

All AgentFlow-owned DLL and EXE projects have their own `Version.props` file.
`Directory.Build.props` supplies shared metadata and imports the nearest project
version file:

- `Company`: `Rolling Wireless, SARL.`
- `Product`: `Agent Flow Node Editor`
- `Copyright`: `(c) Rolling Wireless, SARL.  All rights reserved.`
- `AssemblyTitle` and `Description`: derived from the project name.

Each `Version.props` owns `AssemblyVersion`, `FileVersion`, and
`InformationalVersion`. A change to one component therefore does not modify the
version of any other component. `Directory.Build.targets` fails compilation when a
project is missing its version file or required version fields.

The packaging scripts validate every AgentFlow-owned DLL and EXE and write
`BinaryVersions.json` into the package root. Validation checks company, product,
copyright, file version, product version, file description, original filename, and
SHA-256. The verifier is `build/Verify-BinaryVersions.ps1`.

### SDK Packaging

Two SDK packaging scripts are kept for different distribution models:

- `package-sdk-src.bat` builds the source-based SDK. It copies the public shared project
  sources under `src` and packages plugin assemblies separately for Debug and Release.
- `package-sdk-dll.py` builds the binary SDK. It does not include AgentFlow platform
  source code, but it includes the minimal `Demo.Cli` and `Demo.Desktop` runner source
  projects. External projects reference the prebuilt assemblies under `SharedDlls`.

The binary SDK layout is configuration-isolated:

```text
AgentFlow.Sdk.Binaries/
├─ SharedDlls/
│  └─ x64/
│     ├─ Debug/
│     │  ├─ *.dll / *.pdb
│     │  └─ Plugins/
│     │     └─ External/<RID>/
│     └─ Release/
│        ├─ *.dll
│        └─ Plugins/
│           └─ External/<RID>/
├─ Demo.Cli/
└─ Demo.Desktop/
```

`SharedDlls/x64/<Configuration>/Plugins` contains C# node plugin assemblies; external
language packages are under `Plugins/External/<RID>`; supported standard RIDs are
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, and `linux-arm`. Debug
plugin builds also include PDB files, while Release plugin builds exclude them.
`Node.Broadcast.Demo` remains Debug-only. `SharedDlls/x64/<Configuration>` contains the
shared AgentFlow assemblies, plugin runtime dependencies, their PDB files in Debug,
and the NuGet runtime dependencies required to compile and run against the SDK.
When packaging on Windows, `package-sdk-dll.py` also includes Windows native node binaries,
`AgentFlow.Windows.NativeHost.dll`, the native ABI header, and the NativeNodeCodeGen tool.

`Demo.Cli` and `Demo.Desktop` are minimal source projects for opening and running
workflows; they intentionally do not include graph editing. They reference the SDK
configuration selected by their build (`Debug` or `Release`) and copy `Plugins` beside
their output. Consumers must use matching Debug or Release assemblies and must not copy
the shared assemblies into the plugin folder; keeping one central copy preserves shared
type identity and process-local services such as the broadcast bus.

The binary package is written to
`artifacts/AgentFlow.Sdk.Binaries.zip` by running:

```powershell
python .\package-sdk-dll.py
```

## Windows Native Nodes

Windows-only VC++ nodes use a stable C ABI and a generated managed proxy, without changing
AgentFlow.Core's reflection-based plugin discovery.

```text
VC++ node DLL
  -> AfNode C ABI
  -> AgentFlow.Windows.NativeHost.dll
  -> bounded Channel per node (capacity 1024)
  -> NativeNodeBackgroundPump
  -> BasePin.SendSample / ILogger / workflow ITopicBus

workflow ITopicBus
  -> NativeHost subscription handler
  -> bounded Channel per node
  -> NativeNodeBackgroundPump
  -> VC++ broadcast receive callback
```

- Native source lives under `Plugins/Windows/Src`.
- Windows native builds prefer the repository-portable `cmake-*/bin/cmake.exe`, fall back to PATH, and select the installed Visual Studio 18/17 generator dynamically.
- The native build is serialized across all `PluginHost` projects by a shared file lock under `artifacts/WindowsNative`. A shared source fingerprint stamp lets later hosts reuse the completed DLL instead of reconfiguring CMake concurrently.
- `AgentFlow.NativeNodeApi/agentflow_node_api.h` defines the ABI.
- `AgentFlow.NativeNodeApi/agentflow_broadcast_helpers.h` provides C++ helpers for the five
  canonical broadcast payload shapes.
- Each module has a VC++ project, implementation, and `manifest.json`.
- `Tools/NativeNodeCodeGen` reads manifests and regenerates `NativeNodeProxies.g.cs` before
  `AgentFlow.Windows.NativeHost` compiles.
- `AgentFlow.Windows.NativeHost.dll` must remain a top-level file under `Plugins` so the
  existing plugin loader can discover it.
- Native binaries and manifests deploy under `Plugins/Windows/win-x64/<ModuleId>`.
- `Category=C/W/E` identifies common nodes, Windows-only nodes, and external Worker nodes.
  Components displays only the `W` or `E` corner badge; `C` has no badge.
- Native broadcasts use `topic + payloadKind + payloadJson` through `publishBroadcast`,
  `subscribeBroadcast`, and `unsubscribeBroadcast` in `AfNodeHostVTable`.
- The vtable extension appends fields at the end and uses `structSize` for compatibility; older
  DLLs can ignore the broadcast tail.
- Callbacks may copy and enqueue data only. They must not call `BasePin` directly or retain
  borrowed sample or broadcast buffers after returning.
- A full callback queue returns `AF_ERR_QUEUE_FULL`; native code must handle that status without
  blocking indefinitely.
- Debug and Release native DLLs use static CRT linking through `/MTd` and `/MT` respectively.
- `package-sdk-dll.py` includes NativeHost, native node binaries, the ABI header, and the
  NativeNodeCodeGen tool when packaging on Windows.

The example `Node.NativeUpperCase.Windows` converts ASCII lowercase characters to uppercase
from `input_string` and publishes the result on `output_string`.
## Bundled Plugin Skeletons

The following plugins provide the standard construction, parameters, pins, lifecycle,
and receive hooks without implementing final business behavior yet:

| Plugin | Role | Default pins |
| --- | --- | --- |
| `Node.FACTLauncher` | FACT launch entry point with SLOT-aware FSN/IMEI capture, user-settings coordination, raw RET forwarding, newest matching flat-file XML path publication, and BUSINESS host payload publication whose `STATUS` is the decimal RET code (`0` passes, any positive value fails; a negative RET is logged as an error and not published) with post-dispatch FSN/IMEI cache clearing, successful position-keyed command broadcasting, cached CMD/IP/SLOT/PORTHUB state, and launcher-settings broadcasts | `str_slotfsnimei_run`, `input_string_settings`, and `input_int64_ret` inputs; `output_string_host`, `output_string_cmd`, `output_int64_ret`, and `out_str_flatfilepath` outputs |
| `Node.UserSettings` | Consumes `Node.ReadIniFile` JSON, validates `GUID`, publishes the GUID once through a dedicated pin, publishes settings JSON without GUID through `output_string_settings`, broadcasts the keyed snapshot to GUI/CFG consumers, and publishes runtime endpoint/SLOT outputs when settings are accepted | `input_string_settings` string input; `output_string_slotA/B/C/D/F`, `output_string_endpoint`, `output_string_settings`, and `output_string_guid` string outputs |
| `Node.ReadIniFile` | Generic INI reader supporting section-content and standard key/value styles; runtime input path overrides the `IniPath` parameter and successful reads publish a compact flat JSON dictionary whose values are arrays | `input_string_path` string input; `output_string_sample` string output |
| `Node.FACTLogCollector` | FACT log collection | `factId` string input; `log` string output |
| `Node.FACTFlatFileParser` | Parses FACT XML logs and publishes original XML plus normalized JSON with the legacy FlatFile row split into indexed fields | `input_string_path` string input; `output_string_raw_xml` and `output_string_json` string outputs |
| `Node.PriFileParser` | Resolves direct PRI paths or `/P` paths from FACT commands, parses PRI configuration files into a JSON dictionary, publishes it through `output_string_json` when connected, and broadcasts the keyed dictionary on `Pri.File.Settings.00850D6A-1F19-4040-B2D2-BA4E3E8E0F93` | `input_string_path` string input; `output_string_json` string output |
| `Node.PassFailTotalYield` | Maintains per-slot pass/fail/yield counters in `C:\AC710\UserSettings\Yield.ini` and broadcasts keyed slot and aggregate calculations; `0` records a pass and any non-zero RET records a fail | `input_int64_retA/B/C/D/F` INT64 inputs; no output pins; GUI reset via `CFGPassFailTotalYieldBroadcasts.CFGReset` |
| `Node.WorkflowFileConverter` | Developer-only conversion between encrypted `.rw` workflows and readable JSON files | `input_string_rw` and `input_string_json` string inputs; `output_string_json` and `output_string_rw` string outputs |
| `Node.MessagesDispatcher` | Workflow start trigger and SLOT router; validates exactly five `MESSAGES` records, routes only `STATUS=TEST` items to SLOT-specific outputs, and requests workflow start without handling RUN handshakes | `in_string_messages` string input; `output_string_readyA/B/C/D/F` string outputs |
| `Node.AutoRunStop` | Handles workflow RUN and DISCONNECT controls; performs the launcher start/reply handshake for RUN, emits `RUN_ACK`, and publishes the stop broadcast plus host stop request for DISCONNECT | `input_string_sample` string input; `output_string_sample` string output |
| `Node.DatabaseWriter` | Database persistence sink | `data` string input; `status` string output |
| `Node.ProcessRunner` | Windows command runner using the already-elevated AgentFlow host token; streams stdout/stderr lines to the log panel in real time, applies millisecond start/execution timeouts, and publishes the final combined output | `input_string_cmd` string input; `output_string_ret` string output and `output_int64_ret` int64 output |
| `Node.Heartbeat` | Immediately forwards each input sample and repeatedly publishes a cloned copy of the latest sample on a fixed interval | `input` and `output`; configurable data type and `IntervalMilliseconds` |
| `Node.CFGSHandler` | Builds CFG-1/CFG-2 BUSINESS envelopes from SLOT/FSN/IMEI batches, retains the complete Read INI File dictionary, prepares a local PowerShell startup command for CFG-1, runs the local CFG-1 query/kill/verify PID sequence, and runs the remote CFG-2 PsExec query/kill/verify sequence with up to three verification attempts at 750 ms intervals; it also publishes one `CFGSTestLaunch` broadcast per `STATUS=TEST` record of each launch batch it sends and one `CFGSTestResult` broadcast per BUSINESS result record it receives from a CFG | `input_str_message`, `in_str_from_upper`, `in_str_user_settings`, `str_start_ret1`, `int64_start_ret1`, `str_sure_exit1`, `int64_sure_exit1`, `str_start_ret2`, `int64_start_ret2`, `str_sure_exit2`, `int64_sure_exit2`; `cmd_start_cfg1`, `cmd_start_cfg2`, `cmd_sure_exit_cfg1`, `cmd_sure_exit_cfg2`, `output_string_message` |

`Node.UserSettings` consumes the array-valued JSON dictionary produced by `Node.ReadIniFile`; it no longer reads the fixed INI file itself. On a valid payload it requires a `GUID` section, normalizes the first valid GUID to uppercase `8-4-4-4-12` form, caches the settings, publishes runtime settings/endpoint/SLOT/GUID outputs when running, publishes the remaining keyed `SampleListsByKey` snapshot to GUI consumers through `CFGLauncherSettings`, and publishes representative keyed values through `CFGUserSettingsGuid`. Initialization tolerates settings that have not arrived yet, while invalid JSON or an invalid GUID stops the node. `Node.FACTLauncher` requests settings through pins and does not contain a duplicate parser.

`Node.PassFailTotalYield` subscribes to `CFGPassFailTotalYieldBroadcasts.CFGReset` (`Pass.Fail.Total.Yield.Reset.70CBA995-4A34-4CC8-8789-F0E29F5058C0`) during `Initialize` and retains the subscription until `Shutdown`, independent of `Run`/`Stop`. Reset payloads contain `A/B/C/D/F` keys with exact `StringSample` values of `Reset`; valid requests clear only selected slots, persist `Yield.ini`, then publish a complete `Pass.Fail.Total.Yield.0314C9D4-964F-4520-AC02-8E1A8729754A` snapshot. Input pins accept only INT64 RET results and do not accept reset commands.

Business-specific service calls, SQL persistence, and settings transformation remain explicit
`TODO` items in each node implementation.

`Node.MessagesDispatcher` receives one complete JSON object from `in_string_messages`, requires exactly
five `MESSAGES` records from either SLOT 1-5 or SLOT 6-10, and validates every
`SLOT`/`FSN`/`IMEI`/`STATUS` business message. Only `STATUS=TEST` records are sent as compact
item JSON to the output mapped from `SLOT`; `STATUS=IDLE` records log a warning and produce no
output. After all TEST records are dispatched, a batch with at least one TEST record emits one
host-neutral start request through `IWorkflowStartRequestSink`. The outer `MESSAGES` wrapper is
never forwarded. A product-specific host registers the service implementation; the plugin has
no Avalonia dependency.

`Node.AutoRunStop` accepts one complete JSON string on `input_string_sample`, strips optional
leading/trailing whitespace and semicolons, and parses the value as a JSON object. A valid
`RUN` stores `GUID`, `LOCATION`, `VERSION`, and `SEQ`, publishes `CFGLauncherStart` with
`StringSample` value `Run`, waits for `CFGLauncherStartReply` value `Running`, then emits a
correlated `RUN_ACK` through `output_string_sample`. The configurable
`RunReplyTimeoutMilliseconds` defaults to `60000`; timeout discards the pending RUN without a
successful acknowledgement. A valid `DISCONNECT` publishes the shared Auto Run Stop broadcast and
requests a normal workflow stop through `IWorkflowStopRequestSink`. Invalid JSON, a non-object
root, a missing or non-string `TYPE`, and every other `TYPE` value are logged as warnings and
ignored. It does not split semicolon-delimited frames or buffer partial input. The service is
implemented by the consuming product host, not by the generic AgentFlow editor GUI; it does not
invoke final application `Shutdown`.

The CFG/CFGS session nodes add protocol-level framing and identity on top of TCP:

- `Node.CFGSTcpServer` accepts the flat `Node.ReadIniFile` JSON on
  `input_string_client_config`, reads `GUID.CFG-1` and `GUID.CFG-2` values to map clients to their
  locations, and reads `PORT.LISTEN` as an optional runtime listen-port source. It supports `Immediate` and
  `WaitForEndpoint` start modes, keeps `Running = true` while waiting, accepts an optional
  integer port on `input_string_endpoint`, loads only the first client configuration, authenticates
  one active session per configured GUID, validates outgoing BUSINESS payloads,
  routes envelopes by target GUID, maintains bounded offline queues and PING/PONG heartbeats,
  and emits client BUSINESS envelopes through `output_string_message`.

- `Node.CFGTcpClient` authenticates with its configured `ClientGuid`, obtains the authoritative
  `LOCATION` from `AUTH_OK`, supports `Immediate` and `WaitForEndpoint` connection modes,
  reconnects automatically, retains the last authenticated location for offline queueing,
  infers the location from the fixed SLOT group before first authentication, validates inbound
  target GUIDs and BUSINESS payloads, forwards authenticated RUN envelopes through
  `output_string_message`, and accepts AutoRunStop-produced `RUN_ACK` envelopes on
  `input_string_message` for unchanged transmission to CFGS.
- `Node.CFGConfimer` receives the current local GUID on `input_string_local_guid` and one
  complete envelope on `input_string_messages`. It validates the target GUID and `LOCATION`.
  A `BUSINESS` envelope also requires a strictly increasing `TIMESTAMP`, a JSON-object `DATA`,
  and the fixed five-record identity contract, then emits `{"MESSAGES":[...]}` through
  `output_string_messages`. Any other `TYPE` emits the complete original envelope through
  `output_string_others`. Any validation failure logs an error and stops the node from running.
- The CFG/CFGS session nodes use `;JSON;` framing and validate top-level `GUID`, `LOCATION`,
  `TYPE`, `VERSION`, `SEQ`, `TIMESTAMP`, and `DATA` fields. Every BUSINESS `DATA.MESSAGES`
  record contains `SLOT`, `FSN`, `IMEI`, and `STATUS`, all as JSON strings. A launch batch
  (CFGS to CFG) holds five records for one location, SLOT 1-5 for `CFG-1` and SLOT 6-10 for
  `CFG-2`, with `STATUS` `IDLE` or `TEST`. A result frame (CFG to CFGS) holds one record per
  completed slot whose `STATUS` is the decimal RET code: `0` is a pass and any positive value
  is a failure code. The session nodes accept `IDLE`, `TEST`, or a non-negative integer.
- Connection status, per-slot test launches (`CFGSTestLaunch`), and per-slot test results
  (`CFGSTestResult`) are published through the shared topics in
  `AgentFlow.Shared/RTS/CFGS/Broadcasts/CFGSTcpProtocolBroadcasts.cs`.

The TCP demo nodes expose a raw byte-stream interface. `TCP Client` and `TCP Server`
each expose `input_string`, `input_binary`, `output_string`, and `output_binary`.
`TCP Client` additionally exposes `input_string_endpoint` for runtime-only `host:port`
changes without modifying persisted workflow parameters. Text inputs are UTF-8 encoded,
binary inputs are sent unchanged, and received byte blocks are published without adding
framing or automatic replies. See `docs/tcp-transport.md`.