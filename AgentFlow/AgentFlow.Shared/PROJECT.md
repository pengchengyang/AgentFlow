# AgentFlow.Shared

## Purpose

GUI-neutral shared declarations consumed by node plugins and GUI hosts.
The project must not depend on Avalonia, AgentFlow.Core, or any node implementation.

## Layout

Declarations are grouped per concrete project. Each project owns a top-level folder with
the same three sub-folders, so multiple product lines can coexist without name collisions:

```
AgentFlow.Shared/
  FlashForgeHMI/
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

## FlashForgeHMI Modules

- `Errors`: `FlashForgeHmiErrorCode`, with reserved numeric ranges per node family.
- `Results`: `FlashForgeHmiResult` and `FlashForgeHmiResult<T>` result wrappers.
- `Broadcasts`: `FlashForgeHmiBroadcastSubscriptions` topic constants and
  `FlashForgeHmiLauncherStartSignals` request/reply payload values.

## Broadcast Subscriptions

Topic string values are wire-level protocol identifiers and stay stable even when the
containing C# types are renamed.

- `FlashForgeHmiBroadcastSubscriptions.PriFileSettings`: `Pri.File.Settings`
  - Published by `Node.PriFileParser` after parsing a PRI file.
  - Carries the parsed key/value dictionary as `BroadcastTopicMessage.SamplesByKey`,
    with one string sample per key.
- `FlashForgeHmiBroadcastSubscriptions.LauncherSettings`: `FACT.Launcher.Settings`
  - Used to exchange launcher settings between nodes and GUI hosts.
  - `Node.UserSettings` publishes the complete keyed initialization snapshot during
    `Initialize`; `Node.FACTLauncher` publishes its current launcher settings.
  - Both publishing and subscribing code must reference this shared constant instead of
    duplicating the literal string.
- `FlashForgeHmiBroadcastSubscriptions.LauncherStart`: `FACT.Launcher.Start`
  - Used as the request topic for a launcher start command.
- `FlashForgeHmiBroadcastSubscriptions.LauncherStartReply`: `FACT.Launcher.Start.Reply`
  - Used by launcher instances to send correlated start confirmations.
- `FlashForgeHmiLauncherStartSignals.Request`: `Start`
  - Shared request payload value.
- `FlashForgeHmiLauncherStartSignals.Started`: `Started`
  - Shared successful reply payload value.
- `FlashForgeHmiBroadcastSubscriptions.LauncherStop`: `FACT.Launcher.Stop`
  - Published by `Node.AutoStop` as the shared launcher stop signal.
- `FlashForgeHmiBroadcastSubscriptions.PassFailTotalYield`: `Pass.Fail.Total.Yield`
  - Published by `Node.PassFailTotalYield` during initialization and after every
    successful update or reset.
  - Carries a keyed Sample dictionary with per-slot pass/fail/yield values and
    aggregate `P`, `F`, `T`, and `Y` values.

## Build

```powershell
dotnet build "AgentFlow.Shared.csproj" -c Debug
```
