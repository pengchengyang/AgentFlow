# -----------------------------------------------------------------------
# Copyright (c) 2026 Damon Yang. All rights reserved.
# -----------------------------------------------------------------------
"""Windows Named Pipe / Unix Domain Socket NDJSON transport."""

from __future__ import annotations

import os
import socket
import threading
import time
from typing import Optional


class TransportError(RuntimeError):
    pass


class DuplexConnection:
    def __init__(self, endpoint: str, max_line_bytes: int = 1024 * 1024) -> None:
        self.endpoint = endpoint
        self.max_line_bytes = max_line_bytes
        self._write_lock = threading.Lock()
        self._buffer = bytearray()
        self._closed = False
        self._socket: Optional[socket.socket] = None
        self._fd: Optional[int] = None
        self._os_handle: Optional[int] = None

        if os.name == "nt":
            flags = os.O_RDWR | getattr(os, "O_BINARY", 0)
            self._fd = os.open(endpoint, flags)
            self._os_handle = _get_os_handle(self._fd)
        else:
            sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            sock.connect(endpoint)
            self._socket = sock

    def read_line(self) -> Optional[str]:
        while True:
            newline = self._buffer.find(b"\n")
            if newline >= 0:
                raw = bytes(self._buffer[:newline])
                del self._buffer[:newline + 1]
                if raw.endswith(b"\r"):
                    raw = raw[:-1]
                if len(raw) > self.max_line_bytes:
                    raise TransportError(f"Worker message exceeds {self.max_line_bytes} bytes.")
                return raw.decode("utf-8")

            chunk = self._read_chunk()
            if not chunk:
                if self._buffer:
                    raw = bytes(self._buffer)
                    self._buffer.clear()
                    return raw.decode("utf-8")
                return None
            self._buffer.extend(chunk)

    def _read_chunk(self):
        if self._socket is not None:
            return self._socket.recv(8192)
        if self._fd is not None:
            if os.name == "nt":
                return _read_pipe_available(self._fd, self._os_handle, lambda: self._closed)
            return os.read(self._fd, 8192)
        return b""

    def write_line(self, line: str) -> None:
        data = (line.rstrip("\r\n") + "\n").encode("utf-8")
        if len(data) > self.max_line_bytes:
            raise TransportError(f"Outbound message exceeds {self.max_line_bytes} bytes.")
        with self._write_lock:
            if self._closed:
                raise TransportError("Transport is closed.")
            if self._socket is not None:
                self._socket.sendall(data)
                return
            if self._fd is None:
                raise TransportError("Transport is not connected.")
            view = memoryview(data)
            while view:
                written = os.write(self._fd, view)
                if written <= 0:
                    raise TransportError("Named Pipe write returned zero bytes.")
                view = view[written:]

    def close(self) -> None:
        with self._write_lock:
            if self._closed:
                return
            self._closed = True
            if self._socket is not None:
                try:
                    self._socket.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                self._socket.close()
                self._socket = None
            if self._fd is not None:
                try:
                    os.close(self._fd)
                except OSError:
                    pass
                self._fd = None



def _get_os_handle(fd: int) -> int:
    import msvcrt
    return int(msvcrt.get_osfhandle(fd))


def _read_pipe_available(fd: int, handle: int, is_closed) -> bytes:
    """Poll a synchronous Named Pipe without blocking writes on the same handle."""
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    peek = kernel32.PeekNamedPipe
    peek.argtypes = [wintypes.HANDLE, wintypes.LPVOID, wintypes.DWORD,
                     wintypes.LPVOID, ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID]
    peek.restype = wintypes.BOOL
    available = wintypes.DWORD(0)
    while not is_closed():
        if peek(wintypes.HANDLE(handle), None, 0, None, ctypes.byref(available), None):
            if available.value:
                return os.read(fd, min(8192, int(available.value)))
        else:
            error = ctypes.get_last_error()
            if error in (109, 232):
                return b""
            raise OSError(error, "PeekNamedPipe failed")
        time.sleep(0.01)
    return b""


