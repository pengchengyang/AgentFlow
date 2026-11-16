# AgentFlow.Contracts

## Purpose

`AgentFlow.Contracts` defines GUI-neutral runtime contracts shared by Core, plugins, and
host applications. It has no Avalonia dependency.

## Main Modules

- `BaseNode`: lifecycle state, parameters, pins, dependency ids, and serialization hooks.
- `BasePin`: typed input/output ports, runtime wiring, sample delivery, and connection guards.
- `Sample`: typed values exchanged between pins.
- `WorkflowFileCrypto`: encrypted `.rw` persistence with atomic replacement and `.bak` recovery.
- `INodeContext` and service contracts: logger, input/output access, and host services.

## Persistence Safety

Encrypted and plain workflow writes use a unique temporary file, flush the payload to disk,
atomically replace the destination, and retain the previous file as a sibling `.bak` copy.
Reads fall back to the backup only when the primary workflow file is missing. The `.rw` format
remains an encrypted container around the in-memory JSON workflow document.

## Build

```powershell
dotnet build 'AgentFlow.Contracts\AgentFlow.Contracts.csproj' `
  -c Debug --no-restore -v:minimal
```
