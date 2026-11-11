# Node.PriFileParser

## Purpose

`Node.PriFileParser` reads a PRI configuration file and publishes the parsed
key/value information as a JSON string.

## Contract Surface

- Display name: `PRI File Parser`
- Category: `Parsing`
- Input pins:
  - `input_string_path`: path to the PRI file
- Output pins:
  - `output_string_json`: JSON string containing the parsed key/value pairs
- Parameters: none

## Parsing Rules

- Empty lines are ignored.
- Lines whose trimmed content starts with `#` are comments and are ignored.
- The first `=` separates the key from the value.
- Keys and values are trimmed at both ends.
- Keys preserve their full prefix, such as `INFO:REV` or `OPTION:TAC`.
- Values preserve case and internal spaces.
- Lines without `=` are ignored.
- Empty keys are ignored with a warning.
- Duplicate keys use the last parsed value.
- A file with no valid pairs publishes an empty JSON object and logs a warning.

## Broadcast Output

- Topic: `FlashForgeHmiBroadcastSubscriptions.PriFileSettings` (`Pri.File.Settings`)
- Payload: `BroadcastTopicMessage.FromSampleDictionary`
- Each parsed key/value pair is published as one `StringSample`.
- The dictionary is broadcast after parsing succeeds even when `output_string_json`
  has no downstream connection.
- Broadcast failures are logged and do not prevent JSON output.
## File Handling

- Files are read with UTF-8 encoding and BOM detection.
- Missing or unreadable files are logged as errors and produce no output.
- The output pin sends data only when a downstream connection exists.
- Every parsed key/value pair is written to the log at Trace level, including the source file path.
