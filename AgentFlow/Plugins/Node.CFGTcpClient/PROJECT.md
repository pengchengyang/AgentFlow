# Node.CFGTcpClient

## Purpose

`Node.CFGTcpClient` is the CFG-side persistent TCP session client. It authenticates using a
runtime GUID received on `input_string_guid`, maintains a reconnecting session, sends PING
heartbeats, caches business envelopes while waiting or disconnected, and transports complete
JSON envelopes inside the shared `;JSON;` frame.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_guid` | Input | STRING | Runtime GUID used for authentication. A valid value enables connection startup; invalid values are rejected. It does not modify the persisted `ClientGuid` parameter. |
| `input_string_endpoint` | Input | STRING | Runtime `host:port` target. In `WaitForEndpoint` mode the first valid value completes the connection prerequisite; later values reconnect. It does not modify persisted `ServerHost` or `ServerPort`. |
| `input_string_message` | Input | STRING | Accepts either a BUSINESS JSON object with a `DATA` object or a complete `RUN_ACK` protocol envelope. BUSINESS input is stamped and validated by the client; `RUN_ACK` is validated against the authenticated session and queued unchanged. |
| `output_string_message` | Output | STRING | Emits the original complete JSON envelope for every authenticated TYPE except `AUTH`, `AUTH_OK`, `AUTH_FAIL`, `PING`, and `PONG`. `DATA.MESSAGES` is neither extracted nor used as a forwarding gate. |

## Identity

`ClientGuid` is an optional fallback parameter and is never required for initialization. The
authoritative runtime identity comes from `input_string_guid`; the client does not start a
connection until a valid runtime GUID has been received. If the parameter contains a valid GUID,
it is shown as a fallback in logs but still does not bypass the runtime input requirement.

The server must list the runtime GUID in its client configuration and maps it to `CFG-1` or
`CFG-2`. The client reads the authoritative `LOCATION` from `AUTH_OK` and includes it in every
authenticated envelope.

## Connection Mode

`ConnectMode` is a single-select parameter:

| Value | Behavior |
| --- | --- |
| `Immediate` | Default. After a valid `input_string_guid` arrives, connect to the configured `ServerHost:ServerPort` or the latest runtime endpoint. |
| `WaitForEndpoint` | Wait for both a valid `input_string_guid` and the first valid `input_string_endpoint` value before connecting. |

`Run` sets `Running = true` while the node waits for its connection prerequisites. `Running`
therefore means the node lifecycle is active, not that the remote server is connected or
authenticated. In either mode, `input_string_endpoint` only changes the active runtime target and
never writes back to the persisted `ServerHost` or `ServerPort` parameters.

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

- Initialization succeeds even when `ClientGuid` is empty or invalid; the node waits for `input_string_guid`.
- A valid runtime GUID is required before the connection supervisor starts. A later valid GUID cancels the active connection attempt and reconnects with the new identity.
- Business input received before the first valid GUID is retained in the pending input queue and processed after the GUID arrives, so envelopes are never stamped with an empty identity.
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
- A valid `RUN` is forwarded unchanged to `output_string_message`; the client does not generate `RUN_ACK` itself.
- `RUN_ACK` produced by AutoRunStop is accepted on `input_string_message`, validated against the current GUID, LOCATION, VERSION, and protocol envelope shape, then sent unchanged to CFGS.
- Connection state is published on `CFGSTcpProtocolBroadcasts.CFGTcpClientConnectionStatus`.

## Build

```powershell
dotnet build "Plugins/Node.CFGTcpClient/Node.CFGTcpClient.csproj" -c Debug -p:Platform=x64
```

