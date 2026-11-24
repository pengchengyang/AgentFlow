#!/usr/bin/env python3
"""Build, verify, and package AgentFlow external-language node packages.

The orchestrator intentionally keeps all language-specific commands in this one
place. Python packages are frozen with PyInstaller (onedir), while C/C++ packages
are built through the vendored CMake executable and CMake-generated projects.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
from contextlib import contextmanager
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts"
EXTERNAL_OUTPUT = ARTIFACTS / "External"
BUILD_WORK = ARTIFACTS / "External-Build"
PYINSTALLER_VERSION = "6.22.3"


def set_artifacts_root(root: Path) -> None:
    """Redirect all external build outputs and caches to an explicit artifacts root."""
    global ARTIFACTS, EXTERNAL_OUTPUT, BUILD_WORK
    ARTIFACTS = root.resolve()
    EXTERNAL_OUTPUT = ARTIFACTS / "External"
    BUILD_WORK = ARTIFACTS / "External-Build"
SOURCES = (ROOT / "Plugins" / "External",)
SUPPORTED_RIDS = (
    "win-x64",
    "win-arm64",
    "linux-x64",
    "linux-arm64",
    "linux-arm",
)


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
    raise RuntimeError(f"Unsupported build host: {platform.system()} {platform.machine()}")


def target_platform(rid: str) -> str:
    if rid.startswith("win-"):
        return "windows"
    if rid.startswith("linux-"):
        return "linux"
    raise ValueError(f"Unsupported runtime identifier: {rid}")


def target_msbuild_platform(rid: str) -> str:
    if target_platform(rid) == "windows":
        return "x64" if rid == "win-x64" else "ARM64"
    return "AnyCPU"


def executable_suffix(rid: str) -> str:
    return ".exe" if target_platform(rid) == "windows" else ""


def ensure_native_target(rid: str) -> None:
    current = host_rid()
    if rid != current and os.environ.get("AGENTFLOW_ALLOW_EXTERNAL_CROSS_BUILD") != "1":
        raise RuntimeError(
            f"External package target {rid} must be built on a {rid} host; "
            f"current host is {current}. Set AGENTFLOW_ALLOW_EXTERNAL_CROSS_BUILD=1 only "
            "when the configured compiler and Python toolchains genuinely target that platform."
        )


def package_worker_executable(package: ExternalPackage, rid: str) -> str:
    return f"{package.artifact_name}{executable_suffix(rid)}"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def external_source_digest() -> str:
    digest = hashlib.sha256()
    for source_root in SOURCES:
        if not source_root.is_dir():
            continue
        for path in sorted(source_root.rglob("*")):
            if not path.is_file():
                continue
            relative = path.relative_to(ROOT).as_posix()
            generated_suffixes = {".pyc", ".exe", ".dll", ".lib", ".exp", ".pdb", ".obj", ".ilk"}
            if any(part in {"build", "dist", "publish", "__pycache__", *SUPPORTED_RIDS} for part in path.parts) or path.suffix.lower() in generated_suffixes:
                continue
            digest.update(relative.encode("utf-8"))
            digest.update(b"\0")
            digest.update(path.read_bytes())
            digest.update(b"\0")
    return digest.hexdigest()


def _process_is_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if os.name == "nt":
        process_query_limited_information = 0x1000
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(process_query_limited_information, False, pid)
        if not handle:
            return False
        kernel32.CloseHandle(handle)
        return True
    try:
        os.kill(pid, 0)
    except PermissionError:
        return True
    except OSError:
        return False
    return True


def _read_lock_owner(path: Path) -> dict[str, Any] | None:
    owner_file = path / "owner.json"
    try:
        value = json.loads(owner_file.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        return None
    return value if isinstance(value, dict) else None


def _lock_is_stale(path: Path) -> bool:
    try:
        age = time.time() - path.stat().st_mtime
    except FileNotFoundError:
        return True
    owner = _read_lock_owner(path)
    if owner:
        try:
            owner_pid = int(owner.get("pid", 0))
        except (TypeError, ValueError):
            owner_pid = 0
        if owner_pid and _process_is_alive(owner_pid):
            return False
        return True
    # A process can be killed between creating the directory and writing owner.json.
    return age > 30


def _describe_lock_owner(path: Path) -> str:
    owner = _read_lock_owner(path)
    if not owner:
        try:
            age = int(time.time() - path.stat().st_mtime)
            return f"owner=unknown age={age}s"
        except FileNotFoundError:
            return "owner=unknown"
    try:
        age = int(time.time() - path.stat().st_mtime)
    except FileNotFoundError:
        age = 0
    return (
        f"owner pid={owner.get('pid', 'unknown')} host={owner.get('host', 'unknown')} "
        f"started={owner.get('startedAt', 'unknown')} age={age}s"
    )


@contextmanager
def exclusive_build_lock(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    owner_token = f"{os.getpid()}-{time.time_ns()}"
    last_wait_log = 0.0
    while True:
        try:
            path.mkdir()
            owner = {
                "pid": os.getpid(),
                "host": platform.node(),
                "startedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "token": owner_token,
            }
            (path / "owner.json").write_text(json.dumps(owner, indent=2), encoding="utf-8")
            break
        except FileExistsError:
            if _lock_is_stale(path):
                log(f"Removing stale external node build lock: {path} ({_describe_lock_owner(path)})")
                shutil.rmtree(path, ignore_errors=True)
                continue
            elapsed = time.monotonic() - started
            if elapsed >= last_wait_log + 30:
                log(f"Waiting for external node build lock: {path} ({_describe_lock_owner(path)})")
                last_wait_log = elapsed
            if elapsed > 600:
                raise TimeoutError(
                    "Timed out waiting for external node build lock: "
                    f"{path} ({_describe_lock_owner(path)})"
                )
            time.sleep(1)
    try:
        yield
    finally:
        current_owner = _read_lock_owner(path)
        if current_owner and current_owner.get("token") == owner_token:
            shutil.rmtree(path, ignore_errors=True)


def run(
    command: list[str],
    *,
    cwd: Path | None = None,
    env: dict[str, str] | None = None,
    check: bool = True,
    capture: bool = False,
) -> subprocess.CompletedProcess[str]:
    rendered_command = " ".join(str(part) for part in command)
    log("  > " + rendered_command)
    completed = subprocess.run(
        [str(part) for part in command],
        cwd=str(cwd) if cwd else None,
        env=env,
        check=False,
        text=True,
        capture_output=capture,
    )
    if capture and completed.returncode != 0:
        if completed.stdout:
            print(completed.stdout.rstrip(), file=sys.stderr, flush=True)
        if completed.stderr:
            print(completed.stderr.rstrip(), file=sys.stderr, flush=True)
    if check and completed.returncode != 0:
        raise RuntimeError(f"Command failed with exit code {completed.returncode}: {rendered_command}")
    return completed


def find_cmake() -> Path | None:
    candidates: list[Path] = []
    if os.name == "nt":
        candidates.extend(sorted(ROOT.glob("cmake-*/bin/cmake.exe"), reverse=True))
    else:
        candidates.extend(sorted(ROOT.glob("cmake-*/bin/cmake"), reverse=True))
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    found = shutil.which("cmake")
    return Path(found) if found else None


def find_dotnet() -> Path | None:
    found = shutil.which("dotnet")
    if found:
        return Path(found)
    windows_default = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "dotnet" / "dotnet.exe"
    return windows_default if windows_default.is_file() else None


def find_dumpbin() -> Path | None:
    found = shutil.which("dumpbin")
    if found:
        return Path(found)
    if os.name != "nt":
        return None
    program_files_x86 = Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)"))
    vswhere = program_files_x86 / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
    if not vswhere.is_file():
        return None
    completed = run([str(vswhere), "-latest", "-property", "installationPath"], capture=True, check=False)
    if completed.returncode != 0 or not completed.stdout.strip():
        return None
    install = Path(completed.stdout.strip())
    tools = sorted((install / "VC" / "Tools" / "MSVC").glob("*/bin/Hostx64/x64/dumpbin.exe"), reverse=True)
    return tools[0] if tools else None


def find_python() -> Path:
    candidates = [Path(sys.executable)]
    if os.name == "nt":
        candidates.append(Path("C:/Program Files/Python314/python.exe"))
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    found = shutil.which("python")
    if found:
        return Path(found)
    raise RuntimeError("Python executable was not found.")


def relative_text(path: Path) -> str:
    try:
        return path.relative_to(ROOT).as_posix()
    except ValueError:
        return path.as_posix()


@dataclass
class ExternalPackage:
    source_dir: Path
    manifest: dict[str, Any]
    build: dict[str, Any]
    package_id: str
    language: str
    artifact_name: str
    node_type: str = ""
    priority: int = 0
    notes: list[str] = field(default_factory=list)

    @property
    def source_priority(self) -> int:
        return 1


def load_json(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8-sig") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object.")
    return value


def discover_packages() -> list[ExternalPackage]:
    by_id: dict[str, ExternalPackage] = {}
    for source_root in SOURCES:
        if not source_root.is_dir():
            continue
        for manifest_path in sorted(source_root.rglob("plugin.json"), key=lambda item: item.as_posix().lower()):
            directory = manifest_path.parent
            relative_parts = manifest_path.relative_to(source_root).parts
            if any(part in {"build", "dist", "publish", "__pycache__", *SUPPORTED_RIDS} for part in relative_parts):
                continue
            build_path = directory / "build.json"
            if not manifest_path.is_file() or not build_path.is_file():
                log(f"skip discovery: {directory} (plugin.json/build.json missing)")
                continue
            manifest = load_json(manifest_path)
            build = load_json(build_path)
            package_id = str(manifest.get("packageId", "")).strip()
            language = str(build.get("language", "")).strip().lower()
            artifact_name = str(build.get("artifactName", "")).strip()
            if not package_id or not artifact_name:
                raise ValueError(f"{directory}: build.json/plugin.json requires packageId and artifactName.")
            if language not in {"python", "c", "cpp"}:
                raise ValueError(f"{directory}: unsupported language '{language}'.")
            node_types = manifest.get("nodeTypes", [])
            node_type = str(node_types[0].get("nodeType", "")) if node_types else ""
            package = ExternalPackage(directory, manifest, build, package_id, language, artifact_name, node_type)
            existing = by_id.get(package_id)
            if existing is None or package.source_priority > existing.source_priority:
                if existing is not None:
                    log(f"discover: replacing duplicate {package_id} from {relative_text(existing.source_dir)} "
                        f"with {relative_text(directory)}")
                by_id[package_id] = package
            else:
                log(f"discover: skipping duplicate {package_id} from {relative_text(directory)}")
    return sorted(by_id.values(), key=lambda item: item.package_id.lower())


def package_output(config: str, rid: str, package: ExternalPackage) -> Path:
    return EXTERNAL_OUTPUT / config / rid / package.package_id


def package_work(config: str, rid: str, package: ExternalPackage) -> Path:
    return BUILD_WORK / config / rid / package.package_id


def ignore_build_artifacts(directory: str, names: list[str]) -> set[str]:
    ignored = {
        "build",
        "dist",
        "__pycache__",
        ".git",
        "obj",
        "bin",
    }
    return {name for name in names if name in ignored or name.endswith(".pyc")}


def copy_tree(source: Path, destination: Path, *, ignore_worker_build: bool = True) -> None:
    if not source.is_dir():
        return
    shutil.copytree(
        source,
        destination,
        dirs_exist_ok=True,
        ignore=ignore_build_artifacts if ignore_worker_build else None,
    )


def copy_package_shell(package: ExternalPackage, destination: Path) -> None:
    if destination.exists():
        shutil.rmtree(destination)
    destination.mkdir(parents=True, exist_ok=True)
    for entry in package.source_dir.iterdir():
        if entry.name in {"Worker", "build", "dist", "__pycache__"}:
            continue
        if entry.is_dir():
            shutil.copytree(entry, destination / entry.name, dirs_exist_ok=True, ignore=ignore_build_artifacts)
        else:
            shutil.copy2(entry, destination / entry.name)
    (destination / "Worker").mkdir(parents=True, exist_ok=True)


def clean_generated_worker_files(destination: Path, artifact_name: str) -> None:
    worker = destination / "Worker"
    patterns = [
        "AgentFlowWorker.dll",
        "AgentFlowWorker.lib",
        "AgentFlowWorker.exp",
        "*.pdb",
        "*.ilk",
        "*.obj",
        "*.lib",
        "*.exp",
        f"{artifact_name}.exe",
        f"{artifact_name}.cmd",
        f"{artifact_name}",
    ]
    for pattern in patterns:
        for path in worker.glob(pattern):
            if path.is_file():
                path.unlink()


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def compute_checksums(package_dir: Path) -> dict[str, str]:
    result: dict[str, str] = {}
    for path in sorted(package_dir.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(package_dir).as_posix()
        if relative in {"build-report.json", "checksums.sha256", "verification.log"}:
            continue
        result[relative] = sha256_file(path)
    return result


def write_checksums(package_dir: Path) -> None:
    checksums = compute_checksums(package_dir)
    lines = [f"{digest}  {path}" for path, digest in checksums.items()]
    (package_dir / "checksums.sha256").write_text("\n".join(lines) + "\n", encoding="utf-8")


def version_tuple(version: str) -> tuple[int, int, int, int]:
    parts = [int(part) for part in re.findall(r"\d+", version)[:4]]
    while len(parts) < 4:
        parts.append(0)
    return tuple(parts[:4])  # type: ignore[return-value]


def pyinstaller_version_file(package: ExternalPackage, output_path: Path) -> None:
    metadata = package.manifest.get("metadata", {})
    version = str(package.manifest.get("version", "1.0.0"))
    file_version = version_tuple(version)
    company = str(metadata.get("publisher") or metadata.get("author") or "AgentFlow")
    copyright_text = str(metadata.get("copyright") or "")
    description = str(package.manifest.get("description") or package.manifest.get("displayName") or package.package_id)
    original_filename = f"{package.artifact_name}.exe"

    def quote(value: str) -> str:
        return repr(value)

    output_path.write_text(
        "VSVersionInfo(\n"
        f"  ffi=FixedFileInfo(filevers={file_version}, prodvers={file_version}, mask=0x3f, "
        "flags=0x0, OS=0x40004, fileType=0x1, subtype=0x0, date=(0, 0)),\n"
        "  kids=[\n"
        "    StringFileInfo([\n"
        "      StringTable('040904B0', [\n"
        f"        StringStruct('CompanyName', {quote(company)}),\n"
        f"        StringStruct('FileDescription', {quote(description)}),\n"
        f"        StringStruct('FileVersion', {quote(version)}),\n"
        f"        StringStruct('InternalName', {quote(package.artifact_name)}),\n"
        f"        StringStruct('LegalCopyright', {quote(copyright_text)}),\n"
        f"        StringStruct('OriginalFilename', {quote(original_filename)}),\n"
        f"        StringStruct('ProductName', {quote(package.package_id)}),\n"
        f"        StringStruct('ProductVersion', {quote(version)})\n"
        "      ])\n"
        "    ]),\n"
        "    VarFileInfo([VarStruct('Translation', [1033, 1200])])\n"
        "  ]\n"
        ")\n",
        encoding="utf-8",
    )


def requirement_lines(path: Path) -> list[str]:
    if not path.is_file():
        return []
    lines = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        stripped = line.strip()
        if stripped and not stripped.startswith("#"):
            lines.append(stripped)
    return lines


def build_python(package: ExternalPackage, config: str, rid: str, output: Path, work: Path) -> dict[str, Any]:
    entry = package.source_dir / str(package.build.get("entry", "Worker/Main.py"))
    requirements = package.source_dir / str(package.build.get("requirements", "Worker/requirements.txt"))
    if not entry.is_file():
        raise FileNotFoundError(f"Python entry was not found: {entry}")
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True, exist_ok=True)
    copy_package_shell(package, output)
    clean_generated_worker_files(output, package.artifact_name)

    python = find_python()
    venv = work / "venv"
    run([python, "-m", "venv", venv], cwd=ROOT)
    venv_python = venv / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
    run([venv_python, "-m", "pip", "install", "--disable-pip-version-check", "--no-input", f"pyinstaller=={PYINSTALLER_VERSION}"], cwd=ROOT)
    requirements_for_build = requirement_lines(requirements)
    if requirements_for_build:
        run([venv_python, "-m", "pip", "install", "--disable-pip-version-check", "--no-input", "-r", requirements], cwd=ROOT)

    version_file = work / "version_info.txt"
    dist_dir = work / "dist"
    work_dir = work / "pyinstaller-work"
    spec_dir = work / "spec"
    command = [
        venv_python,
        "-m",
        "PyInstaller",
        "--noconfirm",
        "--clean",
        "--onedir",
        "--name",
        package.artifact_name,
        "--distpath",
        dist_dir,
        "--workpath",
        work_dir,
        "--specpath",
        spec_dir,
        "--paths",
        ROOT / "SDK" / "Python",
    ]
    if target_platform(rid) == "windows":
        pyinstaller_version_file(package, version_file)
        command.extend(["--version-file", version_file])
    for argument in package.build.get("extraArgs", []):
        command.append(str(argument))
    command.append(entry)
    run(command, cwd=package.source_dir)

    frozen = dist_dir / package.artifact_name
    if not frozen.is_dir():
        raise FileNotFoundError(f"PyInstaller onedir output was not found: {frozen}")
    copy_tree(frozen, output / "Worker", ignore_worker_build=False)

    executable_name = package_worker_executable(package, rid)
    packaged_manifest = json.loads(json.dumps(package.manifest))
    packaged_manifest.setdefault("worker", {})["executable"] = f"Worker/{executable_name}"
    write_json(output / "plugin.json", packaged_manifest)
    return {
        "buildSystem": "pyinstaller",
        "pyinstaller": PYINSTALLER_VERSION,
        "python": platform.python_version(),
        "runtimeIdentifier": rid,
    }


def cmake_generator(cmake: Path) -> str:
    completed = run([cmake, "--help"], capture=True, check=False)
    output = completed.stdout + completed.stderr
    if "Visual Studio 18 2026" in output:
        return "Visual Studio 18 2026"
    if "Visual Studio 17 2022" in output:
        return "Visual Studio 17 2022"
    if os.name != "nt" and shutil.which("ninja"):
        return "Ninja"
    if shutil.which("make"):
        return "Unix Makefiles"
    raise RuntimeError("No supported CMake generator was found.")


def build_native(package: ExternalPackage, config: str, rid: str, output: Path, work: Path) -> dict[str, Any]:
    ensure_native_target(rid)
    cmake = find_cmake()
    if cmake is None:
        raise RuntimeError("CMake was not found. The portable CMake in the repository root is preferred.")
    source_dir = package.source_dir / str(package.build.get("sourceDir", "Worker"))
    if not source_dir.is_dir():
        raise FileNotFoundError(f"Native source directory was not found: {source_dir}")
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True, exist_ok=True)
    build_dir = work / "cmake-build"
    copy_package_shell(package, output)
    clean_generated_worker_files(output, package.artifact_name)

    generator = cmake_generator(cmake)
    configure = [cmake, "-S", source_dir, "-B", build_dir, "-G", generator]
    if target_platform(rid) == "windows":
        configure.extend(["-A", "x64" if rid == "win-x64" else "ARM64"])
        if config == "Release":
            configure.append("-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded")
    else:
        configure.append(f"-DCMAKE_BUILD_TYPE={config}")
    for argument in package.build.get("cmakeConfigureArgs", []):
        configure.append(str(argument))
    run(configure, cwd=ROOT)
    target = str(package.build.get("target") or package.artifact_name)
    run([cmake, "--build", build_dir, "--config", config, "--target", target, "--parallel"], cwd=ROOT)

    suffix = executable_suffix(rid)
    candidates = [
        build_dir / config / f"{package.artifact_name}{suffix}",
        build_dir / f"{package.artifact_name}{suffix}",
        build_dir / f"{package.artifact_name}",
        build_dir / "bin" / f"{package.artifact_name}{suffix}",
    ]
    if rid == "win-x64":
        candidates.insert(1, build_dir / "x64" / config / f"{package.artifact_name}{suffix}")
    elif rid == "win-arm64":
        candidates.insert(1, build_dir / "ARM64" / config / f"{package.artifact_name}{suffix}")
    executable = next((candidate for candidate in candidates if candidate.is_file()), None)
    if executable is None:
        all_candidates = list(build_dir.rglob(package.artifact_name + suffix))
        executable = all_candidates[0] if all_candidates else None
    if executable is None:
        raise FileNotFoundError(f"Native executable for {package.package_id} was not produced by CMake.")
    copied_executable = output / "Worker" / executable.name
    shutil.copy2(executable, copied_executable)
    if target_platform(rid) != "windows":
        copied_executable.chmod(copied_executable.stat().st_mode | 0o111)

    packaged_manifest = json.loads(json.dumps(package.manifest))
    packaged_manifest.setdefault("worker", {})["executable"] = f"Worker/{executable.name}"
    write_json(output / "plugin.json", packaged_manifest)

    cmake_location = "repository-portable" if cmake.is_relative_to(ROOT) else "system-path"
    return {
        "buildSystem": "cmake",
        "cmake": cmake_location,
        "generator": generator,
        "runtimeIdentifier": rid,
    }


def dumpbin_dependencies(dumpbin: Path, executable: Path) -> list[str]:
    completed = run([dumpbin, "/nologo", "/dependents", executable], capture=True, check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"dumpbin failed for {executable}: {completed.stderr}")
    dependencies = []
    for line in completed.stdout.splitlines():
        match = re.search(r"\b([A-Za-z0-9_.-]+\.dll)\b", line, re.IGNORECASE)
        if match:
            name = match.group(1)
            if name not in dependencies:
                dependencies.append(name)
    return dependencies


def readelf_dependencies(readelf: Path, executable: Path) -> list[str]:
    completed = run([readelf, "-d", executable], capture=True, check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"readelf failed for {executable}: {completed.stderr}")
    dependencies = []
    for line in completed.stdout.splitlines():
        match = re.search(r"\(NEEDED\).*\[([^]]+)\]", line)
        if match and match.group(1) not in dependencies:
            dependencies.append(match.group(1))
    return dependencies


def write_third_party_notices(package: ExternalPackage, output: Path, rid: str) -> None:
    if package.language == "python":
        text = (
            "AgentFlow external node distribution\n\n"
            "This package contains a PyInstaller-frozen Python runtime and the Python standard library.\n"
            "Python is distributed under the Python Software Foundation License.\n"
            "PyInstaller is distributed under the GNU General Public License v2 with a special exception.\n"
            "See the LICENSE file in this package and the bundled license files under _internal/.\n"
        )
    else:
        text = (
            "AgentFlow external node distribution\n\n"
            "This package contains a statically linked AgentFlow C Worker Runtime and cJSON.\n"
            "cJSON is distributed under the MIT License. See SDK/C/Third_Party/Cjson/LICENSE.\n"
            + (
                "The Release build statically links the MSVC C/C++ runtime where supported.\n"
                if target_platform(rid) == "windows"
                else "This Linux package is built against the target host's system C/C++ runtime.\n"
            )
        )
    (output / "THIRD_PARTY_NOTICES.txt").write_text(text, encoding="utf-8")


def artifact_validation(package: ExternalPackage, output: Path, config: str, rid: str) -> dict[str, Any]:
    manifest = load_json(output / "plugin.json")
    executable_relative = str(manifest.get("worker", {}).get("executable", "")).replace("\\", "/")
    executable = output / executable_relative
    if not executable.is_file():
        raise FileNotFoundError(f"Manifest executable does not exist: {executable}")
    if target_platform(rid) != "windows" and not os.access(executable, os.X_OK):
        raise RuntimeError(f"Manifest executable is not executable on this platform: {executable}")
    if package.language == "python" and not (output / "Worker" / "_internal").is_dir():
        raise FileNotFoundError("PyInstaller package is missing Worker/_internal.")
    forbidden_development_files = [
        "Worker/build",
        "Worker/dist",
        "Worker/AgentFlowWorker.dll",
        "Worker/Main.py",
        "Worker/CMakeLists.txt",
        "Worker/Main.c",
        "Worker/Main.cpp",
    ]
    for relative in forbidden_development_files:
        if (output / relative).exists():
            raise RuntimeError(f"Release package contains development artifact: {relative}")

    native_dependencies: list[str] = []
    if config == "Release" and package.language in {"c", "cpp"}:
        if target_platform(rid) == "windows":
            dumpbin = find_dumpbin()
            if dumpbin is None:
                raise RuntimeError("dumpbin was not found; cannot verify Release native runtime dependencies.")
            native_dependencies = dumpbin_dependencies(dumpbin, executable)
            forbidden = {"VCRUNTIME140.dll", "VCRUNTIME140_1.dll", "MSVCP140.dll", "MSVCP140_1.dll", "MSVCP140_2.dll"}
            found = sorted(forbidden.intersection(name.upper() for name in native_dependencies))
            if found:
                raise RuntimeError(f"Release executable still depends on dynamic MSVC runtime: {', '.join(found)}")
        else:
            found_readelf = shutil.which("readelf")
            if found_readelf:
                native_dependencies = readelf_dependencies(Path(found_readelf), executable)

    text_paths = [
        path for path in output.rglob("*")
        if path.is_file() and path.suffix.lower() in {".json", ".txt", ".md", ".cmd", ".ps1", ".py", ".c", ".cpp", ".h", ".hpp"}
    ]
    root_text = str(ROOT)
    for path in text_paths:
        try:
            content = path.read_text(encoding="utf-8-sig")
        except UnicodeDecodeError:
            continue
        if root_text in content:
            raise RuntimeError(f"Package text file contains a development-machine absolute path: {path}")

    return {
        "manifestExecutable": executable_relative,
        "nativeDependencies": native_dependencies,
        "checkedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }


def build_package(package: ExternalPackage, config: str, rid: str) -> None:
    ensure_native_target(rid)
    output = package_output(config, rid, package)
    work = package_work(config, rid, package)
    log("")
    log(f"[{config} {rid}] {package.package_id} ({package.language})")
    log(f"  source: {relative_text(package.source_dir)}")
    if output.exists():
        shutil.rmtree(output)
    started = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    if package.language == "python":
        toolchain = build_python(package, config, rid, output, work)
    else:
        toolchain = build_native(package, config, rid, output, work)
    write_third_party_notices(package, output, rid)
    validation = artifact_validation(package, output, config, rid)
    report = {
        "schemaVersion": 1,
        "packageId": package.package_id,
        "nodeType": package.node_type,
        "language": package.language,
        "configuration": config,
        "runtimeIdentifier": rid,
        "platform": target_platform(rid),
        "architecture": rid.rsplit("-", 1)[-1],
        "sourceDirectory": relative_text(package.source_dir),
        "outputDirectory": relative_text(output),
        "buildStartedUtc": started,
        "buildCompletedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "toolchain": toolchain,
        "host": {
            "os": platform.platform(),
            "machine": platform.machine(),
            "python": sys.version,
        },
        "validation": validation,
        "verification": {"status": "not-run"},
    }
    write_json(output / "build-report.json", report)
    write_checksums(output)
    log(f"  output: {relative_text(output)}")


def build_test_client(config: str, rid: str) -> Path:
    ensure_native_target(rid)
    dotnet = find_dotnet()
    if dotnet is None:
        raise RuntimeError("dotnet was not found.")
    output = BUILD_WORK / "test-client" / config / rid
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True, exist_ok=True)
    project = ROOT / "Tools" / "ExternalWorkerTestClient" / "ExternalWorkerTestClient.csproj"
    run([
        dotnet, "build", project, "-c", config,
        f"-p:Platform={target_msbuild_platform(rid)}",
        "-p:UseSharedCompilation=false",
        "-o", output, "-v:minimal",
        "--no-incremental",
        "--disable-build-servers",
        "--artifacts-path", BUILD_WORK / "dotnet-artifacts" / config / rid,
    ], cwd=ROOT)
    dll = output / "ExternalWorkerTestClient.dll"
    if not dll.is_file():
        raise FileNotFoundError(f"FakeCore test client was not produced: {dll}")
    return dll


def clean_environment(temp_dir: Path) -> dict[str, str]:
    clean_path = temp_dir / "empty-path"
    clean_path.mkdir(parents=True, exist_ok=True)
    env = {
        "PATH": str(clean_path),
        "TEMP": str(temp_dir),
        "TMP": str(temp_dir),
        "HOME": str(temp_dir),
    }
    if os.name == "nt":
        system_root = os.environ.get("SystemRoot", "C:/Windows")
        env["SYSTEMROOT"] = system_root
        env["WINDIR"] = system_root
        dotnet_root = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "dotnet"
        if dotnet_root.is_dir():
            env["DOTNET_ROOT"] = str(dotnet_root)
    else:
        dotnet_root = Path(shutil.which("dotnet") or "/usr/bin/dotnet").resolve().parent.parent
        if dotnet_root.is_dir():
            env["DOTNET_ROOT"] = str(dotnet_root)
    return env


def verify_package(package: ExternalPackage, config: str, rid: str, test_client_dll: Path) -> dict[str, Any]:
    ensure_native_target(rid)
    source_package = package_output(config, rid, package)
    if not source_package.is_dir():
        raise FileNotFoundError(f"Package has not been built: {source_package}")
    clean_root = Path(tempfile.mkdtemp(prefix="agentflow-clean-"))
    try:
        isolated_package = clean_root / package.package_id
        shutil.copytree(source_package, isolated_package)
        dotnet = find_dotnet()
        if dotnet is None:
            raise RuntimeError("dotnet was not found.")
        command = [dotnet, test_client_dll, "--package", isolated_package, "--input", "clean-room"]
        completed = subprocess.run(
            [str(part) for part in command],
            cwd=clean_root,
            env=clean_environment(clean_root),
            text=True,
            capture_output=True,
            timeout=120,
        )
        log("  > " + " ".join(str(part) for part in command))
        if completed.stdout:
            log(completed.stdout.rstrip())
        if completed.stderr:
            print(completed.stderr.rstrip(), file=sys.stderr, flush=True)
        sanitized_stdout = completed.stdout.replace(str(clean_root), "<isolated-clean-room>")
        sanitized_stderr = completed.stderr.replace(str(clean_root), "<isolated-clean-room>")
        verification_log = (
            "Mode: PATH-isolated clean-room (no Python, CMake, or compiler in PATH)\n"
            "Working directory: isolated external temporary directory\n"
            f"Exit code: {completed.returncode}\n\n"
            f"STDOUT:\n{sanitized_stdout}\n\nSTDERR:\n{sanitized_stderr}\n"
        )
        (source_package / "verification.log").write_text(verification_log, encoding="utf-8")
        if completed.returncode != 0:
            raise RuntimeError(f"FakeCore verification failed with exit code {completed.returncode}.")
        result = {
            "status": "passed",
            "mode": "path-isolated-clean-room",
            "verifiedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "workingDirectoryWasOutsideProject": True,
            "pythonAvailableInWorkerPath": False,
            "cmakeAvailableInWorkerPath": False,
            "compilerAvailableInWorkerPath": False,
            "fakeCoreExitCode": completed.returncode,
        }
        report_path = source_package / "build-report.json"
        report = load_json(report_path)
        report["verification"] = result
        write_json(report_path, report)
        write_checksums(source_package)
        return result
    finally:
        shutil.rmtree(clean_root, ignore_errors=True)


def verify_packages(config: str, rid: str, packages: list[ExternalPackage]) -> int:
    test_client = build_test_client(config, rid)
    failures: list[str] = []
    for package in packages:
        log("")
        log(f"verify [{config} {rid}] {package.package_id}")
        try:
            verify_package(package, config, rid, test_client)
            log("  PASS")
        except Exception as error:  # noqa: BLE001 - orchestration reports all package failures
            failures.append(f"{package.package_id}: {error}")
            log(f"  FAIL: {error}")
    if failures:
        log("")
        log("Verification failures:")
        for failure in failures:
            log(f"  - {failure}")
        return 1
    return 0


def write_external_release_metadata(config: str, rid: str) -> None:
    source = EXTERNAL_OUTPUT / config / rid
    packages = []
    discovered_ids = {package.package_id for package in discover_packages()}
    for directory in sorted(source.iterdir(), key=lambda item: item.name.lower()):
        if not directory.is_dir():
            continue
        if directory.name not in discovered_ids:
            log(f"  skip stale external output: {relative_text(directory)}")
            continue
        report_path = directory / "build-report.json"
        if not report_path.is_file():
            raise FileNotFoundError(f"Missing build report: {report_path}")
        report = load_json(report_path)
        verification = report.get("verification", {})
        if verification.get("status") != "passed":
            raise RuntimeError(f"Package has not passed verification: {directory.name}")
        packages.append({
            "packageId": report["packageId"],
            "language": report["language"],
            "configuration": report["configuration"],
            "executable": report["validation"]["manifestExecutable"],
            "verification": verification.get("status"),
            "nativeDependencies": report["validation"].get("nativeDependencies", []),
        })
    manifest = {
        "schemaVersion": 1,
        "configuration": config,
        "runtimeIdentifier": rid,
        "platform": target_platform(rid),
        "architecture": rid.rsplit("-", 1)[-1],
        "generatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "verificationMode": "path-isolated-clean-room",
        "packages": packages,
    }
    write_json(source / "ReleaseManifest.json", manifest)
    readme = (
        f"AgentFlow External Nodes - {config} {rid}\n\n"
        "Each package directory is self-contained and can be copied independently.\n"
        "Release packages were validated with ExternalWorkerTestClient/FakeCore while the Worker\n"
        "process used a PATH that excluded Python, CMake, and compiler directories.\n\n"
        "See ReleaseManifest.json, each package build-report.json, verification.log, and checksums.sha256.\n"
    )
    (source / "README.txt").write_text(readme, encoding="utf-8")


def package_zip(config: str, rid: str) -> Path:
    source = EXTERNAL_OUTPUT / config / rid
    if not source.is_dir():
        raise FileNotFoundError(f"External package output was not found: {source}")
    write_external_release_metadata(config, rid)
    destination = EXTERNAL_OUTPUT / f"AgentFlow.ExternalNodes.{config}.{rid}.zip"
    if destination.exists():
        destination.unlink()
    package_roots = {package.package_id for package in discover_packages()} | {
        "ReleaseManifest.json",
        "README.txt",
    }
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(source.rglob("*")):
            relative = path.relative_to(source)
            if path.is_file() and relative.parts and relative.parts[0] in package_roots:
                archive.write(path, (Path(rid) / path.relative_to(source)).as_posix())
    return destination


def select_packages(args: argparse.Namespace) -> list[ExternalPackage]:
    packages = discover_packages()
    if args.package:
        wanted = {name.lower() for name in args.package}
        packages = [package for package in packages if package.package_id.lower() in wanted or package.source_dir.name.lower() in wanted]
    if args.language:
        wanted_languages = {name.lower() for name in args.language}
        packages = [package for package in packages if package.language in wanted_languages]
    if not packages:
        raise RuntimeError("No external packages matched the requested selection.")
    return packages


def command_doctor(args: argparse.Namespace) -> int:
    log("AgentFlow external node toolchain doctor")
    log(f"  project root: {ROOT}")
    log(f"  host RID: {host_rid()}")
    log(f"  target RID: {args.rid}")
    python = find_python()
    log(f"  python: {python}")
    pyinstaller_module = run([python, "-m", "PyInstaller", "--version"], capture=True, check=False)
    if pyinstaller_module.returncode == 0:
        log(f"  PyInstaller: {pyinstaller_module.stdout.strip()}")
    else:
        log("  PyInstaller: NOT INSTALLED")
    cmake = find_cmake()
    if cmake:
        version = run([cmake, "--version"], capture=True, check=False)
        log(f"  CMake: {cmake} ({version.stdout.splitlines()[0] if version.stdout else 'unknown'})")
        log(f"  CMake generator: {cmake_generator(cmake)}")
    else:
        log("  CMake: NOT FOUND")
    if target_platform(args.rid) == "windows":
        dumpbin = find_dumpbin()
        log(f"  dumpbin: {dumpbin if dumpbin else 'NOT FOUND'}")
    else:
        dumpbin = None
        readelf = shutil.which("readelf")
        log(f"  readelf: {readelf if readelf else 'NOT FOUND'}")
    dotnet = find_dotnet()
    if dotnet:
        version = run([dotnet, "--version"], capture=True, check=False)
        log(f"  dotnet: {dotnet} ({version.stdout.strip()})")
    else:
        log("  dotnet: NOT FOUND")
    packages = discover_packages()
    log(f"  discovered packages: {len(packages)}")
    for package in packages:
        log(f"    - {package.package_id} [{package.language}] {relative_text(package.source_dir)}")
    toolchain_ready = bool(python and cmake and dotnet)
    if target_platform(args.rid) == "windows":
        toolchain_ready = toolchain_ready and dumpbin is not None
    return 0 if toolchain_ready else 1


def command_discover(_: argparse.Namespace) -> int:
    for package in discover_packages():
        log(f"{package.package_id}\t{package.language}\t{relative_text(package.source_dir)}")
    return 0


def command_build(args: argparse.Namespace) -> int:
    packages = select_packages(args)
    failures: list[str] = []
    for package in packages:
        try:
            build_package(package, args.config, args.rid)
        except Exception as error:  # noqa: BLE001
            failures.append(f"{package.package_id}: {error}")
            log(f"  BUILD FAIL: {error}")
            if args.fail_fast:
                break
    if failures:
        log("Build failures:")
        for failure in failures:
            log(f"  - {failure}")
        return 1
    return 0


def external_artifacts_ready(config: str, rid: str, packages: list[ExternalPackage]) -> bool:
    for package in packages:
        output = package_output(config, rid, package)
        if not (output / "plugin.json").is_file():
            return False
    return bool(packages)


def copy_external_output(
    config: str,
    rid: str,
    destination: Path,
    packages: list[ExternalPackage],
) -> None:
    source = EXTERNAL_OUTPUT / config / rid
    if not source.is_dir():
        raise FileNotFoundError(f"External package output was not found: {source}")
    target = destination / rid
    if target.exists():
        shutil.rmtree(target)
    target.mkdir(parents=True, exist_ok=True)
    for package in packages:
        package_source = source / package.package_id
        if not package_source.is_dir():
            raise FileNotFoundError(f"External package output was not found: {package_source}")
        shutil.copytree(package_source, target / package.package_id, dirs_exist_ok=True)
    log(f"  copied {len(packages)} external package(s) to {target}")


def command_sync(args: argparse.Namespace) -> int:
    packages = select_packages(args)
    digest = external_source_digest()
    stamp = Path(args.stamp) if args.stamp else BUILD_WORK / "source-stamps" / f"{args.config}.{args.rid}.sha256"
    stamp.parent.mkdir(parents=True, exist_ok=True)

    with exclusive_build_lock(BUILD_WORK / f".sync-{args.config}.{args.rid}.lock"):
        previous = stamp.read_text(encoding="utf-8").strip() if stamp.is_file() else ""
        needs_build = not args.incremental or previous != digest or not external_artifacts_ready(args.config, args.rid, packages)
        if needs_build:
            log(f"External source changed; building {len(packages)} package(s) for {args.config}.")
            failures: list[str] = []
            for package in packages:
                try:
                    build_package(package, args.config, args.rid)
                except Exception as error:  # noqa: BLE001 - report all package failures
                    failures.append(f"{package.package_id}: {error}")
                    log(f"  BUILD FAIL: {error}")
                    if args.fail_fast:
                        break
            if failures:
                for failure in failures:
                    log(f"  - {failure}")
                return 1
            stamp.write_text(digest + "\n", encoding="utf-8")
            log(f"  updated source stamp: {relative_text(stamp)}")
        else:
            log(f"External source unchanged; reusing {relative_text(EXTERNAL_OUTPUT / args.config / args.rid)}.")

        for destination_text in args.destination:
            copy_external_output(args.config, args.rid, Path(destination_text), packages)
    return 0


def command_verify(args: argparse.Namespace) -> int:
    return verify_packages(args.config, args.rid, select_packages(args))


def command_package(args: argparse.Namespace) -> int:
    path = package_zip(args.config, args.rid)
    log(f"Package archive created: {relative_text(path)}")
    return 0


def command_clean(args: argparse.Namespace) -> int:
    targets = [BUILD_WORK]
    if args.all:
        targets.append(EXTERNAL_OUTPUT)
    for target in targets:
        if target.exists():
            log(f"Removing {relative_text(target)}")
            shutil.rmtree(target)
    return 0


def add_common_selection(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--all", action="store_true", help="Select every discovered package (default).")
    parser.add_argument("--package", action="append", help="Package ID or source directory name.")
    parser.add_argument("--language", action="append", choices=("python", "c", "cpp"), help="Language filter.")
    parser.add_argument("--config", default="Release", choices=("Debug", "Release"))
    parser.add_argument("--rid", default=host_rid(), choices=SUPPORTED_RIDS)
    parser.add_argument("--fail-fast", action="store_true")
    parser.add_argument("--artifacts-root", help="Directory for external outputs and isolated build caches.")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    doctor_parser = subparsers.add_parser("doctor")
    doctor_parser.add_argument("--rid", default=host_rid(), choices=SUPPORTED_RIDS)
    doctor_parser.set_defaults(func=command_doctor)

    discover_parser = subparsers.add_parser("discover")
    discover_parser.set_defaults(func=command_discover)

    build_parser = subparsers.add_parser("build")
    add_common_selection(build_parser)
    build_parser.set_defaults(func=command_build)

    sync_parser = subparsers.add_parser("sync")
    add_common_selection(sync_parser)
    sync_parser.add_argument("--destination", action="append", required=True,
                             help="Runtime Plugins/External destination; may be repeated.")
    sync_parser.add_argument("--stamp", help="Source fingerprint file used by incremental builds.")
    sync_parser.add_argument("--incremental", action="store_true",
                             help="Rebuild only when Plugins/External sources changed or outputs are missing.")
    sync_parser.set_defaults(func=command_sync)

    verify_parser = subparsers.add_parser("verify")
    add_common_selection(verify_parser)
    verify_parser.set_defaults(func=command_verify)

    package_parser = subparsers.add_parser("package")
    package_parser.add_argument("--config", default="Release", choices=("Debug", "Release"))
    package_parser.add_argument("--rid", default=host_rid(), choices=SUPPORTED_RIDS)
    package_parser.set_defaults(func=command_package)

    clean_parser = subparsers.add_parser("clean")
    clean_parser.add_argument("--all", action="store_true")
    clean_parser.set_defaults(func=command_clean)

    args = parser.parse_args(argv)
    if getattr(args, "artifacts_root", None):
        set_artifacts_root(Path(args.artifacts_root))
    try:
        return int(args.func(args))
    except Exception as error:  # noqa: BLE001 - command-line entrypoint reports a concise error
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

