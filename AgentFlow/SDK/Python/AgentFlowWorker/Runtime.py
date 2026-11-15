# -----------------------------------------------------------------------
# Copyright (c) 2026 Damon Yang. All rights reserved.
# -----------------------------------------------------------------------
"""Threaded AgentFlow Worker runtime.

The runtime owns transport, handshake, inbound/outbound queues, node-instance state
machines, lifecycle callbacks, Pin routing and broadcast routing. Node implementations
only receive a NodeContext and never touch the Named Pipe / Unix Domain Socket directly.
"""

from __future__ import annotations

import inspect
import json
import os
import queue
import sys
import threading
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

from .Samples import Sample
from .Transport import DuplexConnection


PROTOCOL_VERSION = 1
SDK_VERSION = "0.1.0"
MAX_OUTBOUND_QUEUE = 4096
MAX_NODE_QUEUE = 2048


class AgentFlowError(RuntimeError):
    pass


class OutboundQueueFull(AgentFlowError):
    pass


class NodeContext:
    """Per-node facade exposed to external-language node authors."""

    def __init__(
        self,
        runtime: "WorkerRuntime",
        node_instance_id: int,
        node_type: str,
        parameters: dict[str, Any],
    ) -> None:
        self._runtime = runtime
        self.node_instance_id = node_instance_id
        self.node_type = node_type
        self.package_id = runtime.package_id
        self.package_root = runtime.package_root
        self.params = dict(parameters or {})
        self._inputs: dict[str, Sample | None] = {}
        self._input_present: set[str] = set()
        self._condition = threading.Condition()
        self._cancel = threading.Event()
        self._run_id: Optional[str] = None
        self._topics: set[str] = set()

    def param_string(self, name: str, default: Optional[str] = None) -> Optional[str]:
        value = self.params.get(name)
        return default if value is None else str(value)

    def param_bool(self, name: str, default: Optional[bool] = None) -> Optional[bool]:
        value = self.params.get(name)
        return default if value is None else bool(value)

    def param_int32(self, name: str, default: Optional[int] = None) -> Optional[int]:
        value = self.params.get(name)
        return default if value is None else int(value)

    def param_int64(self, name: str, default: Optional[int] = None) -> Optional[int]:
        value = self.params.get(name)
        return default if value is None else int(value)

    def param_float(self, name: str, default: Optional[float] = None) -> Optional[float]:
        value = self.params.get(name)
        return default if value is None else float(value)

    def param_double(self, name: str, default: Optional[float] = None) -> Optional[float]:
        value = self.params.get(name)
        return default if value is None else float(value)

    def has_input(self, pin: str) -> bool:
        with self._condition:
            return pin in self._input_present

    def get_input(self, pin: str) -> Optional[Sample]:
        with self._condition:
            return self._inputs.get(pin)

    def wait_for_input(self, pin: str, timeout_ms: Optional[int] = None) -> Optional[Sample]:
        deadline = None if timeout_ms is None else time.monotonic() + timeout_ms / 1000.0
        with self._condition:
            while pin not in self._input_present:
                if self._cancel.is_set():
                    return None
                remaining = None if deadline is None else max(0.0, deadline - time.monotonic())
                if remaining == 0:
                    return None
                self._condition.wait(remaining)
            return self._inputs.get(pin)

    def send_sample(self, pin: str, sample: Sample | None) -> None:
        message = self._runtime._new_message("pin.send")
        message["nodeInstanceId"] = self.node_instance_id
        if self._run_id:
            message["runId"] = self._run_id
        message["sourcePin"] = pin
        message["sequence"] = self._runtime._next_sequence(self.node_instance_id)
        message["sample"] = None if sample is None else sample.to_wire(pin)
        self._runtime._send(message, blocking=False)

    def send_string(self, pin: str, value: Optional[str], key: str = "") -> None:
        self.send_sample(pin, Sample("STRING", value, key, "utf8"))

    def send_int32(self, pin: str, value: Optional[int], key: str = "") -> None:
        self.send_sample(pin, Sample("INT32", value, key, "decimal"))

    def send_int64(self, pin: str, value: Optional[int], key: str = "") -> None:
        self.send_sample(pin, Sample("INT64", value, key, "decimal-string"))

    def send_bool(self, pin: str, value: Optional[bool], key: str = "") -> None:
        self.send_sample(pin, Sample("BOOL", value, key, "boolean"))

    def send_float(self, pin: str, value: Optional[float], key: str = "") -> None:
        self.send_sample(pin, Sample("FLOAT", value, key, "ieee754-binary32-bits-hex"))

    def send_double(self, pin: str, value: Optional[float], key: str = "") -> None:
        self.send_sample(pin, Sample("DOUBLE", value, key, "ieee754-binary64-bits-hex"))

    def send_bin(self, pin: str, data: Optional[bytes], key: str = "") -> None:
        self.send_sample(pin, Sample("BIN", data, key, "base64"))

    def subscribe(self, topic: str) -> None:
        self._topics.add(topic)
        message = self._runtime._new_message("broadcast.subscribe")
        message["nodeInstanceId"] = self.node_instance_id
        message["workerId"] = self._runtime.worker_id
        message["topic"] = topic
        self._runtime._send(message, blocking=False)

    def unsubscribe(self, topic: str) -> None:
        self._topics.discard(topic)
        message = self._runtime._new_message("broadcast.unsubscribe")
        message["nodeInstanceId"] = self.node_instance_id
        message["workerId"] = self._runtime.worker_id
        message["topic"] = topic
        self._runtime._send(message, blocking=False)

    def publish(self, topic: str, payload: Any, payload_kind: str = "json") -> None:
        message = self._runtime._new_message("broadcast.publish")
        message["nodeInstanceId"] = self.node_instance_id
        message["workerId"] = self._runtime.worker_id
        message["topic"] = topic
        message["payloadKind"] = payload_kind
        message["payload"] = payload
        if self._run_id:
            message["runId"] = self._run_id
        self._runtime._send(message, blocking=False)

    def log_info(self, message: str, fields: Optional[dict[str, Any]] = None) -> None:
        self._log("info", message, fields)

    def log_warning(self, message: str, fields: Optional[dict[str, Any]] = None) -> None:
        self._log("warning", message, fields)

    def log_error(self, message: str, fields: Optional[dict[str, Any]] = None) -> None:
        self._log("error", message, fields)

    def emit_error(self, code: str, message: str, fatal: bool = False) -> None:
        payload = self._runtime._new_message("node.error")
        payload["nodeInstanceId"] = self.node_instance_id
        payload["code"] = code
        payload["severity"] = "error"
        payload["message"] = message
        payload["fatal"] = bool(fatal)
        self._runtime._send(payload, blocking=False)

    def _log(self, level: str, message: str, fields: Optional[dict[str, Any]]) -> None:
        payload = self._runtime._new_message("log")
        payload["nodeInstanceId"] = self.node_instance_id
        payload["workerId"] = self._runtime.worker_id
        payload["level"] = level
        payload["message"] = message
        if fields:
            payload["fields"] = fields
        if self._run_id:
            payload["runId"] = self._run_id
        self._runtime._send(payload, blocking=False)

    @property
    def InstanceId(self) -> int:
        return self.node_instance_id

    @property
    def NodeType(self) -> str:
        return self.node_type

    @property
    def PackageId(self) -> str:
        return self.package_id

    @property
    def PackageRoot(self) -> str:
        return str(self.package_root)

    @property
    def RunId(self) -> Optional[str]:
        return self._run_id

    @property
    def IsCancelled(self) -> bool:
        return self._cancel.is_set()

    def ParamString(self, name: str, default: Optional[str] = None) -> Optional[str]:
        return self.param_string(name, default)

    def ParamBool(self, name: str, default: Optional[bool] = None) -> Optional[bool]:
        return self.param_bool(name, default)

    def ParamInt32(self, name: str, default: Optional[int] = None) -> Optional[int]:
        return self.param_int32(name, default)

    def ParamInt64(self, name: str, default: Optional[int] = None) -> Optional[int]:
        return self.param_int64(name, default)

    def ParamFloat(self, name: str, default: Optional[float] = None) -> Optional[float]:
        return self.param_float(name, default)

    def ParamDouble(self, name: str, default: Optional[float] = None) -> Optional[float]:
        return self.param_double(name, default)

    def HasInput(self, pin: str) -> bool:
        return self.has_input(pin)

    def GetInput(self, pin: str) -> Optional[Sample]:
        return self.get_input(pin)

    def WaitForInput(self, pin: str, timeout_ms: Optional[int] = None) -> Optional[Sample]:
        return self.wait_for_input(pin, timeout_ms)

    def SendSample(self, pin: str, sample: Sample | None) -> None:
        self.send_sample(pin, sample)

    def SendString(self, pin: str, value: Optional[str], key: str = "") -> None:
        self.send_string(pin, value, key)

    def SendInt32(self, pin: str, value: Optional[int], key: str = "") -> None:
        self.send_int32(pin, value, key)

    def SendInt64(self, pin: str, value: Optional[int], key: str = "") -> None:
        self.send_int64(pin, value, key)

    def SendBool(self, pin: str, value: Optional[bool], key: str = "") -> None:
        self.send_bool(pin, value, key)

    def SendFloat(self, pin: str, value: Optional[float], key: str = "") -> None:
        self.send_float(pin, value, key)

    def SendDouble(self, pin: str, value: Optional[float], key: str = "") -> None:
        self.send_double(pin, value, key)

    def SendBin(self, pin: str, data: Optional[bytes], key: str = "") -> None:
        self.send_bin(pin, data, key)

    def Subscribe(self, topic: str) -> None:
        self.subscribe(topic)

    def Unsubscribe(self, topic: str) -> None:
        self.unsubscribe(topic)

    def Publish(self, topic: str, payload: Any, payload_kind: str = "json") -> None:
        self.publish(topic, payload, payload_kind)

    def PublishJson(self, topic: str, payload: Any) -> None:
        self.publish(topic, payload, "json")

    def LogTrace(self, message: str, fields: Optional[dict[str, Any]] = None) -> None:
        self._log("trace", message, fields)

    def LogDebug(self, message: str, fields: Optional[dict[str, Any]] = None) -> None:
        self._log("debug", message, fields)

    def LogInfo(self, message: str, fields: Optional[dict[str, Any]] = None) -> None:
        self._log("info", message, fields)

    def LogWarning(self, message: str, fields: Optional[dict[str, Any]] = None) -> None:
        self._log("warning", message, fields)

    def LogError(self, message: str, fields: Optional[dict[str, Any]] = None) -> None:
        self._log("error", message, fields)

    def LogCritical(self, message: str, fields: Optional[dict[str, Any]] = None) -> None:
        self._log("critical", message, fields)

    def EmitError(self, code: str, message: str, fatal: bool = False) -> None:
        self.emit_error(code, message, fatal)

    def Sleep(self, milliseconds: int) -> bool:
        """Sleep in a stop-aware way. Returns true when cancellation was requested."""
        return self._cancel.wait(max(0, milliseconds) / 1000.0)

    def WaitForStop(self, timeout_ms: Optional[int] = None) -> bool:
        timeout = None if timeout_ms is None else max(0, timeout_ms) / 1000.0
        return self._cancel.wait(timeout)

    def _begin_run(self, run_id: Optional[str]) -> None:
        with self._condition:
            self._run_id = run_id or uuid.uuid4().hex
            self._cancel.clear()

    def _cancel_run(self) -> None:
        with self._condition:
            self._cancel.set()
            self._condition.notify_all()

    def _receive_input(self, pin: str, sample: Sample | None) -> None:
        with self._condition:
            self._inputs[pin] = sample
            self._input_present.add(pin)
            self._condition.notify_all()


