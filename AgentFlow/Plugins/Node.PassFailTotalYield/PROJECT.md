# Node.PassFailTotalYield

## Purpose

`Node.PassFailTotalYield` maintains per-slot pass, fail, total, and yield counters for the
selected CFG or CFGS profile. CFG keeps the existing five-slot business behavior. CFGS adds
ten numeric slots and uses its own persistence implementation.

The node is GUI-neutral. Input samples arrive through pins, the latest state is published
through TopicBus, and reset commands arrive through the shared reset topic.

## Contract Surface

- Display name: `Pass Fail Total Yield`
- Category: `Data`
- Parameter: `Mode`
  - Type: `string`
  - Select options: `CFG`, `CFGS`
  - Default: `CFG`
- Parameter: `YieldFilePath`
  - Type: `string`
  - Default: empty
  - Empty uses the selected mode's default path.
  - A non-empty value overrides the file location without changing the selected mode's format.
- CFG input pins, all `INT64` and optional:
  - `input_int64_retA`
  - `input_int64_retB`
  - `input_int64_retC`
  - `input_int64_retD`
  - `input_int64_retF`
- CFGS input pins, all `INT64` and optional:
  - `input_int64_ret1` through `input_int64_ret10`
- Output pins: none

Only the input pins belonging to the selected mode are active at runtime. Inputs received
through the other mode's pins are logged as warnings and ignored.

## File Path Override

`YieldFilePath` controls only the file location. `Mode` still controls the slot format.
When `YieldFilePath` is empty, the node uses the mode-specific default path. When it is
non-empty, that path is used instead; the configured stores create the parent directory and
file as needed.

## CFG Mode

`Mode=CFG` preserves the existing behavior.

Persistence file:

```text
C:\RTSResources\UserSettingsCFG\YieldCFG.ini
```

Format:

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

Broadcast slot keys remain compatible with the existing protocol:

```text
slotA_Pass
slotA_Fail
slotA_Yield
...
slotF_Pass
slotF_Fail
slotF_Yield
```

Reset accepts the existing keys:

```text
A / B / C / D / F
```

## CFGS Mode

`Mode=CFGS` enables ten numeric slots.

Persistence file:

```text
C:\RTSResources\UserSettingsCFGS\YieldCFGS.ini
```

Format:

```ini
[P]
0
[F]
0
[T]
0
[Y]
0.00

[1_P]
0
[1_F]
0

[2_P]
0
[2_F]
0

...

[10_P]
0
[10_F]
0
```

Broadcast slot keys:

```text
slot1_Pass
slot1_Fail
slot1_Yield
...
slot10_Pass
slot10_Fail
slot10_Yield
```

Reset accepts numeric keys:

```text
1 / 2 / ... / 10
```

## Input Semantics

Each active input Pin accepts one `Int64Sample`:

- `0`: records one pass for the matching slot.
- Any non-zero value: records one fail for the matching slot.
- Non-INT64 or null input is logged and ignored.
- Input pins do not accept JSON or string reset commands.

Every valid sample increments exactly one counter and then rewrites the active profile file.

## Persistence

The selected store creates the missing parent directory and INI file automatically.

Per-slot pass/fail counters are the source of truth. Aggregate `P`, `F`, `T`, and `Y` values
are always derived when the file is saved. Writes use a temporary file followed by an atomic
replacement.

Yield is calculated as `pass * 100 / (pass + fail)`, rounded to one decimal place using
midpoint-away-from-zero rounding. A zero denominator produces `0.0`.

## Reset Command

GUI hosts request a selective reset by publishing to:

`CFGPassFailTotalYieldBroadcasts.CFGReset` (`Pass.Fail.Total.Yield.Reset.70CBA995-4A34-4CC8-8789-F0E29F5058C0`)

The payload is a `BroadcastTopicMessage.FromSampleDictionary(...)` containing one
`StringSample` per requested slot. Every value must be exactly `"Reset"`.

Validation is all-or-nothing:

- The dictionary must not be empty.
- Keys must match the active mode.
- Duplicate normalized keys are rejected.
- Any invalid key or value rejects the entire request without modifying counters.

After a valid request, the node clears only the selected slots, recalculates totals, rewrites
the active profile file, updates in-memory state only after the file save succeeds, and
publishes a complete snapshot.

The reset subscription is created during `Initialize` and remains active until `Shutdown`,
independent of `Run` and `Stop`.

## Broadcast

- Output topic: `CFGBroadcastSubscriptions.CFGPassFailTotalYield`
  (`Pass.Fail.Total.Yield.0314C9D4-964F-4520-AC02-8E1A8729754A`)
- Reset input topic: `CFGPassFailTotalYieldBroadcasts.CFGReset`
- Payload: `BroadcastTopicMessage.FromSampleDictionary`
- Slot keys are generated according to the active profile.
- Aggregate keys are always `P`, `F`, `T` (`Int64Sample`) and `Y` (`DoubleSample`).

The node broadcasts the loaded snapshot during `Initialize` and broadcasts a new snapshot
after each successful update or reset.

## Lifecycle

- `Initialize`: load the active profile file, broadcast the initial snapshot, and subscribe to the reset topic.
- `Run`: mark the node as running.
- `Stop`: mark the node as stopped; the reset subscription remains active between runs.
- `Shutdown`: dispose the reset subscription and release the logger reference.

## Build

```powershell
dotnet build "Node.PassFailTotalYield.csproj" -c Debug -p:Platform=x64
```