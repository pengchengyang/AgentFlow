# AgentFlow Python Worker SDK

The SDK uses only the Python standard library. It connects to AgentFlow Core through a
Windows Named Pipe or Linux Unix Domain Socket and exchanges NDJSON messages.

## Minimal node

```python
from AgentFlowWorker import ExternalNode, NodeContext, RunWorker, Sample

class UpperNode(ExternalNode):
    def OnSample(self, context: NodeContext, pin: str, sample: Sample | None) -> None:
        if pin != "in":
            context.EmitError("PIN_UNKNOWN", f"unknown input pin {pin}")
            return
        value = "" if sample is None else sample.as_string()
        context.SendString("out", value.upper(), "out")

if __name__ == "__main__":
    raise SystemExit(RunWorker({"Node.PythonUpper": UpperNode}))
```

Optional business callbacks are:

```text
Configure / Init / Run / Stop / Shutdown / OnSample / OnBroadcast
```

Transport, manifest discovery, handshake, lifecycle dispatch, input queues, broadcast
routing and logging are handled by `NodeApplication` and `WorkerRuntime`.

The older `WorkerRuntime`, `NodeContext`, snake_case methods and manual registration API
remain available as the Advanced API.

Core always invokes the Worker as:

```text
Worker.exe --endpoint <named-pipe-or-uds> --token <token> [--package-root <root>]
```