@dataclass
class _NodeState:
    node_type: str
    instance: Any
    context: NodeContext
    inbox: "queue.PriorityQueue[tuple[int, int, str, Any]]" = field(
        default_factory=lambda: queue.PriorityQueue(MAX_NODE_QUEUE)
    )
    thread: Optional[threading.Thread] = None
    lifecycle: str = "Created"
    running: bool = False


class WorkerRuntime:
    """One Package Worker. A Worker can host multiple node instances and node types."""

    def __init__(self, manifest_path: str | os.PathLike[str]) -> None:
        self.manifest_path = Path(manifest_path).resolve()
        self.package_root = self.manifest_path.parent
        self.manifest = json.loads(self.manifest_path.read_text(encoding="utf-8"))
        self.package_id = str(self.manifest.get("packageId") or "")
        self.package_version = str(self.manifest.get("version") or "")
        worker = self.manifest.get("worker") or {}
        self.protocol_version = int(worker.get("protocolVersion") or PROTOCOL_VERSION)
        self.heartbeat_interval_ms = int(worker.get("heartbeatIntervalMs") or 5000)
        self.max_line_bytes = int(worker.get("maxLineBytes") or 1024 * 1024)
        self.node_types = list(self.manifest.get("nodeTypes") or [])
        self.capabilities = list(worker.get("capabilities") or ["pin", "broadcast", "log", "lifecycle"])
        self._factories: dict[str, Callable[[], Any]] = {}
        self._nodes: dict[int, _NodeState] = {}
        self._nodes_lock = threading.RLock()
        self._message_counter = 0
        self._message_lock = threading.Lock()
        self._sequence: dict[int, int] = {}
        self._outbound: "queue.Queue[dict[str, Any] | None]" = queue.Queue(MAX_OUTBOUND_QUEUE)
        self._connection: Optional[DuplexConnection] = None
        self._worker_id = ""
        self._reader_thread: Optional[threading.Thread] = None
        self._writer_thread: Optional[threading.Thread] = None
        self._heartbeat_thread: Optional[threading.Thread] = None
        self._closed = threading.Event()
        self._ready = threading.Event()

    @classmethod
    def from_manifest(cls, manifest_path: str | os.PathLike[str]) -> "WorkerRuntime":
        return cls(manifest_path)

    @property
    def worker_id(self) -> str:
        return self._worker_id

    def register_node(self, node_type: str, node_class: Callable[[], Any]) -> None:
        if not node_type:
            raise ValueError("node_type must not be empty.")
        self._factories[node_type] = node_class

    def RegisterNode(self, node_type: str, node_class: Callable[[], Any]) -> None:
        self.register_node(node_type, node_class)

    def run_forever(self, argv: Optional[list[str]] = None) -> int:
        endpoint, token = self._parse_arguments(sys.argv[1:] if argv is None else argv)
        exit_code = 0
        try:
            self._connect_and_handshake(endpoint, token)
            self._writer_thread = threading.Thread(target=self._writer_loop, name="agentflow-writer", daemon=True)
            self._reader_thread = threading.Thread(target=self._reader_loop, name="agentflow-reader", daemon=True)
            self._writer_thread.start()
            self._reader_thread.start()
            ready = self._new_message("worker.ready")
            ready["workerId"] = self._worker_id
            ready["subscriptionsActive"] = True
            self._send(ready, blocking=True)
            self._ready.set()
            self._heartbeat_thread = threading.Thread(
                target=self._heartbeat_loop, name="agentflow-heartbeat", daemon=True
            )
            self._heartbeat_thread.start()
            self._reader_thread.join()
        except KeyboardInterrupt:
            exit_code = 130
        except Exception as exc:
            print(f"AgentFlow Worker failed: {exc}", file=sys.stderr, flush=True)
            exit_code = 1
        finally:
            self._shutdown_runtime()
        return exit_code

    def RunForever(self, argv: Optional[list[str]] = None) -> int:
        return self.run_forever(argv)

    def _parse_arguments(self, argv: list[str]) -> tuple[str, str]:
        endpoint = ""
        token = ""
        index = 0
        while index < len(argv):
            key = argv[index]
            if key == "--endpoint" and index + 1 < len(argv):
                endpoint = argv[index + 1]
                index += 2
            elif key == "--token" and index + 1 < len(argv):
                token = argv[index + 1]
                index += 2
            elif key == "--package-root" and index + 1 < len(argv):
                index += 2
            else:
                index += 1
        if not endpoint:
            raise AgentFlowError("Missing --endpoint argument.")
        return endpoint, token

    def _connect_and_handshake(self, endpoint: str, token: str) -> None:
        self._connection = DuplexConnection(endpoint, self.max_line_bytes)
        hello = self._new_message("worker.hello")
        hello["packageId"] = self.package_id
        hello["authToken"] = token
        hello["processId"] = os.getpid()
        hello["sdkVersion"] = SDK_VERSION
        hello["capabilities"] = self.capabilities
        self._connection.write_line(json.dumps(hello, ensure_ascii=False, separators=(",", ":")))
        welcome = self._read_handshake_message("worker.welcome")
        self._worker_id = str(welcome.get("workerId") or "")
        self.heartbeat_interval_ms = int(welcome.get("heartbeatIntervalMs") or self.heartbeat_interval_ms)
        self.max_line_bytes = int(welcome.get("maxLineBytes") or self.max_line_bytes)

        register = self._new_message("worker.register")
        register["packageId"] = self.package_id
        register["workerVersion"] = self.package_version
        register["processId"] = os.getpid()
        register["capabilities"] = self.capabilities
        register["nodeTypes"] = self._registration_node_types()
        self._connection.write_line(json.dumps(register, ensure_ascii=False, separators=(",", ":")))
        registered = self._read_handshake_message("worker.registered")
        if registered.get("workerId"):
            self._worker_id = str(registered["workerId"])

    def _registration_node_types(self) -> list[dict[str, Any]]:
        result = []
        for node_type in self.node_types:
            result.append({
                "nodeType": node_type.get("nodeType"),
                "displayName": node_type.get("displayName"),
                "category": node_type.get("category"),
                "pins": node_type.get("pins") or [],
                "parameters": node_type.get("parameters") or [],
                "subscriptions": node_type.get("subscriptions") or [],
            })
        return result

    def _read_handshake_message(self, expected_type: str) -> dict[str, Any]:
        assert self._connection is not None
        while True:
            line = self._connection.read_line()
            if line is None:
                raise AgentFlowError(f"Connection closed while waiting for {expected_type}.")
            message = json.loads(line)
            if message.get("protocolVersion") != PROTOCOL_VERSION:
                raise AgentFlowError(f"Unsupported protocolVersion: {message.get('protocolVersion')}")
            if message.get("type") == "error":
                raise AgentFlowError(f"{message.get('code')}: {message.get('message')}")
            if message.get("type") == expected_type:
                return message

    def _reader_loop(self) -> None:
        assert self._connection is not None
        try:
            while not self._closed.is_set():
                line = self._connection.read_line()
                if line is None:
                    break
                if not line.strip():
                    continue
                try:
                    message = json.loads(line)
                except json.JSONDecodeError as exc:
                    self._protocol_error("MALFORMED_JSON", str(exc), None)
                    continue
                self._dispatch(message)
        except Exception as exc:
            if not self._closed.is_set():
                print(f"AgentFlow reader stopped: {exc}", file=sys.stderr, flush=True)
        finally:
            self._closed.set()
            self._unblock_nodes()

    def _dispatch(self, message: dict[str, Any]) -> None:
        if message.get("protocolVersion") != PROTOCOL_VERSION:
            self._protocol_error("PROTOCOL_VERSION", "Unsupported protocol version.", message)
            return
        message_type = str(message.get("type") or "")
        if message_type == "worker.heartbeat":
            return
        if message_type == "node.create":
            self._create_node(message)
        elif message_type == "node.configure":
            self._enqueue_node_event(message, "configure", priority=0)
        elif message_type == "node.command":
            self._enqueue_node_event(message, "command", priority=0)
        elif message_type == "pin.receive":
            self._enqueue_node_event(message, "sample", priority=1)
        elif message_type == "broadcast.receive":
            self._enqueue_node_event(message, "broadcast", priority=1)
        elif message_type == "broadcast.subscribe":
            self._handle_subscription_request(message)
        elif message_type == "broadcast.unsubscribe":
            self._handle_unsubscribe_request(message)
        elif message_type == "error":
            print(f"Core protocol error {message.get('code')}: {message.get('message')}", file=sys.stderr)
        else:
            self._protocol_error("MESSAGE_UNKNOWN", f"Unknown Worker message type: {message_type}", message)

    def _create_node(self, message: dict[str, Any]) -> None:
        node_instance_id = int(message["nodeInstanceId"])
        node_type = str(message.get("nodeType") or "")
        correlation_id = str(message.get("messageId") or "")
        try:
            factory = self._factories.get(node_type)
            if factory is None:
                raise AgentFlowError(f"No node class registered for node type '{node_type}'.")
            parameters = message.get("parameters") or {}
            if not isinstance(parameters, dict):
                raise AgentFlowError("node.create parameters must be an object.")
            instance = factory()
            context = NodeContext(self, node_instance_id, node_type, parameters)
            state = _NodeState(node_type=node_type, instance=instance, context=context)
            self._call_optional(instance, "configure", context, dict(parameters))
            with self._nodes_lock:
                self._nodes[node_instance_id] = state
            state.thread = threading.Thread(
                target=self._node_executor_loop,
                args=(state,),
                name=f"agentflow-node-{node_instance_id}",
                daemon=True,
            )
            state.thread.start()
            response = self._new_message("node.created")
            response["correlationId"] = correlation_id
            response["nodeInstanceId"] = node_instance_id
            response["status"] = "ok"
            self._send(response, blocking=True)
        except Exception as exc:
            self._lifecycle_error_response(message, "node.created", "NODE_CREATE_FAILED", exc)

    def _enqueue_node_event(self, message: dict[str, Any], kind: str, priority: int) -> None:
        node_instance_id_value = message.get("nodeInstanceId")
        if node_instance_id_value is None:
            node_instance_id_value = message.get("targetNodeInstanceId")
        try:
            node_instance_id = int(node_instance_id_value)
        except Exception:
            self._protocol_error(
                "NODE_INSTANCE_ID",
                "Missing or invalid nodeInstanceId/targetNodeInstanceId.",
                message,
            )
            return
        with self._nodes_lock:
            state = self._nodes.get(node_instance_id)
        if state is None:
            self._protocol_error("NODE_UNKNOWN", f"Unknown node instance {node_instance_id}.", message)
            return
        sequence = self._next_message_number()
        try:
            state.inbox.put((priority, sequence, kind, message), block=False)
        except queue.Full:
            self._worker_error("NODE_QUEUE_FULL", f"Node {node_instance_id} inbox is full.", message)

    def _node_executor_loop(self, state: _NodeState) -> None:
        while not self._closed.is_set():
            try:
                _, _, kind, message = state.inbox.get(timeout=0.1)
            except queue.Empty:
                continue
            try:
                if kind == "command":
                    self._handle_command(state, message)
                elif kind == "configure":
                    self._handle_configure(state, message)
                elif kind == "sample":
                    self._handle_sample(state, message)
                elif kind == "broadcast":
                    self._handle_broadcast(state, message)
            except Exception as exc:
                self._report_callback_failure(state, kind, exc)

    def _handle_command(self, state: _NodeState, message: dict[str, Any]) -> None:
        command = str(message.get("command") or "").lower()
        correlation_id = str(message.get("messageId") or "")
        response_type = "node.command.result"
        try:
            if command == "init":
                self._call_optional(state.instance, "init", state.context)
                state.lifecycle = "Initialized"
            elif command == "run":
                state.context._begin_run(message.get("runId"))
                state.running = True
                state.lifecycle = "Running"
                self._send_state(state, "Running", "Initialized")
                self._call_optional(state.instance, "run", state.context)
            elif command == "stop":
                state.context._cancel_run()
                self._call_optional(state.instance, "stop", state.context)
                state.running = False
                state.lifecycle = "Stopped"
                self._send_state(state, "Stopped", "Running")
            elif command == "shutdown":
                state.context._cancel_run()
                self._call_optional(state.instance, "shutdown", state.context)
                state.running = False
                state.lifecycle = "Shutdown"
                self._send_state(state, "Shutdown", "Stopped")
            else:
                raise AgentFlowError(f"Unsupported lifecycle command '{command}'.")
            response = self._new_message(response_type)
            response["correlationId"] = correlation_id
            response["nodeInstanceId"] = state.context.node_instance_id
            response["command"] = command
            response["status"] = "ok"
            self._send(response, blocking=True)
        except Exception as exc:
            response = self._new_message(response_type)
            response["correlationId"] = correlation_id
            response["nodeInstanceId"] = state.context.node_instance_id
            response["command"] = command
            response["status"] = "error"
            response["code"] = f"NODE_{command.upper()}_FAILED"
            response["message"] = str(exc)
            self._send(response, blocking=True)

    def _handle_configure(self, state: _NodeState, message: dict[str, Any]) -> None:
        correlation_id = str(message.get("messageId") or "")
        try:
            parameters = message.get("parameters") or {}
            if not isinstance(parameters, dict):
                raise AgentFlowError("node.configure parameters must be an object.")
            state.context.params = dict(parameters)
            self._call_optional(state.instance, "configure", state.context, state.context.params)
            response = self._new_message("node.configure.result")
            response["correlationId"] = correlation_id
            response["nodeInstanceId"] = state.context.node_instance_id
            response["status"] = "ok"
            self._send(response, blocking=True)
        except Exception as exc:
            response = self._new_message("node.configure.result")
            response["correlationId"] = correlation_id
            response["nodeInstanceId"] = state.context.node_instance_id
            response["status"] = "error"
            response["code"] = "NODE_CONFIGURE_FAILED"
            response["message"] = str(exc)
            self._send(response, blocking=True)

    def _handle_sample(self, state: _NodeState, message: dict[str, Any]) -> None:
        pin = str(message.get("targetPin") or "")
        if not pin:
            raise AgentFlowError("pin.receive is missing targetPin.")
        run_id = message.get("runId")
        if run_id and state.context._run_id and run_id != state.context._run_id:
            return
        sample = Sample.from_wire(message.get("sample"), None, pin)
        state.context._receive_input(pin, sample)
        self._call_optional(state.instance, "on_sample", state.context, pin, sample)

    def _handle_broadcast(self, state: _NodeState, message: dict[str, Any]) -> None:
        topic = str(message.get("topic") or "")
        payload = message.get("payload")
        self._call_optional(state.instance, "on_broadcast", state.context, topic, payload)

    def _handle_subscription_request(self, message: dict[str, Any]) -> None:
        response = self._new_message("broadcast.subscription")
        response["correlationId"] = str(message.get("messageId") or "")
        response["nodeInstanceId"] = message.get("nodeInstanceId")
        response["topic"] = message.get("topic")
        response["status"] = "ok"
        self._send(response, blocking=True)

    def _handle_unsubscribe_request(self, message: dict[str, Any]) -> None:
        response = self._new_message("broadcast.subscription")
        response["correlationId"] = str(message.get("messageId") or "")
        response["nodeInstanceId"] = message.get("nodeInstanceId")
        response["topic"] = message.get("topic")
        response["status"] = "ok"
        self._send(response, blocking=True)

    def _call_optional(self, instance: Any, method_name: str, *args: Any) -> Any:
        pascal_names = {
            "configure": "Configure",
            "init": "Init",
            "run": "Run",
            "stop": "Stop",
            "shutdown": "Shutdown",
            "on_sample": "OnSample",
            "on_broadcast": "OnBroadcast",
        }
        method = getattr(instance, pascal_names.get(method_name, method_name), None)
        if method is None:
            method = getattr(instance, method_name, None)
        if method is None:
            return None
        result = method(*args)
        if inspect.isawaitable(result):
            raise AgentFlowError(
                f"{type(instance).__name__}.{method_name} returned awaitable; first version uses synchronous callbacks."
            )
        return result

    def _send_state(self, state: _NodeState, current: str, previous: str) -> None:
        message = self._new_message("node.state")
        message["nodeInstanceId"] = state.context.node_instance_id
        message["state"] = current
        message["previousState"] = previous
        self._send(message, blocking=True)

    def _report_callback_failure(self, state: _NodeState, kind: str, exc: Exception) -> None:
        error = self._new_message("node.error")
        error["nodeInstanceId"] = state.context.node_instance_id
        error["code"] = f"NODE_{kind.upper()}_FAILED"
        error["severity"] = "error"
        error["message"] = str(exc)
        error["fatal"] = True
        error["exceptionType"] = type(exc).__name__
        self._send(error, blocking=False)
        state.lifecycle = "Failed"
        print(f"Node {state.context.node_instance_id} {kind} failed: {exc}", file=sys.stderr, flush=True)

    def _lifecycle_error_response(
        self,
        request: dict[str, Any],
        response_type: str,
        code: str,
        exc: Exception,
    ) -> None:
        response = self._new_message(response_type)
        response["correlationId"] = str(request.get("messageId") or "")
        response["nodeInstanceId"] = request.get("nodeInstanceId")
        response["status"] = "error"
        response["code"] = code
        response["message"] = str(exc)
        self._send(response, blocking=True)

    def _protocol_error(self, code: str, message_text: str, request: Optional[dict[str, Any]]) -> None:
        message = self._new_message("error")
        message["code"] = code
        message["severity"] = "error"
        message["message"] = message_text
        message["fatal"] = False
        if request:
            message["correlationId"] = request.get("messageId")
            message["nodeInstanceId"] = request.get("nodeInstanceId")
        self._send(message, blocking=False)

    def _worker_error(self, code: str, message_text: str, request: dict[str, Any]) -> None:
        message = self._new_message("error")
        message["code"] = code
        message["severity"] = "error"
        message["message"] = message_text
        message["fatal"] = False
        message["correlationId"] = request.get("messageId")
        message["nodeInstanceId"] = request.get("nodeInstanceId")
        self._send(message, blocking=False)

    def _writer_loop(self) -> None:
        while True:
            message = self._outbound.get()
            if message is None:
                return
            try:
                assert self._connection is not None
                self._connection.write_line(json.dumps(message, ensure_ascii=False, separators=(",", ":")))
            except Exception as exc:
                if not self._closed.is_set():
                    print(f"AgentFlow writer stopped: {exc}", file=sys.stderr, flush=True)
                self._closed.set()
                self._unblock_nodes()
                return

    def _heartbeat_loop(self) -> None:
        interval = max(1.0, self.heartbeat_interval_ms / 1000.0)
        while not self._closed.wait(interval):
            message = self._new_message("worker.heartbeat")
            message["workerId"] = self._worker_id
            message["timestampUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            message["sequence"] = self._next_message_number()
            try:
                self._send(message, blocking=False)
            except OutboundQueueFull:
                return

    def _new_message(self, message_type: str) -> dict[str, Any]:
        return {
            "protocolVersion": PROTOCOL_VERSION,
            "type": message_type,
            "messageId": self._next_message_id(),
        }

    def _next_message_id(self) -> str:
        with self._message_lock:
            return f"worker-{self.package_id}-{uuid.uuid4().hex}"

    def _next_message_number(self) -> int:
        with self._message_lock:
            self._message_counter += 1
            return self._message_counter

    def _next_sequence(self, node_instance_id: int) -> int:
        with self._message_lock:
            value = self._sequence.get(node_instance_id, 0) + 1
            self._sequence[node_instance_id] = value
            return value

    def _send(self, message: dict[str, Any], blocking: bool) -> None:
        try:
            if blocking:
                self._outbound.put(message, timeout=5.0)
            else:
                self._outbound.put_nowait(message)
        except queue.Full as exc:
            raise OutboundQueueFull("Worker outbound queue is full.") from exc

    def _unblock_nodes(self) -> None:
        with self._nodes_lock:
            states = list(self._nodes.values())
        for state in states:
            state.context._cancel_run()
            try:
                state.inbox.put((0, self._next_message_number(), "close", {}), block=False)
            except queue.Full:
                pass
        try:
            self._outbound.put_nowait(None)
        except queue.Full:
            pass

    def _shutdown_runtime(self) -> None:
        self._closed.set()
        self._unblock_nodes()
        if self._connection is not None:
            try:
                self._connection.close()
            except Exception:
                pass
        current = threading.current_thread()
        for thread in (self._reader_thread, self._writer_thread, self._heartbeat_thread):
            if thread and thread is not current and thread.is_alive():
                thread.join(timeout=5.0)
        with self._nodes_lock:
            states = list(self._nodes.values())
        for state in states:
            if state.thread and state.thread is not current:
                state.thread.join(timeout=5.0)




