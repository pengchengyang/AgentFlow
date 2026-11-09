# Node.TcpClient

## Purpose

`Node.TcpClient` is a raw byte-stream TCP client node. It connects to a remote endpoint,
forwards bytes from its two data input pins, and publishes each received block as binary
and UTF-8 string samples. It does not add framing, delimiters, checksums, or automatic
payloads.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_endpoint` | Input | STRING | Runtime endpoint in `host:port` format. It controls when to connect in `WaitForEndpoint` mode and can change the active target while running. |
| `input_string` | Input | STRING | UTF-8 encodes the value and sends the bytes. |
| `input_binary` | Input | BIN | Sends the byte array unchanged. |
| `output_string` | Output | STRING | UTF-8 decodes received bytes for debugging. |
| `output_binary` | Output | BIN | Emits received bytes unchanged. |

## Connection Mode

`ConnectMode` is a single-select parameter in the `Connection` group:

| Value | Behavior |
| --- | --- |
| `Immediate` | Default. `Run` immediately starts connecting to the configured `Host` and `Port`. |
| `WaitForEndpoint` | `Run` does not connect until a valid `input_string_endpoint` value has arrived. |

In `WaitForEndpoint` mode:

- The first valid endpoint received after `Run` starts the connection supervisor.
- An endpoint received before `Run` is cached and used when `Run` starts.
- Invalid values are logged as errors and do not satisfy the wait.
- If the endpoint pin is not connected, the node remains running but waits indefinitely for an
  endpoint input. A warning is recorded during `Initialize`.
- If the first connection attempt fails, the node remains running and retries according to
  `AutoReconnect` when `AutoReconnect = true`. With `AutoReconnect = false`, the node marks
  itself failed and stops.

## Dynamic Endpoint

A valid value such as `192.168.2.1:8900` updates only the active runtime connection target.
It does not modify the persisted `Host` or `Port` parameters. IPv6 endpoints can use bracket
notation, for example `[::1]:8900`.

If the node is running, an endpoint change cancels the current connect/read/write cycle and
reconnects to the new endpoint. Endpoint changes also interrupt the normal auto-reconnect delay
so the new target is tried immediately.

The endpoint change path intentionally bypasses `AutoReconnect = false` for one reconnect so
the requested runtime target becomes active. Subsequent connection failures still follow the
configured auto-reconnect policy.

## Lifecycle

`Initialize` validates parameters, normalizes `ConnectMode`, creates the send and
reconnect-request queues, and resets runtime endpoint state.

`Run` starts the connection supervisor immediately in `Immediate` mode. In
`WaitForEndpoint` mode it marks the node running and waits for the first valid endpoint input.

`Stop` cancels the supervisor, completes queues, closes streams and sockets, and resets runtime
endpoint state.

## Build

```powershell
dotnet build "AgentFlow/plugins/Node.TcpClient/Node.TcpClient.csproj" -c Debug -p:Platform=x64
```
