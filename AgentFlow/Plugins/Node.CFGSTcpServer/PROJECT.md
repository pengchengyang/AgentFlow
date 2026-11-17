# Node.CFGSTcpServer

## Purpose

`Node.CFGSTcpServer` is the CFGS-side TCP session server. It provides GUID authentication,
one active session per configured GUID, target-GUID routing, bounded offline queuing, PING/PONG
heartbeat, and `;JSON;` envelope framing. Business `DATA` is preserved as an opaque JSON object.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_client_config` | Input | STRING | Accepts the client whitelist JSON before `Run`. Replacing config while running is ignored. |
| `input_string_message` | Input | STRING | Accepts a complete BUSINESS JSON envelope. The `GUID` selects the destination session. |
| `output_string_message` | Output | STRING | Emits complete BUSINESS envelopes received from authenticated clients. |

## Client Configuration

```json
{
  "version": "1.0",
  "clients": [
    { "role": "LOCAL_CFG", "guid": "11111111-2222-3333-4444-555555555555" },
    { "role": "REMOTE_CFG", "guid": "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE" }
  ]
}
```

The GUID count is the authenticated-client limit. Unknown or duplicate GUIDs are rejected.
The server never broadcasts a business message to all clients.

## Envelope Contract

All frames use the shared semicolon delimiter:

```text
;{"GUID":"...","TYPE":"BUSINESS","VERSION":"1.0","SEQ":1,"TIMESTAMP":"...","DATA":{...}};
```

Supported `TYPE` values are `AUTH`, `AUTH_OK`, `AUTH_FAIL`, `BUSINESS`, `PING`, `PONG`, and
`DISCONNECT`. The node validates the top-level envelope but does not interpret `DATA`.

## Runtime Behavior

- The configured listener binds when `Run` starts. A bind failure fails the node with an error.
- An offline target is placed into a bounded per-GUID queue with TTL; reconnect drains it FIFO.
- Online writes use a bounded server send queue.
- Authenticated sessions time out when no traffic is seen within `HeartbeatTimeoutSeconds`.
- Connection state is published on `CFGSTcpProtocolBroadcasts.CFGSServerConnectionStatus`.

## Build

```powershell
dotnet build "plugins/Node.CFGSTcpServer/Node.CFGSTcpServer.csproj" -c Debug -p:Platform=x64
```
