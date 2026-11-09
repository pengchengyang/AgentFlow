# BroadcastKit.Contracts

## Purpose

Provides GUI-neutral broadcast topic names and the shared message envelope used by
publishers and subscribers working with `BroadcastKit`.

## Contents

- `BroadcastTopics`: centralized topic names.
- `BroadcastTopicMessage`: shared envelope for `Payload`, a single `Sample`, or an ordered
  `Samples` collection.
- `BroadcastTopicMessage.FromSamples(...)`: creates a batch envelope from a read-only sample list.
- The broadcast bus remains generic and does not interpret payload, sample order, keys, types,
  or business meaning.

## Batch Sample Convention

Batch elements are carried in order. Publishers and subscribers must agree on the meaning of
positions, `Sample.Key` values, and `Sample.Type` values for a specific topic. The list is copied
to a read-only snapshot when the envelope is created, so later mutation of the caller's list
does not change the published message.

## Build

```powershell
dotnet build "BroadcastKit.Contracts.csproj" -c Debug
```
