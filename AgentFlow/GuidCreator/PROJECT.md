# GuidCreator

`GuidCreator.sln` is an independent solution for creating RFC 4122 version 4 GUIDs in uppercase
`8-4-4-4-12` form. It does not reference AgentFlow runtime or node contracts.

## Projects

- `GuidCreator.Desktop`: A fixed-size Avalonia window with Create and Copy actions.
- `GuidCreator.Cli`: A one-shot command-line generator that prints one GUID and exits.

## Build

```powershell
dotnet build .\GuidCreator.sln -c Release -p:Platform=x64
```

## Run

```powershell
dotnet run --project .\GuidCreator.Desktop\GuidCreator.Desktop.csproj -c Release -p:Platform=x64
dotnet run --project .\GuidCreator.Cli\GuidCreator.Cli.csproj -c Release -p:Platform=x64
```

