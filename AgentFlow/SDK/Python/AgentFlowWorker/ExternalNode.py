# -----------------------------------------------------------------------
# Copyright (c) 2026 Damon Yang. All rights reserved.
# -----------------------------------------------------------------------
"""Optional base class for external business nodes.

Business code normally implements only the callbacks it needs. Lifecycle and
transport details remain inside WorkerRuntime and NodeApplication.
"""

from __future__ import annotations

from typing import Any

from .Runtime import NodeContext
from .Samples import Sample


class ExternalNode:
    """Base class for nodes built with the simplified external-node API."""

    def Configure(self, context: NodeContext, parameters: dict[str, Any]) -> None:
        pass

    def Init(self, context: NodeContext) -> None:
        pass

    def Run(self, context: NodeContext) -> None:
        pass

    def Stop(self, context: NodeContext) -> None:
        pass

    def Shutdown(self, context: NodeContext) -> None:
        pass

    def OnSample(self, context: NodeContext, pin: str, sample: Sample | None) -> None:
        pass

    def OnBroadcast(self, context: NodeContext, topic: str, payload: Any) -> None:
        pass
