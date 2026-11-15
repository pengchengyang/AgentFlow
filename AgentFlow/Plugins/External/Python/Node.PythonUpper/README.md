# Node.PythonUpper

Complete Python external-node example for AgentFlow.

Pins:

- `input_string_toupper` (STRING) -> `output_string_toupper` (STRING)
- `input_string_tolower` (STRING) -> `output_string_tolower` (STRING)

Parameter:

- `Suffix` (STRING, default `:py`)

Lifecycle and broadcast callbacks are implemented in `Worker/Main.py`:
`Configure`, `Init`, `Run`, `Stop`, `Shutdown`, `OnSample`, and `OnBroadcast`.

Development run:

```text
Worker\Node.PythonUpper.cmd --endpoint <named-pipe-or-uds> --token <token> --package-root <package-root>
```

For production, replace `worker.executable` with the PyInstaller-built EXE under `Worker/`.
