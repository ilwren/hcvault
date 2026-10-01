"""Volume creation, opening, sector I/O and credential rotation."""

from __future__ import annotations

import ctypes
import traceback
from typing import Callable, Iterable, Sequence

from . import _native
from .enums import VcCipher, VcCreationStage, VcFilesystem, VcKdf
from .errors import VcArgumentError, VcError
from .password import SecurePassword

ProgressCallback = Callable[[int, int, VcCreationStage], None]

_MIN_VOLUME_SIZE = 200 * 1024


class Volume:
    """An open VeraCrypt volume: sector-level encrypted access.

    Instances are not thread-safe - one volume per thread at a time.
    Use as a context manager (``with Volume.open(...) as vol:``) to close
    the native handle deterministically.
    """

    def __init__(self, _handle):
        self._handle = _handle   # opaque vc_volume*

    # ------------------------------------------------------------- create

    @classmethod
    def create(
        cls,
        path: str,
        size_bytes: int,
        password: SecurePassword,
        *,
        cipher: VcCipher = VcCipher.AES,
        kdf: VcKdf = VcKdf.ARGON2ID,
        filesystem: VcFilesystem = VcFilesystem.FAT,
        pim: int = 0,
        keyfiles: Iterable[str] = (),
        quick: bool = True,
        hidden: bool = False,
        progress: ProgressCallback | None = None,
    ) -> None:
        """Create a volume file (long-running - Argon2id takes seconds).

        For hidden volumes ``path`` is the existing outer volume and
        ``size_bytes`` the hidden area's size.
        """
        if not isinstance(password, SecurePassword):
            raise TypeError("password must be a SecurePassword (SecurePassword.from_str)")
        if size_bytes < _MIN_VOLUME_SIZE:
            raise VcArgumentError(f"Volume size must be at least {_MIN_VOLUME_SIZE} bytes.")
        if not isinstance(cipher, VcCipher) or not isinstance(kdf, VcKdf):
            raise TypeError("cipher/kdf must be VcCipher/VcKdf members")
        if not isinstance(filesystem, VcFilesystem):
            raise TypeError("filesystem must be a VcFilesystem member")

        _native.initialize()
        lib = _native.library()

        pw_buf, pw_len = password._view()
        kf_arr, kf_count = _native.str_array(keyfiles)

        trampoline = None
        cb = None
        if progress is not None:
            def trampoline(done, total, stage, _user):  # noqa: F811
                try:
                    progress(done, total, VcCreationStage(stage))
                except Exception:
                    traceback.print_exc()
            cb = _native.PROGRESS_CALLBACK(trampoline)

        status = lib.vc_create_volume(
            _native.encode_path(path),
            ctypes.c_uint64(size_bytes),
            _native.ptr(pw_buf), pw_len,
            _native.ptr(kf_arr) if kf_arr is not None else None, kf_count,
            cipher.native_name.encode("utf-8"),
            kdf.native_name.encode("utf-8"),
            filesystem.native_name.encode("utf-8"),
            pim,
            1 if quick else 0,
            1 if hidden else 0,
            ctypes.cast(cb, ctypes.c_void_p) if cb is not None else None,
            None,
        )
        # keep the callback and arrays alive until here
        _ = trampoline, cb, kf_arr, pw_buf
        _native.raise_for_status(status, "vc_create_volume")

    # --------------------------------------------------------------- open

    @classmethod
    def open(
        cls,
        path: str,
        password: SecurePassword,
        *,
        pim: int = 0,
        keyfiles: Sequence[str] = (),
        read_only: bool = False,
        use_backup_header: bool = False,
    ) -> "Volume":
        """Open a volume with password / keyfiles / PIM."""
        if not isinstance(password, SecurePassword):
            raise TypeError("password must be a SecurePassword (SecurePassword.from_str)")

        _native.initialize()
        lib = _native.library()

        pw_buf, pw_len = password._view()
        kf_arr, kf_count = _native.str_array(keyfiles)
        handle = lib.vc_open_volume(
            _native.encode_path(path),
            _native.ptr(pw_buf), pw_len,
            _native.ptr(kf_arr) if kf_arr is not None else None, kf_count,
            pim,
            1 if read_only else 0,
            1 if use_backup_header else 0,
        )
        _ = kf_arr, pw_buf
        return cls(_native.raise_for_null(handle, "vc_open_volume"))

    # ---------------------------------------------------- change password

    @staticmethod
    def change_password(
        path: str,
        old_password: SecurePassword,
        new_password: SecurePassword,
        *,
        old_pim: int = 0,
        old_keyfiles: Sequence[str] = (),
        new_pim: int = 0,
        new_keyfiles: Sequence[str] = (),
        new_kdf: VcKdf | None = None,
        wipe_count: int = 1,
    ) -> None:
        """Re-encrypt the volume header with new credentials.

        Close any open Volume handles on the same file first - the
        operation opens the file exclusively.
        """
        for pw in (old_password, new_password):
            if not isinstance(pw, SecurePassword):
                raise TypeError("passwords must be SecurePassword objects")

        _native.initialize()
        lib = _native.library()

        old_buf, old_len = old_password._view()
        old_kf, old_kf_n = _native.str_array(old_keyfiles)
        new_buf, new_len = new_password._view()
        new_kf, new_kf_n = _native.str_array(new_keyfiles)

        status = lib.vc_change_password(
            _native.encode_path(path),
            _native.ptr(old_buf), old_len,
            _native.ptr(old_kf) if old_kf is not None else None, old_kf_n,
            old_pim,
            _native.ptr(new_buf), new_len,
            _native.ptr(new_kf) if new_kf is not None else None, new_kf_n,
            new_pim,
            new_kdf.native_name.encode("utf-8") if new_kdf is not None else None,
            wipe_count,
        )
        _ = old_buf, old_kf, new_buf, new_kf
        _native.raise_for_status(status, "vc_change_password")

    # ---------------------------------------------------------- properties

    @property
    def _h(self) -> ctypes.c_void_p:
        if self._handle is None:
            raise RuntimeError("Volume is closed")
        return self._handle

    @property
    def data_size(self) -> int:
        """Usable data-area size in bytes (the decrypted view)."""
        return int(_native.library().vc_get_data_size(self._h))

    @property
    def sector_size(self) -> int:
        """Sector size in bytes (512 for file-hosted volumes)."""
        return int(_native.library().vc_get_sector_size(self._h))

    @property
    def cipher_name(self) -> str:
        return _native._text(_native.library().vc_get_cipher_used(self._h))

    @property
    def kdf_name(self) -> str:
        return _native._text(_native.library().vc_get_kdf_used(self._h))

    @property
    def pim(self) -> int:
        return int(_native.library().vc_get_pim(self._h))

    @property
    def is_hidden(self) -> bool:
        return bool(_native.library().vc_is_hidden(self._h))

    # ----------------------------------------------------------------- I/O

    def read_sectors(self, offset: int, length: int) -> bytes:
        """Sector-aligned read: offset and length must be multiples of
        sector_size. See read_data() for the convenient unaligned variant."""
        ss = self.sector_size
        if offset % ss or length % ss:
            raise VcArgumentError(f"read_sectors: offset and length must be multiples of {ss}")
        buf = ctypes.create_string_buffer(length)
        done = _native.library().vc_read_sectors(
            self._h, _native.ptr(buf), ctypes.c_uint64(offset), length)
        if done < 0:
            _native.raise_for_status(int(done), "vc_read_sectors")
        if done != length:
            raise VcError(f"Short read: got {done} of {length} bytes.")
        return buf.raw[:length]

    def write_sectors(self, offset: int, data: bytes) -> None:
        """Sector-aligned write: offset and len(data) must be multiples of
        sector_size. See write_data() for the convenient unaligned variant."""
        ss = self.sector_size
        if offset % ss or len(data) % ss:
            raise VcArgumentError(f"write_sectors: offset and length must be multiples of {ss}")
        if not data:
            return
        buf, n = _native.byte_buffer(data)
        done = _native.library().vc_write_sectors(
            self._h, _native.ptr(buf), ctypes.c_uint64(offset), n)
        if done < 0:
            _native.raise_for_status(int(done), "vc_write_sectors")
        if done != n:
            raise VcError(f"Short write: got {done} of {n} bytes.")

    def read_data(self, offset: int, length: int) -> bytes:
        """Read `length` bytes at `offset` (any alignment) from the
        decrypted data area."""
        if length < 0 or offset < 0:
            raise VcArgumentError("offset and length must be >= 0")
        if length == 0:
            return b""
        ss = self.sector_size
        start = (offset // ss) * ss
        end = ((offset + length + ss - 1) // ss) * ss
        buf = ctypes.create_string_buffer(end - start)
        done = _native.library().vc_read_sectors(
            self._h, _native.ptr(buf), ctypes.c_uint64(start), end - start)
        if done < 0:
            _native.raise_for_status(int(done), "vc_read_sectors")
        if done != end - start:
            raise VcError(f"Short read: got {done} of {end - start} bytes.")
        within = offset - start
        return buf.raw[within:within + length]

    def write_data(self, offset: int, data: bytes | bytearray) -> None:
        """Write `data` at `offset` (any alignment) into the decrypted data
        area. Unaligned edges are combined with the sector's current content
        (read-modify-write)."""
        if offset < 0:
            raise VcArgumentError("offset must be >= 0")
        if not data:
            return
        ss = self.sector_size
        data = bytes(data)
        start = (offset // ss) * ss
        end = ((offset + len(data) + ss - 1) // ss) * ss
        if start == offset and end == offset + len(data):
            self.write_sectors(start, data)
            return
        current = bytearray(self.read_data(start, end - start))
        current[offset - start:offset - start + len(data)] = data
        self.write_sectors(start, bytes(current))

    # ----------------------------------------------------------- lifecycle

    def close(self) -> None:
        if self._handle is not None:
            handle, self._handle = self._handle, None
            _native.library().vc_close_volume(handle)

    def __enter__(self) -> "Volume":
        return self

    def __exit__(self, *_exc) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass

