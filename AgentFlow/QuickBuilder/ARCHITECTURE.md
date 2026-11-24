# QuickBuilder

QuickBuilder is a Windows C++17 command-line packaging orchestrator for the AgentFlow binary SDK.

## Goals

- Reproduce the SDK package structure produced by `package-sdk-dll.py`.
- Build Debug and Release SDK variants in parallel.
- Perform a fresh full rebuild on every invocation.
- Keep every generated artifact, intermediate file, cache, log-adjacent temp file, and final ZIP under `artifacts-quick` beside `QuickBuilder.exe`.
- Avoid reusing project-local `bin`, `obj`, MSBuild, NuGet, Python, and compiler caches from previous runs.

## Main Flow

1. Locate the repository root from the current directory or executable location.
2. Select `artifacts-quick` beside `QuickBuilder.exe`, then delete and recreate it.
3. Redirect temp, NuGet, .NET, Python, and pip caches into `artifacts-quick`.
4. Query target frameworks with MSBuild node reuse disabled.
5. Build Shared DLLs, C# plugins, Windows native nodes, NativeHost, NativeNodeCodeGen, and external-language node packages through a bounded worker pool.
6. Verify Release external packages.
7. Assemble `artifacts-quick/AgentFlow.Sdk.Binaries` using the same SDK layout as the Python packager.
8. Generate `BinaryVersions.json` and the ZIP archive.

## Output Layout

The following directory is created beside `QuickBuilder.exe`:

```text
artifacts-quick/
  AgentFlow.Sdk.Binaries/
  AgentFlow.Sdk.Binaries.zip
  sdk-dll-build/
  External/
  External-Build/
  temp/
  nuget-packages/
  dotnet-home/
  pip-cache/
  python-pycache/
```

The ZIP contains files rooted at `AgentFlow.Sdk.Binaries/`, matching the Python packager.

## Build

```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' `
  'QuickBuilder\QuickBuilder\QuickBuilder.vcxproj' `
  /p:Configuration=Release /p:Platform=x64 /m /v:minimal /nologo
```

## Run

The Release build writes `QuickBuilder.exe` to the repository root. Double-click it to run a full package build with automatic worker selection. The console stays open at completion so the final path or error can be read.

Command-line runs are still supported:

```powershell
& '.\QuickBuilder.exe' --jobs 10
```

Use `--skip-external` only for diagnostics. Normal SDK packaging must include external packages.
