# Node.CFGConfimer

## Purpose

`Node.CFGConfimer` confirms that a complete CFG protocol envelope targets the local GUID,
checks the configured required envelope type, and extracts the top-level `DATA` object as a
standalone JSON string.

Each transport frame carries exactly one envelope. Multiple business records must be sent as
multiple envelopes or represented as a JSON array inside one `DATA` object. Semicolons remain
transport frame delimiters and are not business record separators.

## Pins

| Pin | Direction | Type | Behavior |
| --- | --- | --- | --- |
| `input_string_local_guid` | Input | STRING | Supplies the current local GUID. Invalid values are rejected and do not replace the last valid GUID. |
| `input_string_message` | Input | STRING | Accepts one complete protocol envelope without the outer semicolon frame. |
| `output_string_data` | Output | STRING | Emits only the envelope `DATA` object serialized as compact JSON. |

## Parameters

| Parameter | Default | Behavior |
| --- | --- | --- |
| `RequiredType` | `BUSINESS` | Envelope `TYPE` must match this value (case-insensitive). Empty configuration falls back to `BUSINESS`. |

`LocalGuid` is intentionally a pin instead of a parameter. Messages received before a valid
local GUID arrives are ignored with a warning, so the local GUID provider should run first or
be connected through `DependsOn`.

## Validation

All envelopes are checked for:

- Valid `GUID` targeting the current local GUID.
- Matching `TYPE`.
- A parseable `TIMESTAMP`.
- `DATA` as a JSON object.

`VERSION` and `SEQ` are ignored and do not participate in validation.

The node caches the last successfully dispatched `TIMESTAMP` in UTC. A frame with the same
normalized timestamp is treated as a duplicate and is not emitted. A different timestamp is
treated as a new message, even when it is older than the previous timestamp. The cache is
cleared on initialization, stop, and when the local GUID changes. The timestamp is cached only
after DATA is successfully dispatched to a downstream pin.

Invalid or mismatched envelopes are not forwarded.

## Build

```powershell
dotnet build "plugins/Node.CFGConfimer/Node.CFGConfimer.csproj" -c Debug -p:Platform=x64
```

