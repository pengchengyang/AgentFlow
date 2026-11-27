# Node.IniFileConverter

## Purpose

Developer utility node that converts encrypted `.rwi` settings files to readable `.ini` text
files and repackages edited `.ini` files back into `.rwi`. It reuses the `.rw` AES-GCM
format from `WorkflowFileCrypto` (shared key and magic; only the extension differs), so a
`.rwi` file is byte-compatible with the workflow encryption scheme.

## Contract Surface

- Display name: `Ini File Converter`
- Category: `C`
- Input pins:
  - `input_path_rwi`: path to an `.rwi` settings file (decrypts)
  - `input_path_ini`: path to an `.ini` settings file (encrypts)
- Output pins:
  - `output_path_ini`: generated sibling `.ini` absolute path
  - `output_path_rwi`: generated sibling `.rwi` absolute path
- Parameters: none

## Behavior

- Each input sample is one conversion; the output file is a sibling of the input with the
  same base name, atomically replacing any existing destination.
- Decrypted `.ini` text is written as BOM-less UTF-8, preserving CRLF line endings.
- A light content check rejects empty files and files with no section header and no
  content line; full INI syntax validation stays with Node.ReadIniFile at runtime.
- Conversion failures log an error and leave the node running; outputs publish only
  after a successful conversion, and only when connected.
