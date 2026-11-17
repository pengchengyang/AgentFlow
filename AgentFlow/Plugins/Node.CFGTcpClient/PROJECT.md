# Node.CFGTcpClient

## Purpose

`Node.CFGTcpClient` is the CFG-side persistent TCP session client. It authenticates using its
local GUID, maintains a reconnecting session, sends PING heartbeats, caches business envelopes
while disconnected, and transports complete JSON envelopes inside the shared `;JSON;` frame.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_endpoint` | Input | STRING | Runtime `host:port` target. In `WaitForEndpoint` mode the first valid value starts connecting; later values reconnect. It does not modify persisted `ServerHost` or `ServerPort`. |
| `input_string_message` | Input | STRING | Accepts a BUSINESS JSON object with a `DATA` object. The client stamps its own GUID, VERSION, SEQ, and TIMESTAMP. |
| `output_string_message` | Output | STRING | Emits complete BUSINESS envelopes whose target GUID matches this client. |

## Identity

`ClientGuid` is a required GUID parameter. The server must list the same GUID in its client
configuration and assigns the matching `ROLE` after `AUTH_OK`.

## Connection Mode

`ConnectMode` is a single-select parameter:

| Value | Behavior |
| --- | --- |
| `Immediate` | Default. `Run` starts connecting to `ServerHost:ServerPort` immediately. |
| `WaitForEndpoint` | `Run` waits for the first valid `input_string_endpoint` value before connecting. |

In either mode, `input_string_endpoint` only changes the active runtime target and never writes
back to the persisted `ServerHost` or `ServerPort` parameters.

## Runtime Behavior

- Uses `ServerHost:ServerPort` as the initial target in `Immediate` mode and as the fallback shown while waiting in `WaitForEndpoint` mode.
- Reconnects after failures.
- Sends `AUTH` first and waits for `AUTH_OK` within `AuthTimeoutSeconds`.
- Sends `PING` every `HeartbeatIntervalSeconds`; no inbound traffic for
  `HeartbeatTimeoutSeconds` ends the session and starts reconnect handling.
- Business input received while disconnected is retained in a bounded FIFO queue and logged as
  an error. The queue drains after successful authentication.
- Inbound envelopes with a different GUID are ignored.
- Connection state is published on `CFGSTcpProtocolBroadcasts.CFGTcpClientConnectionStatus`.

## Build

```powershell
dotnet build "plugins/Node.CFGTcpClient/Node.CFGTcpClient.csproj" -c Debug -p:Platform=x64
```

