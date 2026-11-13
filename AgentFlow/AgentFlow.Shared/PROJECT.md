# AgentFlow.Shared

## Purpose

GUI-neutral shared declarations consumed by node plugins and GUI hosts.
The project must not depend on Avalonia, AgentFlow.Core, or any node implementation.

## Layout

Declarations are grouped per concrete project. Each project owns a top-level folder with
the same three sub-folders, so multiple product lines can coexist without name collisions:

```
AgentFlow.Shared/
  RTS/
    Broadcasts/   broadcast subscription names and protocol payload constants
    Errors/       business error code enums
    Results/      result wrapper types
  <NextProject>/
    Broadcasts/
    Errors/
    Results/
```

To support a new project, add a new project-named folder and create the three sub-folders
above; do not place project-specific types at the repository root.

## RTS Modules

- `Errors`: `RTSErrorCode`, with reserved numeric ranges per node family.
- `Results`: `RTSResult` and `RTSResult<T>` result wrappers.
- `Broadcasts`: `RTSBroadcastSubscriptions` topic constants,
  `PassFailTotalYieldBroadcasts` reset command topics, and
  `RTSLauncherStartSignals` request/reply payload values.

## Broadcast Subscriptions

Topic string values are wire-level protocol identifiers and stay stable even when the
containing C# types are renamed.

- `RTSBroadcastSubscriptions.PriFileSettings`: `Pri.File.Settings`
  - Published by `Node.PriFileParser` after parsing a PRI file.
  - Carries the parsed key/value dictionary as `BroadcastTopicMessage.SamplesByKey`,
    with one string sample per key.
- `RTSBroadcastSubscriptions.LauncherSettings`: `FACT.Launcher.Settings`
  - Used to exchange launcher settings between nodes and GUI hosts.
  - `Node.UserSettings` publishes the complete keyed initialization snapshot during
    `Initialize`; `Node.FACTLauncher` publishes its current launcher settings.
  - Both publishing and subscribing code must reference this shared constant instead of
    duplicating the literal string.
- `RTSBroadcastSubscriptions.LauncherStart`: `FACT.Launcher.Start`
  - Used as the request topic for a launcher start command.
- `RTSBroadcastSubscriptions.LauncherStartReply`: `FACT.Launcher.Start.Reply`
  - Used by launcher instances to send correlated start confirmations.
- `RTSLauncherStartSignals.Request`: `Start`
  - Shared request payload value.
- `RTSLauncherStartSignals.Started`: `Started`
  - Shared successful reply payload value.
- `RTSBroadcastSubscriptions.LauncherStop`: `FACT.Launcher.Stop`
  - Shared launcher stop topic retained for workflow-control components.
- `RTSBroadcastSubscriptions.AutoStopExit`: `Workflow.AutoStop.Exit`
  - Published by `Node.AutoStop` when it receives the exact input
    `;{"Request":"Exit"};`.
  - Carries one `StringSample` with value `{"Request":"Exit"}`.
- `RTSBroadcastSubscriptions.LauncherCommand`: `FACT.Launcher.Command`
  - Published by `Node.FACTLauncher` after a launch command is successfully sent to its
    command output pin.
  - Carries one `StringSample` in `BroadcastTopicMessage.SamplesByKey`, keyed by the
    launcher position (`A`, `B`, `C`, `D`, or `F`); its `StringValue` is the complete
    command dispatched to the downstream command pin.
- `RTSBroadcastSubscriptions.PassFailTotalYield`: `Pass.Fail.Total.Yield`
  - Published by `Node.PassFailTotalYield` during initialization and after every
    successful update or reset.
  - Carries a keyed Sample dictionary with per-slot pass/fail/yield values and
    aggregate `P`, `F`, `T`, and `Y` values.
- `PassFailTotalYieldBroadcasts.Reset`: `Pass.Fail.Total.Yield.Reset`
  - GUI-to-node selective reset command consumed by `Node.PassFailTotalYield`.
  - Payload: `BroadcastTopicMessage.SamplesByKey` with keys `A`, `B`, `C`, `D`, or `F`
    and exact `StringSample` value `"Reset"`.
  - Valid requests clear only the selected slots, rewrite `Yield.ini`, and cause a complete
    `Pass.Fail.Total.Yield` snapshot to be published after persistence succeeds.

## Build

```powershell
dotnet build "AgentFlow.Shared.csproj" -c Debug
```
