"""exFAT / FAT file access inside an open volume (via the FatFs bridge).

The bridge mounts FatFs on top of the volume's decrypted data area, so all
file structures stay inside the encrypted container. FatFs handles FAT12 /
16 / 32 as well as exFAT; the same calls work for both.
"""

from __future__ import annotations

import ctypes
import datetime as _dt
import os
from typing import Iterator

from . import _native
from .volume import Volume

_MODE_READ = 0
_MODE_CREATE = 1
_MODE_APPEND = 2


def _fat_to_datetime(date: int, time: int) -> _dt.datetime | None:
    """FatFs packed date/time (local time, 2s resolution) -> datetime."""
    if not date:
        return None
    try:
        return _dt.datetime(
            ((date >> 9) & 0x7F) + 1980,
            (date >> 5) & 0x0F,
            date & 0x1F,
            (time >> 11) & 0x1F,
            (time >> 5) & 0x3F,
            (time & 0x1F) * 2,
        )
    except ValueError:
        return None


class FileInfo:
    """One directory entry."""

    __slots__ = ("name", "is_directory", "size", "modified")

    def __init__(self, name: str, is_directory: bool, size: int, modified: _dt.datetime | None):
        self.name = name
        self.is_directory = is_directory
        self.size = size
        self.modified = modified

    def __repr__(self) -> str:
        kind = "dir" if self.is_directory else "file"
        return f"FileInfo({kind} {self.name!r}, {self.size} bytes)"


class ExFatFile:
    """An open file inside the volume. Use as a context manager:

        with fs.open("/notes.txt", "w") as f:
            f.write(b"...")
    """

    def __init__(self, _handle, _fs: "ExFatFileSystem"):
        self._handle = _handle
        self._fs = _fs   # keeps the mount alive while the file is open

    @property
    def _h(self):
        if self._handle is None:
            raise RuntimeError("File is closed")
        return self._handle

    @property
    def filesystem(self) -> "ExFatFileSystem":
        """The mount this file belongs to."""
        return self._fs

    def read(self, length: int = -1) -> bytes:
        """Read up to `length` bytes from the current position (-1 = rest)."""
        if length == 0:
            return b""
        if length is not None and length > 0:
            buf = ctypes.create_string_buffer(length)
            done = _native.library().vc_exfat_read(self._h, _native.ptr(buf), length)
            if done < 0:
                _native.raise_for_status(int(done), "vc_exfat_read")
            return buf.raw[:done]
        # read the rest in chunks
        out = bytearray()
        while True:
            chunk_len = 1 << 20
            buf = ctypes.create_string_buffer(chunk_len)
            done = _native.library().vc_exfat_read(self._h, _native.ptr(buf), chunk_len)
            if done < 0:
                _native.raise_for_status(int(done), "vc_exfat_read")
            out += buf.raw[:done]
            if done < chunk_len:
                return bytes(out)

    def write(self, data: bytes | bytearray) -> int:
        if not data:
            return 0
        buf, n = _native.byte_buffer(data)
        done = _native.library().vc_exfat_write(self._h, _native.ptr(buf), n)
        if done < 0:
            _native.raise_for_status(int(done), "vc_exfat_write")
        return int(done)

    def seek(self, position: int) -> None:
        """Move the file pointer to `position` (absolute, in bytes)."""
        _native.raise_for_status(
            _native.library().vc_exfat_seek(self._h, ctypes.c_uint64(position)),
            "vc_exfat_seek")

    def flush_close(self) -> None:
        self.close()

    def close(self) -> None:
        if self._handle is not None:
            handle, self._handle = self._handle, None
            _native.raise_for_status(
                _native.library().vc_exfat_close(handle), "vc_exfat_close")

    def __enter__(self) -> "ExFatFile":
        return self

    def __exit__(self, *_exc) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass


class _DirIterator:
    """Iterator over one directory listing (closes the handle at the end)."""

    def __init__(self, handle):
        self._handle = handle

    def __iter__(self) -> Iterator[FileInfo]:
        return self

    def __next__(self) -> FileInfo:
        if self._handle is None:
            raise StopIteration
        entry = _native.VcExfatEntry()
        rc = _native.library().vc_exfat_readdir(self._handle, ctypes.byref(entry))
        if rc == 0:
            self.close()
            raise StopIteration
        if rc < 0:
            self.close()
            _native.raise_for_status(rc, "vc_exfat_readdir")
        return FileInfo(
            name=entry.name.decode("utf-8", "replace").split("\0", 1)[0],
            is_directory=bool(entry.is_directory),
            size=int(entry.size),
            modified=_fat_to_datetime(entry.modified_date, entry.modified_time),
        )

    def close(self) -> None:
        if self._handle is not None:
            handle, self._handle = self._handle, None
            _native.library().vc_exfat_closedir(handle)

    def __enter__(self) -> "_DirIterator":
        return self

    def __exit__(self, *_exc) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass


