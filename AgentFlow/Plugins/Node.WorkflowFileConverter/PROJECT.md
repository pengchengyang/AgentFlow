# Node.WorkflowFileConverter

## Purpose

Developer utility node that converts encrypted `.rw` workflow files to readable JSON and
repackages edited JSON back into `.rw`.

## Contract Surface

- Display name: `Workflow File Converter`
- Category: `Workflow Tools`
- Input pins:
  - `input_string_rw`: path to an `.rw` workflow file
  - `input_string_json`: path to a JSON workflow file
- Output pins:
  - `output_string_json`: generated sibling `.json` absolute path
  - `output_string_rw`: generated sibling `.rw` absolute path
- Parameters: none

## Behavior

- `foo.rw` is decrypted to the same directory as `foo.json`.
- `foo.json` is encrypted to the same directory as `foo.rw`.
- Output pins send the converted file path only when a downstream connection exists.
- Files are written through a temporary file and then atomically replaced.
- Invalid input paths, extensions, encrypted payloads, or JSON content are logged as errors and
  do not replace the target file.

Normal AgentFlow loading, saving, AutoSave, and CLI execution use the shared encryption helper
directly and do not require this node.


