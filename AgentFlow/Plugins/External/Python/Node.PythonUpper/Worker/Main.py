# -----------------------------------------------------------------------
# Copyright (c) 2026 Damon Yang. All rights reserved.
# -----------------------------------------------------------------------
"""Minimal Python external node for AgentFlow."""

from __future__ import annotations

import sys
from pathlib import Path

# Development-only import path. Packaged Workers include the SDK via PyInstaller.
DEV_ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(DEV_ROOT / "SDK" / "Python"))

from AgentFlowWorker import ExternalNode, NodeContext, RunWorker, Sample


class PythonUpperNode(ExternalNode):
    """Demonstrate the complete simplified Python external-node API."""

    def Configure(self, context: NodeContext, parameters: dict[str, object]) -> None:
        context.LogInfo("configure", {"parameters": parameters})

    def Init(self, context: NodeContext) -> None:
        context.LogInfo("init")

    def Run(self, context: NodeContext) -> None:
        context.LogInfo("run")
        context.PublishJson("Demo.External.Stats", {"event": "run", "language": "python"})

    def Stop(self, context: NodeContext) -> None:
        context.LogInfo("stop")

    def Shutdown(self, context: NodeContext) -> None:
        context.LogInfo("shutdown")

    def OnSample(self, context: NodeContext, pin: str, sample: Sample | None) -> None:
        suffix = context.ParamString("Suffix", ":py") or ""
        value = "" if sample is None else sample.as_string()
        if pin == "input_string_toupper":
            context.SendString("output_string_toupper", value.upper() + suffix,
                               key="output_string_toupper")
        elif pin == "input_string_tolower":
            context.SendString("output_string_tolower", value.lower() + suffix,
                               key="output_string_tolower")
        else:
            context.EmitError("PIN_UNKNOWN", f"unknown input pin {pin}", fatal=False)

    def OnBroadcast(self, context: NodeContext, topic: str, payload: object) -> None:
        context.LogInfo(topic, {"payload": payload})
        context.PublishJson("Demo.External.PingAck", {"event": "ping-ack", "language": "python"})


def Main() -> int:
    return RunWorker({"Node.PythonUpper": PythonUpperNode})


if __name__ == "__main__":
    raise SystemExit(Main())
