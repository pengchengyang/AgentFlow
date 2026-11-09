# FACT.Shared

## Purpose

Provides shared, GUI-neutral declarations used by FACT nodes and FACT-related clients.
The project must not depend on Avalonia, AgentFlow.Core, or any node implementation.

## Modules

- `Errors`: FACT error codes.
- `Results`: shared result wrappers.
- `Broadcast`: broadcast subscription names shared by FACT publishers and subscribers.

## Broadcast Subscriptions

- `FactBroadcastSubscriptions.LauncherSettings`: `fact.launcher.settings`
  - Used to exchange FACT Launcher settings between nodes and GUI hosts.
  - Both publishing and subscribing code must reference this shared constant instead of
    duplicating the literal string.
- `FactBroadcastSubscriptions.LauncherStart`: `fact.launcher.start`
  - Used as the request topic for a FACT Launcher start command.
- `FactBroadcastSubscriptions.LauncherStartReply`: `fact.launcher.start.reply`
  - Used by launcher instances to send correlated start confirmations.
- `FactLauncherStartSignals.Request`: `Start`
  - Shared request payload value.
- `FactLauncherStartSignals.Started`: `Started`
  - Shared successful reply payload value.
- `FactBroadcastSubscriptions.LauncherStop`: `fact.launcher.stop`
  - Published by `Node.AutoStop` as the shared FACT Launcher stop signal.

## Build

```powershell
dotnet build "FACT.Shared.csproj" -c Debug
```
