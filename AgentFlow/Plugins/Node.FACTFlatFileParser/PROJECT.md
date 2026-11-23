# Node.FACTFlatFileParser

## Purpose

Parses a FACT XML log file and publishes the original XML together with a normalized JSON
representation. The legacy comma-separated `FlatFile` row is preserved as indexed JSON fields so
business column names can be added later when the official FACT schema is available. Successful
parses are also broadcast as two independent `StringSample` topics, one for raw XML and one for
JSON.

## Contract Surface

- Display name: `FACT Flat File Parser`
- Category: `Parsing`
- Input pin: `input_string_path` (`STRING`, optional), full path to a `.xml` file
- Output pins:
  - `output_string_raw_xml`: original XML text
  - `output_string_json`: normalized JSON with the full XML tree and parsed `flatFile`
- Parameters: none

## Behavior

- The node accepts an existing `.xml` file path; relative paths are resolved with `Path.GetFullPath`.
- XML is parsed with DTD processing disabled and external XML resolution disabled.
- The root element must be `FactLogInfo`.
- `output_string_raw_xml` preserves the XML file text and is intended for debugging or re-parsing.
- `output_string_json` contains:
  - `schemaVersion`
  - `sourceFile`
  - `root`
  - `xml`: recursively converted XML elements and attributes
  - `flatFile`: `raw`, `fieldCount`, and `fields` indexed from `0`
- Each output pin is checked independently. An unconnected pin is logged as a warning and does not
  block the other output.
- After a successful parse, both output pins and both broadcasts are attempted independently.
  Pin connection state does not affect broadcasting, and one broadcast failure does not prevent
  the other broadcast.

## Broadcast Topics

| Constant | Topic | Payload |
| --- | --- | --- |
| `CFGBroadcastSubscriptions.CFGFactFlatFileXml` | `FACT.FlatFile.Xml.17135905-2023-4B95-A82D-2041AAF75094` | One `StringSample` containing the original XML text, keyed `rawXml`. |
| `CFGBroadcastSubscriptions.CFGFactFlatFileJson` | `FACT.FlatFile.Json.F13B9428-A945-4982-9B1E-A1862EB8A55E` | One `StringSample` containing the normalized JSON text, keyed `json`. |

Broadcasts use `BroadcastTopicMessage.FromSample` and are published only after XML reading,
structure validation, and JSON construction all succeed.

## JSON Notes

The XML converter preserves element and attribute local names. Text-only elements become strings;
elements with attributes or child elements become objects. Repeated child names become arrays.
Values are kept as strings to preserve identifiers such as FSN, IMEI, SKU revisions, and leading
zeros.

## Validation

The sample `RSZ-ED-000476_20261028095858775UTC_603122002902ABXX.xml` was parsed successfully:
root `FactLogInfo`, station `RSZ-ED-000476`, device model `RN934A`, and FlatFile field count `64`.
The raw XML output matched the source file exactly.