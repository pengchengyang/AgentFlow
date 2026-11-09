# TCP Transport Nodes

This document describes the byte-oriented behavior of `Node.TcpClient` and
`Node.TcpServer`.

## Design rule

TCP is a byte stream. These demo nodes deliberately act like a generic TCP terminal
such as NetAssist: they transmit and receive raw bytes and do not define a business
protocol, framing, delimiters, JSON envelope, or automatic reply.

A single `ReadAsync` result is one runtime block. TCP does not preserve application
message boundaries, so downstream protocol nodes must implement any required packet
framing, length headers, checksums, or reassembly rules.

## Pins

Both nodes expose these common data pins:

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string` | Input | STRING | UTF-8 encodes the received string and sends those bytes. |
| `input_binary` | Input | BIN | Sends the received byte array unchanged. |
| `output_string` | Output | STRING | UTF-8 decodes each received byte block for text debugging. |
| `output_binary` | Output | BIN | Emits each received byte block unchanged; recommended for production pipelines. |

`TCP Client` also exposes:

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_endpoint` | Input | STRING | Changes the active connection target using `host:port` format. |

Output pins publish only when they have a downstream connection. The UTF-8 output is
a debugging view and can produce replacement characters if a multi-byte text character
is split across two read blocks.

## TCP client behavior

`TCP Client` starts a connection supervisor during `Run`. The supervisor connects to
the configured `Host:Port` and starts one read loop and one write loop. When the connection
ends, the node closes the current socket, waits for the configured reconnect delay, and
connects again until `Stop` is called. The node remains `Running = true` while reconnecting.
Input blocks are queued for transmission. No payload is sent automatically at startup.

The `input_string_endpoint` pin accepts values such as `192.168.2.1:8900`. A valid update
changes only the runtime connection target; the workflow `Host` and `Port` parameters remain
unchanged and are not persisted with the new value. If the client is already connected or
connecting, the supervisor abandons the current endpoint and reconnects to the new target.
An endpoint update triggers a new connection attempt even when `AutoReconnect` is disabled.
Invalid values are logged and ignored, leaving the existing connection target unchanged.

## TCP server behavior

`TCP Server` listens on the configured bind address and port. It accepts multiple clients
up to `MaxClients`.

- A value received on `input_string` or `input_binary` is broadcast to every client that
  is connected when the broadcast loop processes it.
- Each block received from a client is published once to the server's output pins.
- Received client data is not automatically echoed or broadcast to other clients.

## Parameters

| Parameter | Default | Meaning |
| --- | --- | --- |
| `Host` | `127.0.0.1` client / `0.0.0.0` server | Initial remote endpoint or local bind address. |
| `Port` | `9000` | Initial TCP port. For the client, `input_string_endpoint` can override it at runtime without changing this parameter. |
| `ConnectTimeoutSeconds` | `5` | Per-attempt client connect timeout. |
| `MaxConnectAttempts` | `5` | Client attempts per connection cycle before entering the reconnect delay. |
| `AutoReconnect` | `true` | Keep retrying after a failed or closed connection until stopped. |
| `ReconnectDelaySeconds` | `1` | Initial reconnect delay. |
| `ReconnectMaxDelaySeconds` | `30` | Maximum reconnect delay after exponential backoff. |
| `MaxClients` | `32` | Server active-client limit. |
| `SendQueueCapacity` | `256` | Bounded outbound or broadcast queue capacity. |
| `ReadTimeoutSeconds` | `0` | Reads time out after this interval; `0` disables the timeout. |
| `WriteTimeoutSeconds` | `10` | Per-write timeout. |
| `ReceiveBufferSize` | `4096` | Size of the buffer used for each network read. |

## Lifecycle

- `Initialize` validates parameters and prepares queues; the server also binds its listener.
- `Run` starts the client connection supervisor or server loops and sets `Running = true`.
- `Stop` is idempotent, cancels loops, closes streams and sockets, waits briefly for tasks,
  and returns the node to idle.
- There is no TLS, heartbeat, or built-in packet protocol.
