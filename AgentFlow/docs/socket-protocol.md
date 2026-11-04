# Socket Node Protocol — Version 1

This document is the contract shared by `Node.SocketClient` and `Node.SocketServer`.
The wire format is deliberately small and compatible with simple line-based peers.

## Transport

- TCP, one persistent connection per client.
- UTF-8 text.
- One logical message per line, terminated by `\n`.
- `\r\n` is accepted on input; `\r` is stripped by line readers.
- Empty lines are ignored.
- Send-side embedded `\r` / `\n` characters are flattened to spaces, so one logical
  message always remains one line.
- Maximum message length is **8192 characters**. Longer lines are dropped and logged.

## Version

- Wire protocol version: **1**.
- There is no version handshake in v1; both nodes log `protocol v1` at startup.
- Any future wire-breaking change must bump the version in the nodes and this document.
  Backward-compatible additions (new parameters, new statistics) keep version 1.

## Client → Server

1. After connecting, the client sends its configured `Payload` as the first line.
2. Values arriving on the `Send` input pin are queued and sent as additional lines.
3. The client does not echo `Send` values to its own `Received` output.

## Server → Client

1. For every received line, the server replies on the same connection.
2. If any of `Send1..Send4` is non-empty, each non-empty value is sent as one reply line.
3. Otherwise the non-empty `Response` parameter is sent as one reply line.
4. An empty reply set means no response is sent.

## Timeouts and limits

| Setting | Default | Meaning |
|---|---|---|
| `ConnectTimeoutSeconds` | 5 | Per-attempt client connect timeout. |
| `MaxConnectAttempts` | 5 | Bounded retry count, 1s between attempts. |
| `ReadTimeoutSeconds` | 0 / 60 | Client 0 = disabled; server 60 = idle client disconnect. |
| `WriteTimeoutSeconds` | 10 | Per-write timeout on both nodes. |
| `MaxClients` | 32 | Server rejects connections beyond this active session count. |
| `SendQueueCapacity` | 256 | Client outbound queue; when full, new messages are dropped and counted. |

The server logs a stop summary with accepted, rejected, sent, received, dropped, and error
counts. The client logs sent, received, and dropped counts.

## Lifecycle

- `Initialize` validates Host/Port and binds the listener.
- `Run` starts the accept/read/write loops; `Stop` cancels them, closes sockets, waits for
  tracked tasks, and is idempotent.
- Unexpected loss of connection marks the node `Failed`; an intentional stop returns it to
  `Idle` while preserving a previous failure state.
- No TLS, heartbeat, reconnect state machine, or binary framing is part of v1.