class ExFatFileSystem:
    """A mounted FAT/exFAT filesystem inside an open volume.

    Created via :meth:`mount` / :meth:`format`. Not thread-safe - keep a
    mount (and its files) on one thread at a time.
    """

    def __init__(self, _handle, _volume: Volume):
        self._handle = _handle
        self._volume = _volume   # owns the volume while mounted

    # ------------------------------------------------------------ lifecycle

    @classmethod
    def mount(cls, volume: Volume) -> "ExFatFileSystem":
        """Mount the FAT/exFAT filesystem found in the volume's data area."""
        handle = _native.library().vc_exfat_mount(volume._h)
        return cls(_native.raise_for_null(handle, "vc_exfat_mount"), volume)

    @classmethod
    def format(cls, volume: Volume) -> "ExFatFileSystem":
        """Format the volume's data area as exFAT, then mount it.

        Destroys all data inside the volume (not the volume header).
        """
        lib = _native.library()
        _native.raise_for_status(lib.vc_exfat_format(volume._h), "vc_exfat_format")
        return cls.mount(volume)

    @property
    def _h(self):
        if self._handle is None:
            raise RuntimeError("Filesystem is unmounted")
        return self._handle

    def unmount(self) -> None:
        if self._handle is not None:
            handle, self._handle = self._handle, None
            _native.library().vc_exfat_unmount(handle)

    def __enter__(self) -> "ExFatFileSystem":
        return self

    def __exit__(self, *_exc) -> None:
        self.unmount()

    def __del__(self) -> None:
        try:
            self.unmount()
        except Exception:
            pass

    # ------------------------------------------------------------ operations

    def listdir(self, path: str = "/") -> list[FileInfo]:
        """Entries of `path` ('.' and '..' filtered out)."""
        with _DirIterator(self._opendir(path)) as _:
            return list(_)

    def _opendir(self, path: str):
        handle = _native.library().vc_exfat_opendir(self._h, path.encode("utf-8"))
        return _native.raise_for_null(handle, "vc_exfat_opendir")

    def iterdir(self, path: str = "/") -> Iterator[FileInfo]:
        """Lazily yield directory entries (handle closed when exhausted)."""
        return iter(_DirIterator(self._opendir(path)))

    def mkdir(self, path: str) -> None:
        _native.raise_for_status(
            _native.library().vc_exfat_mkdir(self._h, path.encode("utf-8")),
            "vc_exfat_mkdir")

    def remove(self, path: str) -> None:
        """Delete a file or (empty, non-root) directory."""
        _native.raise_for_status(
            _native.library().vc_exfat_delete(self._h, path.encode("utf-8")),
            "vc_exfat_delete")

    def stat(self, path: str) -> FileInfo:
        """FileInfo for `path` (looks up the entry in the parent directory)."""
        path = path.replace("\\", "/").rstrip("/") or "/"
        if path in ("", "/"):
            raise ValueError("stat() needs a path below the root")
        parent, _, name = path.rpartition("/")
        parent = parent or "/"
        for info in self.listdir(parent):
            if info.name == name:
                return info
        raise FileNotFoundError(f"{path} not found")

    def open(self, path: str, mode: str = "r") -> ExFatFile:
        """Open a file inside the volume.

        mode: 'r' (existing, read), 'w' (create/truncate), 'a' (append).
        Suffix 'b' is accepted and ignored (I/O is always binary).
        """
        m = mode.replace("b", "")
        native_mode = {"r": _MODE_READ, "w": _MODE_CREATE, "a": _MODE_APPEND}.get(m)
        if native_mode is None:
            raise ValueError(f"unsupported mode {mode!r} (use 'r', 'w', 'a', with optional 'b')")
        handle = _native.library().vc_exfat_open(self._h, path.encode("utf-8"), native_mode)
        return ExFatFile(_native.raise_for_null(handle, "vc_exfat_open"), self)

    # -------------------------------------------------------------- helpers

    def read_file(self, path: str) -> bytes:
        with self.open(path, "r") as f:
            return f.read()

    def write_file(self, path: str, data: bytes | bytearray) -> None:
        with self.open(path, "w") as f:
            f.write(data)

    def append_file(self, path: str, data: bytes | bytearray) -> None:
        with self.open(path, "a") as f:
            f.write(data)

    def get_space(self) -> tuple[int, int, int]:
        """(total_bytes, free_bytes, cluster_bytes) of the mounted filesystem."""
        space = _native.VcExfatSpace()
        _native.raise_for_status(
            _native.library().vc_exfat_get_space(self._h, ctypes.byref(space)),
            "vc_exfat_get_space")
        return (space.total_bytes, space.free_bytes, space.cluster_bytes)

    def usage(self) -> tuple[int, int]:
        """(used_bytes, total_bytes) convenience."""
        total, free, _cluster = self.get_space()
        return (total - free, total)
