# -----------------------------------------------------------------------
# Copyright (c) 2026 Damon Yang. All rights reserved.
# -----------------------------------------------------------------------
"""Application facade that hides manifest discovery and runtime setup."""

from __future__ import annotations

import sys
from pathlib import Path
from typing import Any, Callable, Mapping, Optional

from .Runtime import WorkerRuntime


NodeClass = Callable[[], Any]


class NodeApplication:
    """Registers one or more node classes and runs the Worker runtime."""

    def __init__(self, manifest: str | Path | None = None) -> None:
        self._manifest = self._resolve_manifest(manifest)
        self._runtime = WorkerRuntime.from_manifest(self._manifest)

    @staticmethod
    def _resolve_manifest(manifest: str | Path | None) -> Path:
        if manifest is not None:
            path = Path(manifest).resolve()
            if not path.is_file():
                raise FileNotFoundError(f"External node manifest was not found: {path}")
            return path

        if getattr(sys, "frozen", False):
            executable = Path(sys.executable).resolve()
            candidate = executable.parent.parent / "plugin.json"
            if candidate.is_file():
                return candidate

        main_module = sys.modules.get("__main__")
        main_file = getattr(main_module, "__file__", None)
        if main_file:
            candidate = Path(main_file).resolve().parent.parent / "plugin.json"
            if candidate.is_file():
                return candidate

        candidate = Path.cwd() / "plugin.json"
        if candidate.is_file():
            return candidate.resolve()

        raise FileNotFoundError(
            "Cannot locate plugin.json. Pass its path to NodeApplication or RunWorker."
        )

    def RegisterNode(self, node_type: str, node_class: NodeClass) -> "NodeApplication":
        if not node_type:
            raise ValueError("node_type must not be empty.")
        if node_class is None:
            raise ValueError("node_class must not be None.")
        self._runtime.RegisterNode(node_type, node_class)
        return self

    def Run(self, argv: Optional[list[str]] = None) -> int:
        return self._runtime.RunForever(argv)


def RunWorker(
    registry: Mapping[str, NodeClass],
    manifest: str | Path | None = None,
) -> int:
    """Run a Worker from a simple node_type -> class registry."""

    if not registry:
        raise ValueError("registry must contain at least one node type.")
    application = NodeApplication(manifest)
    for node_type, node_class in registry.items():
        application.RegisterNode(node_type, node_class)
    return application.Run()
