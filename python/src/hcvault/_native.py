"""ctypes bindings for the 35 vcapi.h functions (API version 4)."""

from __future__ import annotations

import atexit
import ctypes
import os

from . import _loader
from .errors import status_to_exception

# ---------------------------------------------------------------- structures


class VcExfatEntry(ctypes.Structure):
    """Mirrors vc_exfat_entry (blittable; explicit padding)."""

    _fields_ = [
        ("name", ctypes.c_char * 256),
        ("is_directory", ctypes.c_uint8),
        ("reserved0", ctypes.c_char * 7),
        ("size", ctypes.c_uint64),
        ("modified_date", ctypes.c_uint16),
        ("modified_time", ctypes.c_uint16),
        ("reserved1", ctypes.c_uint32),
    ]


class VcExfatSpace(ctypes.Structure):
    """Mirrors vc_exfat_space."""

    _fields_ = [
        ("total_bytes", ctypes.c_uint64),
        ("free_bytes", ctypes.c_uint64),
        ("cluster_bytes", ctypes.c_uint64),
    ]


PROGRESS_CALLBACK = ctypes.CFUNCTYPE(
    None, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_int32, ctypes.c_void_p
)

_bound = False


def library() -> ctypes.CDLL:
    """Load the native library (once) with all prototypes bound."""
    global _bound
    lib = _loader.load()
    if not _bound:
        _bind(lib)
        _bound = True
    return lib


def _c_p(func, restype):
    func.restype = restype
    return func


def _bind(lib: ctypes.CDLL) -> None:
    lib.vc_init.restype = ctypes.c_int32
    lib.vc_init.argtypes = []
    lib.vc_shutdown.restype = None
    lib.vc_api_version.restype = ctypes.c_int32
    lib.vc_last_error.restype = ctypes.c_char_p
    lib.vc_last_status.restype = ctypes.c_int32

    lib.vc_get_cipher_count.restype = ctypes.c_int32
    lib.vc_get_cipher_name.restype = ctypes.c_char_p
    lib.vc_get_cipher_name.argtypes = [ctypes.c_int32]
    lib.vc_get_kdf_count.restype = ctypes.c_int32
    lib.vc_get_kdf_name.restype = ctypes.c_char_p
    lib.vc_get_kdf_name.argtypes = [ctypes.c_int32]

    lib.vc_create_volume.restype = ctypes.c_int32
    lib.vc_create_volume.argtypes = [
        ctypes.c_char_p,                    # path
        ctypes.c_uint64,                    # size_bytes
        ctypes.c_void_p, ctypes.c_size_t,   # password, password_len
        ctypes.c_void_p, ctypes.c_size_t,   # keyfile_paths, keyfile_count
        ctypes.c_char_p,                    # cipher
        ctypes.c_char_p,                    # kdf
        ctypes.c_char_p,                    # filesystem
        ctypes.c_int32,                     # pim
        ctypes.c_int32, ctypes.c_int32,     # quick, hidden
        ctypes.c_void_p,                    # progress callback (cast, None ok)
        ctypes.c_void_p,                    # progress_user
    ]

    lib.vc_open_volume.restype = ctypes.c_void_p
    lib.vc_open_volume.argtypes = [
        ctypes.c_char_p,
        ctypes.c_void_p, ctypes.c_size_t,
        ctypes.c_void_p, ctypes.c_size_t,
        ctypes.c_int32, ctypes.c_int32, ctypes.c_int32,
    ]

    lib.vc_read_sectors.restype = ctypes.c_int64
    lib.vc_read_sectors.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint64, ctypes.c_size_t]
    lib.vc_write_sectors.restype = ctypes.c_int64
    lib.vc_write_sectors.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint64, ctypes.c_size_t]

    lib.vc_get_data_size.restype = ctypes.c_uint64
    lib.vc_get_data_size.argtypes = [ctypes.c_void_p]
    lib.vc_get_sector_size.restype = ctypes.c_uint32
    lib.vc_get_sector_size.argtypes = [ctypes.c_void_p]
    lib.vc_get_pim.restype = ctypes.c_int32
    lib.vc_get_pim.argtypes = [ctypes.c_void_p]
    lib.vc_get_cipher_used.restype = ctypes.c_char_p
    lib.vc_get_cipher_used.argtypes = [ctypes.c_void_p]
    lib.vc_get_kdf_used.restype = ctypes.c_char_p
    lib.vc_get_kdf_used.argtypes = [ctypes.c_void_p]
    lib.vc_is_hidden.restype = ctypes.c_int32
    lib.vc_is_hidden.argtypes = [ctypes.c_void_p]
    lib.vc_close_volume.restype = None
    lib.vc_close_volume.argtypes = [ctypes.c_void_p]

    lib.vc_change_password.restype = ctypes.c_int32
    lib.vc_change_password.argtypes = [
        ctypes.c_char_p,
        ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_int32,
        ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_int32,
        ctypes.c_char_p, ctypes.c_int32,
    ]

    lib.vc_exfat_format.restype = ctypes.c_int32
    lib.vc_exfat_format.argtypes = [ctypes.c_void_p]
    lib.vc_exfat_mount.restype = ctypes.c_void_p
    lib.vc_exfat_mount.argtypes = [ctypes.c_void_p]
    lib.vc_exfat_unmount.restype = None
    lib.vc_exfat_unmount.argtypes = [ctypes.c_void_p]

    lib.vc_exfat_mkdir.restype = ctypes.c_int32
    lib.vc_exfat_mkdir.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    lib.vc_exfat_delete.restype = ctypes.c_int32
    lib.vc_exfat_delete.argtypes = [ctypes.c_void_p, ctypes.c_char_p]

    lib.vc_exfat_opendir.restype = ctypes.c_void_p
    lib.vc_exfat_opendir.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    lib.vc_exfat_readdir.restype = ctypes.c_int32
    lib.vc_exfat_readdir.argtypes = [ctypes.c_void_p, ctypes.POINTER(VcExfatEntry)]
    lib.vc_exfat_closedir.restype = None
    lib.vc_exfat_closedir.argtypes = [ctypes.c_void_p]

    lib.vc_exfat_open.restype = ctypes.c_void_p
    lib.vc_exfat_open.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32]
    lib.vc_exfat_read.restype = ctypes.c_int64
    lib.vc_exfat_read.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]
    lib.vc_exfat_write.restype = ctypes.c_int64
    lib.vc_exfat_write.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]
    lib.vc_exfat_seek.restype = ctypes.c_int32
    lib.vc_exfat_seek.argtypes = [ctypes.c_void_p, ctypes.c_uint64]
    lib.vc_exfat_close.restype = ctypes.c_int32
    lib.vc_exfat_close.argtypes = [ctypes.c_void_p]

    lib.vc_exfat_get_space.restype = ctypes.c_int32
    lib.vc_exfat_get_space.argtypes = [ctypes.c_void_p, ctypes.POINTER(VcExfatSpace)]


