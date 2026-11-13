# AgentFlow.Core

## Purpose

`AgentFlow.Core` is the GUI-neutral runtime layer for AgentFlow. It loads node
plugins, wires runtime pins, validates workflows, serializes node logic, exposes
the reusable `WorkflowHost` facade, and owns node lifecycle execution.

It must not reference Avalonia or any desktop-specific project.

## Main Modules

- `PluginLoader`: discovers and instantiates node implementations from plugin assemblies.
- `NodeRegistry`: maps registered node type names to descriptors and factories.
- `EditorGraph`: stores editor-facing node and connection state while keeping runtime nodes in Contracts.
- `WorkflowValidation`: checks pins, required inputs, and the acyclic `DependsOn` startup graph.
- `NodeInstanceManager`: coordinates initialization, startup, health monitoring, stop, and shutdown.
- `WorkflowHost`: shared read-only facade for AgentFlow and future product-specific GUIs.
- `WorkflowEngine`: headless execution for CLI and other non-GUI hosts.
- `LogicSerializer` / `LogicDeserializer`: persist node logic and parameters without GUI data.

## Lifecycle Contract

Node lifecycle is intentionally split into explicit phases. Each phase always applies to all
nodes in the current loaded workflow. Validation and state checks are whole-call preconditions;
they never filter which nodes receive lifecycle callbacks.

1. `InitializeAsync` prepares resources in `DependsOn` forward topological layers.
   Nodes in the same layer may initialize concurrently. A later layer starts only
   after all dependency initialization succeeds.
2. `StartAsync` requires a valid initialization for the exact current node set.
   It never initializes nodes itself. A node starts only after all `DependsOn`
   nodes reach `State == Running` and `Running == true`.
3. `StopAsync` calls every node's `Stop` in reverse `DependsOn` topological order.
   `preserveFailure` only controls state retention, not node selection.
4. `ShutdownAsync` calls every node's `Shutdown` in reverse `DependsOn` topological order.

Workflow structure, parameter, pin-wiring, plugin, and document-load changes
invalidate prepared initialization. The next run must initialize again.

Pin connections are runtime data-flow edges and may contain feedback cycles.
Only `DependsOn` controls initialization and startup ordering.

## Build

```powershell
dotnet build 'AgentFlow.Core\AgentFlow.Core.csproj' `
  -c Debug -p:Platform=x64 --no-restore -m:1 -nr:false `
  -p:UseSharedCompilation=false
```

The desktop and CLI hosts consume this project through project references.


