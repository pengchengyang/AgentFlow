# Node.ReadIniFile

## Purpose

`Node.ReadIniFile` reads one INI file and publishes its contents as compact JSON. It supports
both section-content INI files such as the existing `UserSettings.ini` format and standard
`SECTION` plus `KEY=VALUE` files without requiring a parser-mode parameter.

A runtime path received on `input_string_path` has priority over the configured `IniPath`
parameter.

## Contract Surface

- Display name: `Read INI File`
- Category: `Parsing`
- Input pin: `input_string_path` (`STRING`, optional)
- Output pin: `output_string_sample` (`STRING`)
- Parameter: `IniPath` (`STRING`, default empty)

## Path Resolution

Path selection order:

1. Non-empty `input_string_path` sample value.
2. `IniPath` parameter.
3. If both are empty, the node logs an error and sets `Running = false`.

Relative paths are resolved against the AgentFlow working directory with
`Path.GetFullPath`. Matching single or double quotes around the path are removed. The resolved
absolute path is included in output dispatch logging.

During `Run`, the node reads once using the path available on the input pin or the configured
fallback. If a new path arrives while the node is running, the node immediately rereads and
republishes the JSON. Input received before `Run` remains stored on the pin and is used when
`Run` starts.

## Supported INI Styles

### Section-content style

```ini
[CMD]
C:\AC710\FACT.exe /PC:\sierra\Pri_Files\test.cfg

[IP]
127.0.0.1:8900
```

Output:

```json
{"CMD":["C:\\AC710\\FACT.exe /PC:\\sierra\\Pri_Files\\test.cfg"],"IP":["127.0.0.1:8900"]}
```

### Standard key/value style

```ini
[OPTION]
TAC=xxxxxxxx
VERIFYTAC=NO
```

Output:

```json
{"OPTION.TAC":["xxxxxxxx"],"OPTION.VERIFYTAC":["NO"]}
```

For `key=value` lines outside a section, the output key is `GLOBAL.KEY`. A plain value line
outside any section is ignored with a warning.

## Parsing Rules

- UTF-8 is used with BOM detection.
- Blank lines are ignored.
- Lines whose first non-whitespace character is `#` or `;` are ignored as comments.
- Inline comments are not stripped from values.
- Section names and keys preserve their first observed casing in the output.
- Section and key matching for duplicate collection is case-insensitive.
- A line is treated as `key=value` only when:
  - an `=` is present;
  - the left side is non-empty;
  - the left side contains only letters, digits, `_`, `-`, or `.`.
- Other lines under a section are treated as section-content values.
- Every output value is a JSON array. Duplicate section values or duplicate keys append to the
  same array.
- Invalid section headers are fatal parse errors.
- An empty but structurally valid file produces `{}` with a warning.

## Output and Error Handling

The output uses a flat dictionary whose values are always string arrays:

```json
{"SECTION":["value"],"SECTION.KEY":["value"],"GLOBAL.KEY":["value"]}
```

Disconnected output pins log a warning and do not change `Running`.

The following failures log an error and set `Running = false`:

- Missing path from both sources.
- Missing file.
- File read failure.
- Invalid INI structure.
- Invalid non-string input sample.

## Lifecycle

`Initialize` captures the logger. `Run` sets `Running = true`, resolves the effective path,
and performs the initial read. `Stop` and `Shutdown` set `Running = false`. No file watcher or
automatic refresh is used; rereading occurs only when `Run` starts or a new input path arrives.

## Build

```powershell
dotnet build "plugins/Node.ReadIniFile/Node.ReadIniFile.csproj" -c Debug -p:Platform=x64
```