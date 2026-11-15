# -----------------------------------------------------------------------
# Copyright (c) 2026 Damon Yang. All rights reserved.
# -----------------------------------------------------------------------
"""Language-neutral AgentFlow Sample types and wire encoding."""

from __future__ import annotations

import base64
import struct
from dataclasses import dataclass
from typing import Any


SUPPORTED_TYPES = {"STRING", "INT32", "INT64", "BOOL", "FLOAT", "DOUBLE", "BIN"}


@dataclass(frozen=True)
class Sample:
    data_type: str
    value: Any = None
    key: str = ""
    encoding: str = ""

    @property
    def type(self) -> str:
        return self.data_type

    def as_string(self) -> str:
        self._require("STRING")
        return "" if self.value is None else str(self.value)

    def as_bool(self) -> bool:
        self._require("BOOL")
        return bool(self.value)

    def as_int32(self) -> int:
        self._require("INT32")
        return int(self.value)

    def as_int64(self) -> int:
        self._require("INT64")
        return int(self.value)

    def as_float(self) -> float:
        self._require("FLOAT")
        return float(self.value)

    def as_double(self) -> float:
        self._require("DOUBLE")
        return float(self.value)

    def as_bin(self) -> bytes:
        self._require("BIN")
        return bytes(self.value or b"")

    def _require(self, expected: str) -> None:
        if self.data_type != expected:
            raise TypeError(f"Sample is {self.data_type}, not {expected}.")

    def to_wire(self, fallback_key: str = "") -> dict[str, Any]:
        key = self.key or fallback_key
        if self.data_type == "STRING":
            encoding, wire_value = "utf8", self.value
        elif self.data_type == "INT32":
            encoding, wire_value = "decimal", int(self.value)
        elif self.data_type == "INT64":
            encoding, wire_value = "decimal-string", None if self.value is None else str(int(self.value))
        elif self.data_type == "BOOL":
            encoding, wire_value = "boolean", self.value
        elif self.data_type == "FLOAT":
            encoding = "ieee754-binary32-bits-hex"
            wire_value = None if self.value is None else struct.pack("<f", float(self.value)).hex()
        elif self.data_type == "DOUBLE":
            encoding = "ieee754-binary64-bits-hex"
            wire_value = None if self.value is None else struct.pack("<d", float(self.value)).hex()
        elif self.data_type == "BIN":
            encoding = "base64"
            wire_value = None if self.value is None else base64.b64encode(bytes(self.value)).decode("ascii")
        else:
            raise ValueError(f"Unsupported Sample type: {self.data_type}")
        return {"key": key, "dataType": self.data_type, "encoding": encoding, "value": wire_value}

    @classmethod
    def from_wire(cls, value: Any, expected_type: str | None = None, pin: str = "") -> "Sample | None":
        if value is None:
            return None
        if not isinstance(value, dict):
            raise ValueError(f"Sample for pin '{pin}' must be an object.")
        data_type = str(value.get("dataType") or "").upper()
        if data_type not in SUPPORTED_TYPES:
            raise ValueError(f"Sample for pin '{pin}' has unsupported dataType '{data_type}'.")
        if expected_type and data_type != expected_type.upper():
            raise ValueError(f"Sample type mismatch for pin '{pin}': expected {expected_type}, received {data_type}.")
        key = str(value.get("key") or pin)
        encoding = str(value.get("encoding") or "")
        raw = value.get("value")
        if raw is None:
            converted = None
        elif data_type == "STRING" and encoding == "utf8":
            converted = str(raw)
        elif data_type == "INT32" and encoding == "decimal":
            converted = int(raw)
        elif data_type == "INT64" and encoding == "decimal-string":
            converted = int(raw)
        elif data_type == "BOOL" and encoding == "boolean":
            converted = bool(raw)
        elif data_type == "FLOAT" and encoding == "ieee754-binary32-bits-hex":
            converted = struct.unpack("<f", bytes.fromhex(str(raw)))[0]
        elif data_type == "DOUBLE" and encoding == "ieee754-binary64-bits-hex":
            converted = struct.unpack("<d", bytes.fromhex(str(raw)))[0]
        elif data_type == "BIN" and encoding == "base64":
            converted = base64.b64decode(str(raw), validate=True)
        else:
            raise ValueError(f"Sample for pin '{pin}' has unsupported encoding '{encoding}' for {data_type}.")
        return cls(data_type, converted, key, encoding)