# ------------------------------------------------------------------ helpers


def _text(raw: bytes | None) -> str:
    return raw.decode("utf-8", "replace") if raw else ""


def last_error_text() -> str:
    """Native error text of the last failed call on this thread."""
    return _text(library().vc_last_error())


def raise_for_status(status: int, context: str) -> None:
    """Translate a vc_status return code into a Python exception."""
    if status == 0:
        return
    # must read vc_last_error() FIRST - any other native call invalidates it
    message = last_error_text() or f"{context} failed (status {status})"
    raise status_to_exception(status, message)


def raise_for_null(handle, context: str):
    """Translate a NULL handle return into a Python exception."""
    if handle is not None and handle != 0:
        return handle
    lib = library()
    status = lib.vc_last_status()
    message = last_error_text() or f"{context} failed"
    raise status_to_exception(status, message)


def encode_path(path: str) -> bytes:
    return os.fspath(path).encode("utf-8") if not isinstance(path, bytes) else path


def str_array(items) -> tuple[ctypes.Array | None, int]:
    """(c_char_p array, count) for a list of paths; (None, 0) when empty."""
    encoded = [os.fspath(i).encode("utf-8") if not isinstance(i, bytes) else i for i in items]
    if not encoded:
        return None, 0
    return (ctypes.c_char_p * len(encoded))(*encoded), len(encoded)


def byte_buffer(data: bytes | bytearray) -> tuple[ctypes.Array, int]:
    """(ctypes buffer, length) - the buffer keeps `data` alive for the call."""
    if len(data) == 0:
        return (ctypes.c_char * 1)(), 0
    return (ctypes.c_char * len(data)).from_buffer_copy(bytes(data)), len(data)


def ptr(buf: ctypes.Array) -> ctypes.c_void_p:
    return ctypes.cast(buf, ctypes.c_void_p)


# ------------------------------------------------------------- init/shutdown

_initialized = False


def initialize() -> None:
    """Run the crypto self-tests and start the RNG/KDF thread pools.

    Idempotent; the first volume operation calls it automatically.
    """
    global _initialized
    if _initialized:
        return
    raise_for_status(library().vc_init(), "vc_init")
    _initialized = True


def shutdown() -> None:
    """Stop the native background threads. Optional (also runs at exit)."""
    global _initialized
    if _initialized and _loader._lib is not None:
        _initialized = False
        try:
            _loader._lib.vc_shutdown()
        except Exception:
            pass


atexit.register(shutdown)


def verify_algorithm_tables() -> None:
    """Cross-check the enum tables against the native algorithm list."""
    from .enums import VcCipher, VcKdf

    initialize()
    lib = library()
    native_ciphers = {
        _text(lib.vc_get_cipher_name(i)) for i in range(lib.vc_get_cipher_count())
    }
    for cipher in VcCipher:
        if cipher.native_name not in native_ciphers:
            raise VcUnsupportedError(f"Cipher '{cipher.native_name}' is not available in the native library.")
    native_kdfs = {_text(lib.vc_get_kdf_name(i)) for i in range(lib.vc_get_kdf_count())}
    for kdf in VcKdf:
        if kdf.native_name not in native_kdfs:
            raise VcUnsupportedError(f"KDF '{kdf.native_name}' is not available in the native library.")

