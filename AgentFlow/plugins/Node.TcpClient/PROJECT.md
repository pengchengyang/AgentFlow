# Node.TcpClient

## Purpose

`Node.TcpClient` is a raw byte-stream TCP client node. It connects to a remote endpoint,
forwards bytes from its two data input pins, and publishes each received block as binary
and UTF-8 string samples. It does not add framing, delimiters, checksums, or automatic
payloads.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_endpoint` | Input | STRING | Runtime-only endpoint change in `host:port` format. |
| `input_string` | Input | STRING | UTF-8 encodes the value and sends the bytes. |
| `input_binary` | Input | BIN | Sends the byte array unchanged. |
| `output_string` | Output | STRING | UTF-8 decodes received bytes for debugging. |
| `output_binary` | Output | BIN | Emits received bytes unchanged. |

## Dynamic Endpoint

A valid value such as `192.168.2.1:8900` updates only the active runtime connection target.
It does not modify the persisted `Host` or `Port` parameters. If the node is running, the
connection supervisor cancels the current connect/read/write cycle and reconnects to the new
endpoint. Invalid values are logged and ignored.

The endpoint change path intentionally bypasses `AutoReconnect = false` for one reconnect so
the requested runtime target becomes active. Subsequent connection failures still follow the
configured auto-reconnect policy.

## Lifecycle

`Initialize` validates parameters and creates the send and reconnect-request queues.
`Run` starts the connection supervisor. `Stop` cancels the supervisor, completes queues,
closes streams and sockets, and resets node state.

## Build

```powershell
dotnet build "AgentFlow/plugins/Node.TcpClient/Node.TcpClient.csproj" -c Debug -p:Platform=x64
```
