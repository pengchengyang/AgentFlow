# AgentFlow.Shared

## Purpose

GUI-neutral shared declarations consumed by node plugins and GUI hosts.
The project must not depend on Avalonia, AgentFlow.Core, or any node implementation.

## Layout

Declarations are grouped per concrete project. Each project owns a top-level folder with
the same CFG/CFGS structure, so multiple product lines can coexist without name collisions:

```
AgentFlow.Shared/
  RTS/
    CFG/
      Broadcasts/   broadcast subscription names and protocol payload constants
      Errors/       business error code enums
      Results/      result wrapper types
    CFGS/           reserved for configuration schema declarations
  <NextProject>/
    CFG/
      Broadcasts/
      Errors/
      Results/
    CFGS/
```

To support a new project, add a project-named folder with CFG and CFGS children.
Place shared protocol types under `CFG`; do not place project-specific types at the repository root.

## RTS CFG Modules

- `Errors`: `CFGErrorCode`, with reserved numeric ranges per node family.
- `Results`: `CFGResult` and `CFGResult<T>` result wrappers.
- `Broadcasts`: `CFGBroadcastSubscriptions` topic constants,
  `CFGPassFailTotalYieldBroadcasts` reset command topics, and
  `CFGLauncherStartSignals` request/reply payload values.

## Broadcast Subscriptions

Topic string values are wire-level protocol identifiers and stay stable even when the
containing C# types are renamed.

- `CFGBroadcastSubscriptions.CFGPriFileSettings`: `Pri.File.Settings.00850D6A-1F19-4040-B2D2-BA4E3E8E0F93`
  - Published by `Node.PriFileParser` after parsing a PRI file.
  - Carries the parsed key/value dictionary as `BroadcastTopicMessage.SamplesByKey`,
    with one string sample per key.
- `CFGBroadcastSubscriptions.CFGLauncherSettings`: `FACT.Launcher.Settings.48D865B9-AB18-4DD0-A7A8-0F79D63EEDBB`
  - Used to exchange launcher settings between nodes and GUI hosts.
  - `Node.UserSettings` publishes the complete keyed initialization snapshot during
    `Initialize`; `Node.FACTLauncher` publishes its current launcher settings.
  - Both publishing and subscribing code must reference this shared constant instead of
    duplicating the literal string.
- `CFGBroadcastSubscriptions.CFGLauncherStart`: `FACT.Launcher.Start.660079D0-4F28-4B98-8C7D-81EF060AC9DD`
  - Used as the request topic for a launcher start command.
- `CFGBroadcastSubscriptions.CFGLauncherStartReply`: `FACT.Launcher.Start.Reply.C394EB3B-0382-45B2-A27A-91E82FD37917`
  - Used by launcher instances to send correlated start confirmations.
- `CFGLauncherStartSignals.CFGRequest`: `Start`
  - Shared request payload value.
- `CFGLauncherStartSignals.CFGStarted`: `Started`
  - Shared successful reply payload value.
- `CFGBroadcastSubscriptions.CFGLauncherStop`: `FACT.Launcher.Stop.628779B8-57FF-407F-B252-FD9C448B67CC`
  - Shared launcher stop topic retained for workflow-control components.
- `CFGBroadcastSubscriptions.CFGAutoStopExit`: `Workflow.AutoStop.Exit.5526559F-0CEF-4728-90F2-C87943496AA8`
  - Published by `Node.AutoStop` when it receives the exact input
    `;{"Request":"Exit"};`.
  - Carries one `StringSample` with value `{"Request":"Exit"}`.
- `CFGBroadcastSubscriptions.CFGLauncherCommand`: `FACT.Launcher.Command.C3578D71-A049-43B8-AFE1-2482C1D0FFDD`
  - Published by `Node.FACTLauncher` after a launch command is successfully sent to its
    command output pin.
  - Carries one `StringSample` in `BroadcastTopicMessage.SamplesByKey`, keyed by the
    launcher position (`A`, `B`, `C`, `D`, or `F`); its `StringValue` is the complete
    command dispatched to the downstream command pin.
- `CFGBroadcastSubscriptions.CFGPassFailTotalYield`: `Pass.Fail.Total.Yield.0314C9D4-964F-4520-AC02-8E1A8729754A`
  - Published by `Node.PassFailTotalYield` during initialization and after every
    successful update or reset.
  - Carries a keyed Sample dictionary with per-slot pass/fail/yield values and
    aggregate `P`, `F`, `T`, and `Y` values.
- `CFGPassFailTotalYieldBroadcasts.CFGReset`: `Pass.Fail.Total.Yield.Reset.70CBA995-4A34-4CC8-8789-F0E29F5058C0`
  - GUI-to-node selective reset command consumed by `Node.PassFailTotalYield`.
  - Payload: `BroadcastTopicMessage.SamplesByKey` with keys `A`, `B`, `C`, `D`, or `F`
    and exact `StringSample` value `"Reset"`.
  - Valid requests clear only the selected slots, rewrite `Yield.ini`, and cause a complete
    `Pass.Fail.Total.Yield.0314C9D4-964F-4520-AC02-8E1A8729754A` snapshot to be published after persistence succeeds.

## Build

```powershell
dotnet build "AgentFlow.Shared.csproj" -c Debug
```
