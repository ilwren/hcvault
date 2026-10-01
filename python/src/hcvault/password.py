"""Password buffer: pinned, wipe-on-close bytes.

Best effort only - Python byte strings cannot be erased deterministically,
so a mutable bytearray is used and zeroed on close. Do not keep real
passwords in str objects; str is immutable and will linger in memory.
"""

from __future__ import annotations

import ctypes


class SecurePassword:
    """A password held as a zeroable byte buffer."""

    __slots__ = ("_data", "_closed")

    def __init__(self, data: bytes | bytearray | memoryview):
        self._data = bytearray(bytes(data))
        self._closed = False

    @classmethod
    def from_str(cls, text: str) -> "SecurePassword":
        return cls(text.encode("utf-8"))

    @classmethod
    def from_bytes(cls, data: bytes | bytearray | memoryview) -> "SecurePassword":
        return cls(data)

    @property
    def length(self) -> int:
        return len(self._data)

    def _view(self) -> tuple[ctypes.Array, int]:
        """(ctypes buffer view, usable length) - for the native call."""
        self._raise_if_closed()
        if not self._data:
            # native side takes ptr + len; a 1-byte dummy keeps the pointer valid
            return (ctypes.c_char * 1)(), 0
        return (ctypes.c_char * len(self._data)).from_buffer(self._data), len(self._data)

    def close(self) -> None:
        if not self._closed:
            self._closed = True
            if self._data:
                # memset needs a ctypes buffer; from_buffer shares the
                # bytearray's storage, so the zeros land in the real password
                ctypes.memset((ctypes.c_char * len(self._data)).from_buffer(self._data),
                              0, len(self._data))
                self._data = bytearray()

    def _raise_if_closed(self) -> None:
        if self._closed:
            raise RuntimeError("SecurePassword is closed")

    def __enter__(self) -> "SecurePassword":
        return self

    def __exit__(self, *_exc) -> None:
        self.close()

    def __del__(self) -> None:  # best effort
        try:
            self.close()
        except Exception:
            pass

    def __repr__(self) -> str:
        return "SecurePassword(<%d bytes, %s>)" % (self.length, "closed" if self._closed else "open")
