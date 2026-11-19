# Node.CFGTcpClient

## Purpose

`Node.CFGTcpClient` is the CFG-side persistent TCP session client. It authenticates using its
local GUID, maintains a reconnecting session, sends PING heartbeats, caches business envelopes
while disconnected, and transports complete JSON envelopes inside the shared `;JSON;` frame.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_endpoint` | Input | STRING | Runtime `host:port` target. In `WaitForEndpoint` mode the first valid value starts connecting; later values reconnect. It does not modify persisted `ServerHost` or `ServerPort`. |
| `input_string_message` | Input | STRING | Accepts a BUSINESS JSON object with a `DATA` object. The client stamps its own GUID, authenticated LOCATION, VERSION, SEQ, and TIMESTAMP, and validates the one-to-five-record `MESSAGES` contract. |
| `output_string_message` | Output | STRING | Emits the original complete JSON envelope for every authenticated TYPE except `AUTH`, `AUTH_OK`, `AUTH_FAIL`, `PING`, and `PONG`. `DATA.MESSAGES` is neither extracted nor used as a forwarding gate. |

## Identity

`ClientGuid` is a required GUID parameter. The server must list the same GUID in its client
configuration and maps it to `CFG-1` or `CFG-2`. The client reads the authoritative `LOCATION`
from `AUTH_OK` and includes it in every authenticated envelope.

## Connection Mode

`ConnectMode` is a single-select parameter:

| Value | Behavior |
| --- | --- |
| `Immediate` | Default. `Run` starts connecting to `ServerHost:ServerPort` immediately. |
| `WaitForEndpoint` | `Run` waits for the first valid `input_string_endpoint` value before connecting. |

In either mode, `input_string_endpoint` only changes the active runtime target and never writes
back to the persisted `ServerHost` or `ServerPort` parameters.

## Authentication Timeout

`AuthTimeoutSeconds` limits one individual `AUTH` attempt. `AuthenticationTimeoutMilliseconds`
limits the entire time the client may remain unauthenticated while it retries, including failed
connections, failed `AUTH` attempts, and reconnect delays.

- Default: `60000`.
- On successful authentication the deadline is cleared.
- A later session loss starts a new authentication recovery deadline.
- When the deadline expires, the node logs an error, sets `Running` to `false`, and lets the
  AgentFlow Core health monitor fail the workflow and stop it automatically.

## Runtime Behavior

- Uses `ServerHost:ServerPort` as the initial target in `Immediate` mode and as the fallback shown while waiting in `WaitForEndpoint` mode.
- Reconnects after failures while the authentication deadline has not expired.
- Sends `AUTH` first and waits for `AUTH_OK` within `AuthTimeoutSeconds`.
- Sends `PING` every `HeartbeatIntervalSeconds`; no inbound traffic for
  `HeartbeatTimeoutSeconds` ends the session and starts reconnect handling.
- Business input received while disconnected is retained in a bounded FIFO queue and logged as
  an error. The queue drains after successful authentication.
- Inbound envelopes with a different GUID or LOCATION are ignored.
- Business input must contain one to five records for the configured LOCATION. `CFG-1` uses SLOT 1-5 and `CFG-2` uses SLOT 6-10. Slots must be unique within one envelope but do not need to cover the full range. Each record contains SLOT, FSN, IMEI, and STATUS; STATUS is IDLE, TEST, OK, or NG.
- Every authenticated received envelope whose TYPE is not `AUTH`, `AUTH_OK`, `AUTH_FAIL`, `PING`, or `PONG` is forwarded unchanged to `output_string_message`. The node does not require `DATA.MESSAGES` to exist for forwarding.
- Connection state is published on `CFGSTcpProtocolBroadcasts.CFGTcpClientConnectionStatus`.

## Build

```powershell
dotnet build "Plugins/Node.CFGTcpClient/Node.CFGTcpClient.csproj" -c Debug -p:Platform=x64
```

