#!/usr/bin/env python3
"""Build and package the AgentFlow binary SDK."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
ARTIFACTS = ROOT / "artifacts"
STAGING = ARTIFACTS / "AgentFlow.Sdk.Binaries"
BUILD_ROOT = ARTIFACTS / "sdk-dll-build"
ZIP_PATH = ARTIFACTS / "AgentFlow.Sdk.Binaries.zip"

SHARED_PROJECTS = (
    "AgentFlow.Contracts",
    "AgentFlow.Core",
    "BroadcastKit",
    "BroadcastKit.Contracts",
    "AgentFlow.Shared",
    "Hosting.Logging",
)
REQUIRED_SHARED_ASSEMBLIES = (
    "AgentFlow.Contracts.dll",
    "AgentFlow.Core.dll",
    "BroadcastKit.dll",
    "BroadcastKit.Contracts.dll",
    "AgentFlow.Shared.dll",
    "Hosting.Logging.dll",
)


SUPPORTED_RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-arm")
WINDOWS_NATIVE_SOURCE = ROOT / "Plugins" / "Windows" / "Src"
WINDOWS_NATIVE_OUTPUT = ROOT / "Plugins" / "Windows" / "win-x64"
WINDOWS_NATIVE_BUILD_SCRIPT = ROOT / "Scripts" / "build_windows_native.ps1"
WINDOWS_NATIVE_HOST_PROJECT = (
    ROOT / "Plugins" / "Windows" / "AgentFlow.Windows.NativeHost" /
    "AgentFlow.Windows.NativeHost.csproj"
)
WINDOWS_NATIVE_API_HEADER = (
    WINDOWS_NATIVE_SOURCE / "AgentFlow.NativeNodeApi" / "agentflow_node_api.h"
)
NATIVE_NODE_CODEGEN_PROJECT = ROOT / "Tools" / "NativeNodeCodeGen" / "NativeNodeCodeGen.csproj"
DEMO_PROJECTS = ("Demo.Cli", "Demo.Desktop")


def log(message: str) -> None:
    print(message, flush=True)


def host_rid() -> str:
    system = platform.system().lower()
    machine = platform.machine().lower()
    if system == "windows" and machine in {"amd64", "x86_64"}:
        return "win-x64"
    if system == "windows" and machine in {"arm64", "aarch64"}:
        return "win-arm64"
    if system == "linux" and machine in {"amd64", "x86_64"}:
        return "linux-x64"
    if system == "linux" and machine in {"arm64", "aarch64"}:
        return "linux-arm64"
    if system == "linux" and machine in {"arm", "armv7l", "armv8l", "armhf"}:
        return "linux-arm"
    raise RuntimeError(f"Unsupported SDK build host: {platform.system()} {platform.machine()}")


def run(command: list[str | Path], *, cwd: Path = ROOT) -> None:
    rendered = " ".join(str(part) for part in command)
    log(f"  > {rendered}")
    completed = subprocess.run(
        [str(part) for part in command],
        cwd=str(cwd),
        text=True,
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"Command failed with exit code {completed.returncode}: {rendered}")


def require_tool(name: str) -> Path:
    found = shutil.which(name)
    if not found:
        raise RuntimeError(f"{name} was not found in PATH.")
    return Path(found)


def find_visual_studio_msbuild() -> Path | None:
    """Locate the Visual Studio MSBuild used for Windows native-host packaging."""
    program_files_x86 = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"))
    vswhere = program_files_x86 / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
    if vswhere.is_file():
        completed = subprocess.run(
            [
                str(vswhere), "-latest", "-products", "*",
                "-requires", "Microsoft.Component.MSBuild",
                "-find", r"MSBuild\**\Bin\MSBuild.exe",
            ],
            cwd=str(ROOT),
            text=True,
            capture_output=True,
            check=False,
        )
        if completed.returncode == 0:
            for line in completed.stdout.splitlines():
                candidate = Path(line.strip())
                if candidate.is_file():
                    return candidate

    for edition in ("Enterprise", "Professional", "Community", "BuildTools"):
        candidate = (
            Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
            / "Microsoft Visual Studio" / "2022" / edition
            / "MSBuild" / "Current" / "Bin" / "MSBuild.exe"
        )
        if candidate.is_file():
            return candidate
    return None


def msbuild_property(project: Path, property_name: str) -> str:
    completed = subprocess.run(
        ["dotnet", "msbuild", str(project), "-nologo", f"-getProperty:{property_name}"],
        cwd=str(ROOT),
        text=True,
        capture_output=True,
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(completed.stderr.strip() or completed.stdout.strip() or
                           f"Failed to read {property_name} from {project}.")
    value = completed.stdout.strip()
    if not value:
        raise RuntimeError(f"{property_name} is empty for {project}.")
    return value


def build_shared(configuration: str, plugin_framework: str) -> None:
    destination = STAGING / "SharedDlls" / "x64" / configuration
    destination.mkdir(parents=True, exist_ok=True)
    log("")
    log(f"Building and packaging {configuration} Shared assemblies...")
    for project_name in SHARED_PROJECTS:
        project = ROOT / project_name / f"{project_name}.csproj"
        if not project.is_file():
            raise FileNotFoundError(f"Shared project not found: {project}")
        output = BUILD_ROOT / "Shared" / configuration / project_name
        if output.exists():
            shutil.rmtree(output)
        run([
            "dotnet", "build", project, "-c", configuration, "-p:Platform=x64",
            "-p:CopyLocalLockFileAssemblies=true", "-o", output, "-v:minimal",
        ])
        for file in output.glob("*.dll"):
            shutil.copy2(file, destination / file.name)
        if configuration == "Debug":
            for file in output.glob("*.pdb"):
                shutil.copy2(file, destination / file.name)
    for name in REQUIRED_SHARED_ASSEMBLIES:
        if not (destination / name).is_file():
            raise FileNotFoundError(f"Required Shared assembly is missing: {destination / name}")


def build_plugins(configuration: str) -> None:
    source_root = ROOT / "Plugins"
    destination = STAGING / "SharedDlls" / "x64" / configuration / "Plugins"
    destination.mkdir(parents=True, exist_ok=True)
    log("")
    log(f"Building and packaging {configuration} C# plugins...")
    for directory in sorted(source_root.iterdir(), key=lambda path: path.name.lower()):
        if not directory.is_dir():
            continue
        project = directory / f"{directory.name}.csproj"
        if not project.is_file():
            continue
        if configuration == "Release" and directory.name == "Node.Broadcast.Demo":
            log(f"  [Release] skip debug-only plugin {directory.name}")
            continue
        output = BUILD_ROOT / "Plugins" / configuration / directory.name
        if output.exists():
            shutil.rmtree(output)
        run([
            "dotnet", "build", project, "-c", configuration, "-p:Platform=x64",
            "-p:CopyLocalLockFileAssemblies=true", "-o", output, "-v:minimal",
        ])
        plugin_dll = output / f"{directory.name}.dll"
        if not plugin_dll.is_file():
            raise FileNotFoundError(f"Plugin assembly was not produced: {plugin_dll}")
        shutil.copy2(plugin_dll, destination / plugin_dll.name)
        for dependency in output.glob("*.dll"):
            if dependency.name != plugin_dll.name:
                shutil.copy2(dependency, STAGING / "SharedDlls" / "x64" / configuration / dependency.name)
        if configuration == "Debug":
            pdb = output / f"{directory.name}.pdb"
            if pdb.is_file():
                shutil.copy2(pdb, destination / pdb.name)


def package_windows_native_nodes(configuration: str) -> None:
    if platform.system() != "Windows":
        log("")
        log(f"Skipping Windows native nodes for {configuration}: build host is not Windows.")
        return

    powershell = find_powershell()
    if not powershell:
        raise RuntimeError("PowerShell was not found; Windows native node packaging requires it.")
    if not WINDOWS_NATIVE_BUILD_SCRIPT.is_file():
        raise FileNotFoundError(f"Windows native build script was not found: {WINDOWS_NATIVE_BUILD_SCRIPT}")

    log("")
    log(f"Building {configuration} Windows native nodes...")
    run([
        powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
        WINDOWS_NATIVE_BUILD_SCRIPT, "-Configuration", configuration,
    ])

    if not WINDOWS_NATIVE_OUTPUT.is_dir():
        raise FileNotFoundError(f"Windows native output was not found: {WINDOWS_NATIVE_OUTPUT}")

    native_destination = STAGING / "SharedDlls" / "x64" / configuration / "Plugins" / "Windows" / "win-x64"
    if native_destination.exists():
        shutil.rmtree(native_destination)
    native_destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(WINDOWS_NATIVE_OUTPUT, native_destination)
    log(f"  copied Windows native modules: {native_destination}")

    # Build NativeHost into its normal project output. Passing a shared -o output directory
    # here can make the project-reference restore fail without diagnostics on some SDK/MSBuild
    # combinations, so the packaging script discovers the produced assembly by configuration.
    msbuild = find_visual_studio_msbuild()
    if msbuild is not None:
        run([
            msbuild, WINDOWS_NATIVE_HOST_PROJECT,
            f"/p:Configuration={configuration}",
            "/p:Platform=x64",
            "/p:CopyLocalLockFileAssemblies=true",
            "/v:minimal",
            "/nologo",
        ])
    else:
        run([
            "dotnet", "build", WINDOWS_NATIVE_HOST_PROJECT, "-c", configuration,
            "-p:Platform=x64", "-p:CopyLocalLockFileAssemblies=true",
            "-v:minimal",
        ])
    native_host_dlls = sorted(
        WINDOWS_NATIVE_HOST_PROJECT.parent.glob(
            f"bin/x64/{configuration}/net*/AgentFlow.Windows.NativeHost.dll"
        ),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    if not native_host_dlls:
        raise FileNotFoundError(
            "NativeHost assembly was not produced under "
            f"{WINDOWS_NATIVE_HOST_PROJECT.parent / 'bin' / 'x64' / configuration}."
        )
    native_host_dll = native_host_dlls[0]
    native_host_output = native_host_dll.parent

    plugin_destination = STAGING / "SharedDlls" / "x64" / configuration / "Plugins"
    plugin_destination.mkdir(parents=True, exist_ok=True)
    shutil.copy2(native_host_dll, plugin_destination / native_host_dll.name)
    if configuration == "Debug":
        native_host_pdb = native_host_output / "AgentFlow.Windows.NativeHost.pdb"
        if native_host_pdb.is_file():
            shutil.copy2(native_host_pdb, plugin_destination / native_host_pdb.name)
    log(f"  copied NativeHost plugin assembly: {plugin_destination / native_host_dll.name}")


def package_windows_native_sdk(configuration: str) -> None:
    if platform.system() != "Windows":
        return
    if not WINDOWS_NATIVE_API_HEADER.is_file():
        raise FileNotFoundError(f"Windows native API header was not found: {WINDOWS_NATIVE_API_HEADER}")

    header_destination = STAGING / "SDK" / "Windows" / "NativeNodeApi"
    header_destination.mkdir(parents=True, exist_ok=True)
    shutil.copy2(WINDOWS_NATIVE_API_HEADER, header_destination / WINDOWS_NATIVE_API_HEADER.name)

    codegen_output = BUILD_ROOT / "NativeNodeCodeGen" / configuration
    if codegen_output.exists():
        shutil.rmtree(codegen_output)
    run([
        "dotnet", "publish", NATIVE_NODE_CODEGEN_PROJECT, "-c", configuration,
        "-p:Platform=x64", "-p:CopyLocalLockFileAssemblies=true", "-o", codegen_output,
        "-v:minimal",
    ])
    codegen_destination = STAGING / "SDK" / "Windows" / "NativeNodeCodeGen" / configuration
    if codegen_destination.exists():
        shutil.rmtree(codegen_destination)
    shutil.copytree(codegen_output, codegen_destination)
    log(f"  copied Windows native SDK support files for {configuration}.")


def sync_external_nodes(configuration: str, rid: str, destination: Path) -> None:
    log("")
    log(f"Building external-language packages for {configuration} {rid}...")
    run([
        sys.executable, ROOT / "Scripts" / "build_external_nodes.py", "sync", "--all",
        "--config", configuration, "--rid", rid, "--destination", destination,
        "--incremental",
    ])


def verify_external_nodes(rid: str) -> None:
    log("")
    log(f"Verifying Release external-language packages with FakeCore for {rid}...")
    run([
        sys.executable, ROOT / "Scripts" / "build_external_nodes.py", "verify", "--all",
        "--config", "Release", "--rid", rid,
    ])


def copy_external_packages(configuration: str, rid: str) -> None:
    source = ROOT / "artifacts" / "External" / configuration / rid
    destination = STAGING / "SharedDlls" / "x64" / configuration / "Plugins" / "External" / rid
    if destination.exists():
        shutil.rmtree(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    package_ids = external_package_ids()
    destination.mkdir(parents=True, exist_ok=True)
    for package_id in sorted(package_ids, key=str.lower):
        package_source = source / package_id
        if not package_source.is_dir():
            raise FileNotFoundError(f"External package output was not found: {package_source}")
        shutil.copytree(package_source, destination / package_id)
    log(f"  copied {len(package_ids)} external package(s): {destination}")


def external_package_ids() -> set[str]:
    source_root = ROOT / "Plugins" / "External"
    identifiers: set[str] = set()
    for manifest_path in source_root.rglob("plugin.json"):
        relative_parts = manifest_path.relative_to(source_root).parts
        if any(part in {"build", "dist", "publish", "__pycache__", *SUPPORTED_RIDS}
               for part in relative_parts):
            continue
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        package_id = str(manifest.get("packageId", "")).strip()
        if not package_id:
            raise ValueError(f"{manifest_path} does not declare packageId.")
        identifiers.add(package_id)
    if not identifiers:
        raise RuntimeError(f"No external node packages were found under {source_root}.")
    return identifiers


def find_powershell() -> str | None:
    for name in ("powershell", "pwsh"):
        found = shutil.which(name)
        if found:
            return found
    return None


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_binary_versions_fallback(root: Path, output: Path) -> None:
    entries = []
    for path in sorted(root.rglob("*"), key=lambda item: item.as_posix().lower()):
        if not path.is_file() or path.suffix.lower() not in {".dll", ".exe"}:
            continue
        entries.append({
            "relativePath": path.relative_to(root).as_posix(),
            "fileName": path.name,
            "assemblyVersion": None,
            "fileVersion": None,
            "productVersion": None,
            "length": path.stat().st_size,
            "sha256": sha256_file(path),
        })
    document = {
        "schemaVersion": 1,
        "generatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "root": ".",
        "fileCount": len(entries),
        "files": entries,
    }
    output.write_text(json.dumps(document, ensure_ascii=False, indent=2), encoding="utf-8")
    log(f"Binary version report written by portable fallback: {output} ({len(entries)} files)")


def copy_demo_projects() -> None:
    source_root = ROOT / "Samples"
    for project_name in DEMO_PROJECTS:
        source = source_root / project_name
        destination = STAGING / project_name
        if not source.is_dir():
            raise FileNotFoundError(f"Demo project was not found: {source}")
        if destination.exists():
            shutil.rmtree(destination)
        shutil.copytree(
            source,
            destination,
            ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"),
        )
        log(f"  copied demo project: {destination}")


def verify_binary_versions() -> None:
    log("")
    log("Validating binary version metadata...")
    powershell = find_powershell()
    if powershell:
        run([
            powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            ROOT / "Scripts" / "Verify-BinaryVersions.ps1", "-Root", STAGING,
            "-Output", STAGING / "BinaryVersions.json",
        ])
        return
    write_binary_versions_fallback(STAGING, STAGING / "BinaryVersions.json")


def write_readme(target_framework: str, plugin_framework: str, rids: list[str], external_enabled: bool) -> None:
    text = f"""AgentFlow Binary SDK

