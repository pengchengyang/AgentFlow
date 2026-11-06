# Node.Shrinker

## Purpose

Fans ten independent input pins into one output pin. The selected data type is applied
to every input and the output.

## Contract Surface

- Display name: `Shrinker`
- Category: `General`
- Input pins: `input1` through `input10` (`STRING` by default, optional)
- Output pin: `output` (`STRING` by default)
- Parameter: `DataType`
  - Options: `string`, `int32`, `int64`, `float`, `double`, `binary`, `bool`

## Behavior

`Run` sets `Running = true`. Every non-null sample received on any of the ten inputs
is forwarded unchanged to the single output pin. The node does not merge or buffer
values.

## Build

```powershell
dotnet build "AgentFlow/plugins/Node.Shrinker/Node.Shrinker.csproj" -c Debug -p:Platform=x64
```