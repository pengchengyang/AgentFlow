# Node.PassFailTotalYield

## Purpose

`Node.PassFailTotalYield` maintains per-slot pass, fail, total, and yield counters in
`C:\AC710\UserSettings\Yield.ini`. It is a GUI-neutral plugin: nodes forward Int64 RET results through pins, GUI hosts
read the latest state through a shared broadcast topic, and reset commands are sent back
through a dedicated broadcast topic.

## Contract Surface

- Display name: `Pass Fail Total Yield`
- Category: `Data`
- Input pins (all INT64, optional):
  - `input_int64_retA`
  - `input_int64_retB`
  - `input_int64_retC`
  - `input_int64_retD`
  - `input_int64_retF`
- Output pins: none
- Parameters: none

## Input Semantics

Each input Pin accepts one `Int64Sample`:

- `0`: records one Pass for that Slot.
- Any non-zero value: records one Fail for that Slot.
- Non-INT64 or null input is logged and ignored.
- Input Pins do not accept JSON commands or string-based Reset commands.

Each received sample increments exactly one counter on the matching Slot.

## Reset Command

GUI hosts request a selective reset by publishing to:

`PassFailTotalYieldBroadcasts.Reset` (`Pass.Fail.Total.Yield.Reset`)

The payload must be a `BroadcastTopicMessage.FromSampleDictionary(...)` containing one
`StringSample` per requested slot. Keys are `A`, `B`, `C`, `D`, or `F`, and every value
must be exactly `"Reset"`:

```text
B -> "Reset"
D -> "Reset"
```

Validation is all-or-nothing:

- The dictionary must not be empty.
- Keys are normalized to uppercase and must be one of `A`, `B`, `C`, `D`, or `F`.
- Duplicate normalized keys are rejected.
- Values must be `StringSample` with the exact value `"Reset"`.
- Any invalid key or value rejects the entire request without modifying counters.

After a valid request, the node:

- Clears Pass/Fail only for the requested slots; other slots remain unchanged.
- Recalculates per-slot Yield and aggregate `P`, `F`, `T`, and `Y`.
- Atomically rewrites `Yield.ini`.
- Updates in-memory state only after the file save succeeds.
- Publishes the complete latest snapshot on `Pass.Fail.Total.Yield`.

The Reset subscription is created during `Initialize` and remains active until `Shutdown`,
independent of `Run` and `Stop`.

## Persistence

The fixed file path is:

```text
C:\AC710\UserSettings\Yield.ini
```

Missing directories and the missing file are created automatically. The initial or
reset file format is:

```ini
[P]
0
[F]
0
[T]
0
[Y]
0.00

[A_P]
0
[A_F]
0

[B_P]
0
[B_F]
0

[C_P]
0
[C_F]
0

[D_P]
0
[D_F]
0

[F_P]
0
[F_F]
0
```

Per-slot pass/fail counters are the source of truth. Aggregate `P`, `F`, `T`, and
`Y` values are always derived when the file is saved. Writes use a temporary file
followed by an atomic replacement. If saving fails, the in-memory counters are not
updated.

Yield is calculated as `pass * 100 / (pass + fail)`, rounded to one decimal place
using midpoint-away-from-zero rounding. A zero denominator produces `0.0`.

## Broadcast

- Output topic: `FlashForgeHmiBroadcastSubscriptions.PassFailTotalYield` (`Pass.Fail.Total.Yield`)
- Reset input topic: `PassFailTotalYieldBroadcasts.Reset` (`Pass.Fail.Total.Yield.Reset`)
- Payload: `BroadcastTopicMessage.FromSampleDictionary`
- Keys:
  - `slotA_Pass`, `slotA_Fail`: `Int64Sample`
  - `slotA_Yield`: `DoubleSample`
  - Equivalent keys for slots B, C, D, and F
  - `P`, `F`, `T`: `Int64Sample`
  - `Y`: `DoubleSample`

The node broadcasts the loaded snapshot during `Initialize` and broadcasts a new
snapshot after each successful update or reset.

## Lifecycle

- `Initialize`: create/load `Yield.ini`, broadcast the initial snapshot, and subscribe to the reset topic.
- `Run`: mark the node as running; it does not change the reset subscription.
- `Stop`: mark the node as stopped; the reset subscription remains active between runs.
- `Shutdown`: dispose the reset subscription and release the logger reference.

## Build

```powershell
dotnet build "Node.PassFailTotalYield.csproj" -c Debug -p:Platform=x64
```