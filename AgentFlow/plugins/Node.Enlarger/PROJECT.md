# Node.Enlarger

## Purpose

Fans one input sample out to ten independent output pins. The selected data type is
applied to the input and every output pin.

## Contract Surface

- Display name: `Enlarger`
- Category: `General`
- Input pin: `input` (`STRING` by default, optional)
- Output pins: `output1` through `output10` (`STRING` by default)
- Parameter: `DataType`
  - Options: `string`, `int32`, `int64`, `float`, `double`, `binary`, `bool`

## Behavior

`Run` sets `Running = true`. Every non-null sample received on `input` is forwarded
unchanged to all ten output pins. Only outputs with a downstream connection deliver
the sample further.

## Build

```powershell
dotnet build "AgentFlow/plugins/Node.Enlarger/Node.Enlarger.csproj" -c Debug -p:Platform=x64
```