This archive does not contain the AgentFlow platform source projects.
It does include minimal Demo.Cli and Demo.Desktop source projects that run workflows.

Layout:
  SharedDlls/x64/Debug/Plugins          Debug C# node assemblies and PDB files
  SharedDlls/x64/Debug/Plugins/External/<RID> Debug external-language node packages
  SharedDlls/x64/Release                Release shared assemblies and dependencies
  SharedDlls/x64/Release/Plugins        Release C# node assemblies
  SharedDlls/x64/Release/Plugins/External/<RID> Verified Release external-language node packages
  SharedDlls/x64/<Configuration>/Plugins/Windows/win-x64/<ModuleId>
                                        Windows native node DLL and manifest
  SDK/Windows/NativeNodeApi/            Stable C ABI header for Windows native nodes
  SDK/Windows/NativeNodeCodeGen/<Configuration>
                                        Proxy generator for Windows native node manifests
  Demo.Cli/                          Minimal command-line workflow runner source project
  Demo.Desktop/                      Minimal Avalonia workflow runner source project

External projects should reference DLLs from the matching configuration:
  Debug projects   -> SharedDlls/x64/Debug
  Release projects -> SharedDlls/x64/Release

Keep Plugins and SharedDlls from the same SDK archive. Do not mix Debug and Release.
BinaryVersions.json contains the file and assembly versions of every AgentFlow-owned binary.
Node.Broadcast.Demo is included only in the Debug plugin folder.
External RIDs packaged: {", ".join(rids) if external_enabled else "none"}.
Release external packages were built, FakeCore-verified, and copied from artifacts/External/Release/<RID>.
Windows native SDK content is included when the package is built on Windows.
Application/shared target framework: {target_framework}.
Plugin target framework: {plugin_framework}.
"""
    (STAGING / "README.txt").write_text(text, encoding="utf-8")


def create_zip() -> None:
    if ZIP_PATH.exists():
        ZIP_PATH.unlink()
    with zipfile.ZipFile(ZIP_PATH, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(STAGING.rglob("*")):
            if path.is_file():
                archive.write(path, (Path(STAGING.name) / path.relative_to(STAGING)).as_posix())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--skip-external", action="store_true", help="Skip external-language node build and verification.")
    parser.add_argument("--rid", action="append", choices=SUPPORTED_RIDS,
                        help="External package RID to include; may be repeated. Defaults to the build host RID.")
    args = parser.parse_args()
    rids = args.rid or ([host_rid()] if not args.skip_external else [])

    print("==========================================")
    print("AgentFlow binary SDK packaging")
    print(f"Root:      {ROOT}")
    print(f"Staging:   {STAGING}")
    print(f"Output:    {ZIP_PATH}")
    print("==========================================")

    require_tool("dotnet")
    if not Path(sys.executable).is_file():
        raise RuntimeError("The current Python interpreter path is invalid.")

    target_framework = msbuild_property(
        ROOT / "AgentFlow.Contracts" / "AgentFlow.Contracts.csproj", "AgentFlowTargetFramework")
    plugin_framework = msbuild_property(
        ROOT / "Plugins" / "Node.AutoStart" / "Node.AutoStart.csproj", "PluginTargetFramework")

    for path in (STAGING, BUILD_ROOT):
        if path.exists():
            shutil.rmtree(path)
    if ZIP_PATH.exists():
        ZIP_PATH.unlink()
    STAGING.mkdir(parents=True, exist_ok=True)
    BUILD_ROOT.mkdir(parents=True, exist_ok=True)

    for configuration in ("Debug", "Release"):
        build_shared(configuration, plugin_framework)
        build_plugins(configuration)
        package_windows_native_nodes(configuration)
        package_windows_native_sdk(configuration)

    if not args.skip_external:
        external_debug = STAGING / "SharedDlls" / "x64" / "Debug" / "Plugins" / "External"
        for rid in rids:
            release_staging = BUILD_ROOT / "External-Release" / rid
            sync_external_nodes("Debug", rid, external_debug)
            sync_external_nodes("Release", rid, release_staging)
            verify_external_nodes(rid)
            copy_external_packages("Debug", rid)
            copy_external_packages("Release", rid)

    copy_demo_projects()
    verify_binary_versions()
    write_readme(target_framework, plugin_framework, rids, not args.skip_external)
    create_zip()
    log("")
    log(f"Package created: {ZIP_PATH}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
