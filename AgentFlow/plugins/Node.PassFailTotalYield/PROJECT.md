# Node.PassFailTotalYield

## Purpose

`Node.PassFailTotalYield` maintains per-slot pass, fail, total, and yield counters in
`C:\AC710\UserSettings\Yield.ini`. It is a GUI-neutral plugin: operators send compact
JSON commands through pins, and GUI or product hosts read the latest state through a
shared broadcast topic.

## Contract Surface

- Display name: `Pass Fail Total Yield`
- Category: `Data`
- Input pins (all STRING, optional):
  - `input_string_slotA`
  - `input_string_slotB`
  - `input_string_slotC`
  - `input_string_slotD`
  - `input_string_slotF`
- Output pins: none
- Parameters: none

## Input Commands

Each input pin accepts one of these commands:

```json
{"Pass":1}
```

```json
{"Fail":1}
```

```text
Reset
```

`"Reset"` is also accepted. The JSON object must contain exactly one of `Pass` or
`Fail`, and the value must be a positive 64-bit integer. Invalid input is logged and
does not change the file or in-memory counters.

A pass or fail command increments only the counter represented by the receiving pin.
A reset command received on any slot pin resets all five slots and rewrites `Yield.ini`.

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

- Topic: `FlashForgeHmiBroadcastSubscriptions.PassFailTotalYield` (`Pass.Fail.Total.Yield`)
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

- `Initialize`: create/load `Yield.ini`, cache counters, and broadcast the initial snapshot.
- `Run`: mark the node as running.
- `Stop`: clear the runtime-running state and logger reference.
- `Shutdown`: no additional resource cleanup is required.

## Build

```powershell
dotnet build "Node.PassFailTotalYield.csproj" -c Debug -p:Platform=x64
```