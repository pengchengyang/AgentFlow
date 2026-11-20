# Node.CFGSTcpServer

## Purpose

`Node.CFGSTcpServer` is the CFGS-side TCP session server. It provides GUID authentication,
one active session per configured GUID, target-GUID routing, bounded offline queuing, PING/PONG
heartbeat, `;JSON;` envelope framing, GUID-to-LOCATION validation, authenticated forwarding, and target-GUID routing.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_client_config` | Input | STRING | Accepts the client whitelist JSON before `Run`. Replacing config while running is ignored. |
| `input_string_message` | Input | STRING | Accepts a complete JSON envelope. BUSINESS, RUN, and RUN_ACK messages require a matching `LOCATION`; the `GUID` selects the destination session. |
| `output_string_message` | Output | STRING | Emits the original complete JSON envelope for every authenticated TYPE except `AUTH`, `AUTH_OK`, `AUTH_FAIL`, `PING`, and `PONG`. `DATA.MESSAGES` is neither extracted nor used as a forwarding gate. |

## Client Configuration

```json
{
  "version": "1.0",
  "clients": [
    { "LOCATION": "CFG-1", "GUID": "11111111-2222-3333-4444-555555555555" },
    { "LOCATION": "CFG-2", "GUID": "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE" }
  ]
}
```

The GUID count is the authenticated-client limit. Unknown or duplicate GUIDs are rejected.
`ROLE` is accepted as a legacy alias when `LOCATION` is absent; both values must match when supplied
together. The server never broadcasts a business message to all clients.

## Envelope Contract

All frames use the shared semicolon delimiter:

```text
;{"GUID":"...","LOCATION":"CFG-1","TYPE":"BUSINESS","VERSION":"1.0","SEQ":1,"TIMESTAMP":"...","DATA":{...}};
```

Supported control `TYPE` values are `AUTH`, `AUTH_OK`, `AUTH_FAIL`, `PING`, and `PONG`.
Application-level values currently include `RUN`, `RUN_ACK`, `BUSINESS`, and `DISCONNECT`. The node
validates the top-level envelope but does not interpret `DATA` while forwarding received messages.

## Runtime Behavior

- The configured listener binds when `Run` starts. A bind failure fails the node with an error.
- Authenticated envelopes must carry LOCATION matching the GUID configuration.
- Outgoing BUSINESS input must contain one to five records for the target LOCATION. `CFG-1` uses SLOT 1-5 and `CFG-2` uses SLOT 6-10. Slots must be unique within one envelope but do not need to cover the full range. Each record contains SLOT, FSN, IMEI, and STATUS; STATUS is IDLE, TEST, OK, or NG.
- Every authenticated received envelope whose TYPE is not `AUTH`, `AUTH_OK`, `AUTH_FAIL`, `PING`, or `PONG` is forwarded unchanged to `output_string_message`. This includes `RUN`, `RUN_ACK`, `BUSINESS`, `DISCONNECT`, and future non-control types. `DATA.MESSAGES` is optional for forwarding.
- Outgoing `RUN` and `RUN_ACK` envelopes require a matching `LOCATION` before they are queued or sent.
- An offline target is placed into a bounded per-GUID queue with TTL; reconnect drains it FIFO.
- Online writes use a bounded server send queue.
- Authenticated sessions time out when no traffic is seen within `HeartbeatTimeoutSeconds`.
- Connection state is published on `CFGSTcpProtocolBroadcasts.CFGSServerConnectionStatus`.

## Build

```powershell
dotnet build "Plugins/Node.CFGSTcpServer/Node.CFGSTcpServer.csproj" -c Debug -p:Platform=x64
```
