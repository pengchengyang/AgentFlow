# BroadcastKit.Contracts

## Purpose

Provides GUI-neutral broadcast topic names and the shared message envelope used by
publishers and subscribers working with `BroadcastKit`.

## Contents

- `BroadcastTopics`: centralized topic names.
- `BroadcastTopicMessage`: shared envelope for `Payload`, a single `Sample`, an ordered
  `Samples` collection, a `SamplesByKey` dictionary, or a `SampleListsByKey` dictionary.
- `BroadcastTopicMessage.FromSamples(...)`: creates a batch envelope from a read-only sample list.
- `BroadcastTopicMessage.FromSampleDictionary(...)`: creates an envelope with one Sample per key.
- `BroadcastTopicMessage.FromSampleListDictionary(...)`: creates an envelope with an ordered
  Sample list per key.
- The broadcast bus remains generic and does not interpret payload, sample order, keys, types,
  or business meaning.

## Sample Payloads

### Ordered Batch

`Samples` carries an ordered batch. Publishers and subscribers must agree on the meaning of
positions, `Sample.Key` values, and `Sample.Type` values for a specific topic.

### Dictionary With One Sample Per Key

`SamplesByKey` is an `IReadOnlyDictionary<string, Sample>`. Publishers and subscribers define
the expected keys and sample types. Dictionary keys use ordinal, case-sensitive matching.

### Dictionary With Sample Lists Per Key

`SampleListsByKey` is an `IReadOnlyDictionary<string, IReadOnlyList<Sample>>`. The outer
dictionary provides key-based lookup, while each list preserves its own ordered batch. This
payload is appropriate when a key may produce multiple Samples.

## Snapshot And Validation Rules

- Dictionaries and lists are copied to read-only snapshots when the envelope is created.
- Dictionary keys cannot be empty.
- Dictionary values cannot be null.
- The bus does not merge dictionary representations, coerce Sample types, or decide which
  optional payload takes precedence when multiple representations are populated.
- Publishers and subscribers own the topic contract, including key names, value types,
  ordering, coexistence rules, and malformed-payload handling.

## Build

```powershell
dotnet build "BroadcastKit.Contracts.csproj" -c Debug
```
