# AgentFlow.Windows.NativeHost

## Purpose

`AgentFlow.Windows.NativeHost` is the managed bridge between AgentFlow's existing plugin loader and
Windows VC++ node DLLs. It exposes generated `BaseNode` proxy classes so `AgentFlow.Core` can keep
using its normal reflection-based discovery without any native-specific changes.

## Runtime Architecture

```text
VC++ callback
  -> NativeHost bounded channel
  -> NativeNodeBackgroundPump
  -> BasePin.SendSample / ILogger
```

Native callbacks never call `BasePin.SendSample` directly and never retain managed or native sample
buffers after returning. Each node has one bounded event queue and one serial background pump.

## Source Layout

- `Plugins/Windows/Src/AgentFlow.NativeNodeApi/agentflow_node_api.h`: stable C ABI.
- `Plugins/Windows/Src/Node.*.Windows/`: VC++ node source and `manifest.json`.
- `Tools/NativeNodeCodeGen`: generates `Generated/NativeNodeProxies.g.cs`.
- `Plugins/Windows/win-x64/<ModuleId>/`: deployed native DLL and manifest.

## Build

```powershell
pwsh -File Scripts\build_windows_native.ps1 -Configuration Debug
dotnet run --project Tools\NativeNodeCodeGen -- <workspace-root> <manifest-root> <output-file>
dotnet build Plugins\Windows\AgentFlow.Windows.NativeHost\AgentFlow.Windows.NativeHost.csproj -c Debug -p:Platform=x64
```

The Desktop and CLI build pipelines copy `Plugins/Windows/win-x64` into the host output
`Plugins/Windows/win-x64` directory.

## Generated proxies

`Generated/NativeNodeProxies.g.cs` is generated from every native `manifest.json`. The project
reference to `Tools/NativeNodeCodeGen` and the `GenerateNativeNodeProxies` target run the
generator before `CoreCompile`, preventing a stale proxy after a manifest or native project is
added.

Proxy generation does not replace native compilation. The VC++ DLL is built separately by
`Scripts/build_windows_native.ps1`, which the Desktop and CLI host builds invoke for Windows x64.