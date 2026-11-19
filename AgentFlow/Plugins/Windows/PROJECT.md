# Windows Native Nodes

Windows-only VC++ nodes use a stable C ABI and a generated managed proxy in
`AgentFlow.Windows.NativeHost.dll`. Native callbacks are copied into a bounded channel and are
dispatched by a serial background pump before any `BasePin.SendSample` call occurs.

## Contract

- `Category=W` identifies Windows-only nodes in Components.
- `Category=E` is reserved for external Worker nodes; `Category=C` is used by cross-platform C# nodes.
- Native DLLs are deployed under `Plugins/Windows/win-x64/<ModuleId>`.
- Native modules never receive managed objects and never pass pointers across callback boundaries.
- `manifest.json` is the source of truth for generated pins and module metadata.
- Native DLLs use static CRT linking: `/MTd` for Debug and `/MT` for Release.

## Example Node

`Node.NativeUpperCase.Windows` receives `input_string`, converts ASCII lowercase characters to
uppercase, and emits the result on `output_string`.

## Build

```powershell
pwsh -File Scripts\build_windows_native.ps1 -Configuration Debug
```

## Adding Another Node

1. Create `Plugins/Windows/Src/Node.<Name>.Windows`.
2. Add the VC++ shared-library project, implementation, and `manifest.json`.
3. Add the project to `Plugins/Windows/Src/CMakeLists.txt` and `AgentFlow.sln`.
4. Build `AgentFlow.Windows.NativeHost`; the pre-compile target runs `NativeNodeCodeGen`.
5. Run `pwsh -File Scripts/build_windows_native.ps1 -Configuration Debug`.
6. Validate exported ABI functions and runtime dependencies with Visual Studio `dumpbin`.
7. Verify there are no `MSVCP*.dll` or `VCRUNTIME*.dll` dependencies.

The managed proxy is generated from the manifest, so pin declarations must stay in sync between
the manifest and the native implementation.