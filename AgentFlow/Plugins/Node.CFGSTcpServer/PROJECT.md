# Node.CFGSTcpServer

## Purpose

`Node.CFGSTcpServer` is the CFGS-side TCP session server. It provides GUID authentication,
one active session per configured GUID, target-GUID routing, bounded offline queuing, PING/PONG
heartbeat, `;JSON;` envelope framing, GUID-to-LOCATION validation, authenticated forwarding, and target-GUID routing.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_client_config` | Input | STRING | Accepts the JSON produced by `Node.ReadIniFile`. The first valid payload may arrive before or after `Run`; later replacements are ignored. |
| `input_string_endpoint` | Input | STRING | Optional runtime listen port. In `WaitForEndpoint` mode the server remains running until both configuration and a valid port are available. |
| `input_string_message` | Input | STRING | Accepts a complete JSON envelope. BUSINESS, RUN, and RUN_ACK messages require a matching `LOCATION`; the `GUID` selects the destination session. |
| `output_string_message` | Output | STRING | Emits the original complete JSON envelope for every authenticated TYPE except `AUTH`, `AUTH_OK`, `AUTH_FAIL`, `PING`, and `PONG`. `DATA.MESSAGES` is neither extracted nor used as a forwarding gate. |

## Client Configuration

The primary input is the flat JSON emitted by `Node.ReadIniFile` after reading
`C:\RTSResources\UserSettingsCFGS\UserSettingsCFGS.ini`:

```json
{
  "GUID.CFG-1": ["CA7EFFA7-A2EB-4AC0-864A-1E1100D40B90"],
  "GUID.CFG-2": ["CA7EFFA7-A2EB-4AC0-864A-1E1100D40B92"],
  "PORT.LISTEN": ["9000"],
  "SLOT.SLOTS": ["1,2,3,4,5,6,7,8,9,10"],
  "IP.CFG-1": ["127.0.0.1"],
  "IP.CFG-2": ["10.22.43.62"],
  "NAME.CFG-1": ["admin"],
  "NAME.CFG-2": ["damon"],
  "PASSWORD.CFG-1": ["Abc911226."],
  "PASSWORD.CFG-2": ["Abc911226."]
}
```

The server reads the flat `SECTION.KEY` form emitted by `Node.ReadIniFile`. Each
`GUID.CFG-1` and `GUID.CFG-2` value is a string array; the first non-empty value must be a valid
GUID. `PORT.LISTEN` is optional, but when present its first non-empty value must be an integer
from `1` through `65535`. The port is cached as a runtime listen-port source. The number of
configured GUIDs is the authenticated-client limit. Unknown keys such as `IP`, `NAME`, or
`PASSWORD` are ignored by this node and may be used by other nodes.

The first valid configuration is loaded once. A replacement received later is ignored until
the node is restarted. Legacy `clients`, nested `GUID`, and `LOCATION=GUID` array formats are not
supported.

## Start Modes

`StartMode` is a single-select parameter:

| Value | Behavior |
| --- | --- |
| `Immediate` | Default. `Run` starts the listener immediately using `PORT.LISTEN` when already cached, otherwise the configured `Port`. Client configuration may still arrive after `Run`. |
| `WaitForEndpoint` | `Run` keeps the node running without binding. The listener starts after both a valid runtime port (`PORT.LISTEN` or `input_string_endpoint`) and the first client configuration are available. |

`input_string_endpoint` accepts only a plain integer port. It is optional, and an unconnected
pin does not prevent `Run`; the node remains running indefinitely while waiting for a runtime
port when `StartMode=WaitForEndpoint`. The configured `Host` always supplies the bind address,
and the runtime port does not modify persisted parameters.

## Envelope Contract

All frames use the shared semicolon delimiter:

```text
;{"GUID":"...","LOCATION":"CFG-1","TYPE":"BUSINESS","VERSION":"1.0","SEQ":1,"TIMESTAMP":"...","DATA":{...}};
```

Supported control `TYPE` values are `AUTH`, `AUTH_OK`, `AUTH_FAIL`, `PING`, and `PONG`.
Application-level values currently include `RUN`, `RUN_ACK`, `BUSINESS`, and `DISCONNECT`. The node
validates the top-level envelope but does not interpret `DATA` while forwarding received messages.

## Runtime Behavior

- `Immediate` binds during `Run`. `WaitForEndpoint` binds only after both a runtime port and the first client configuration are available.
- A bind failure fails the node with an error. Waiting for an endpoint or configuration keeps `Running = true`.
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
