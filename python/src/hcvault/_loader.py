"""Discovery and loading of the hcvault-core native library.

Search order (first hit wins):
  1. $VCNATIVE_HOME - a directory or the full path to the library file
  2. hcvault/_binaries/{rid}/ bundled inside the wheel
  3. native/runtimes/{rid}/native/ up to 6 levels above the package
     (building/developing inside the repository)
  4. the OS default loader (CDLL("hcvault-core"))
"""

from __future__ import annotations

import ctypes
import os
import platform
import struct
import sys
import threading
from pathlib import Path

from .errors import VcLibraryNotFoundError, VcUnsupportedError

VC_API_VERSION = 4
LIBRARY_NAME = "hcvault-core"

_lock = threading.Lock()
_lib: ctypes.CDLL | None = None


def _rid() -> str:
    bits = struct.calcsize("P") * 8
    machine = platform.machine().lower()
    if sys.platform == "win32":
        if machine == "arm64":
            return "win-arm64"
        return "win-x86" if bits == 32 else "win-x64"
    if sys.platform == "darwin":
        return "osx-arm64" if machine == "arm64" else "osx-x64"
    machine_map = {
        "x86_64": "linux-x64",
        "amd64": "linux-x64",
        "aarch64": "linux-arm64",
        "armv8l": "linux-arm64",
        "armv7l": "linux-arm",
        "armv6l": "linux-arm",
        "i386": "linux-x86",
        "i686": "linux-x86",
    }
    if machine in machine_map:
        return machine_map[machine]
    return "linux-x64" if bits == 64 else "linux-x86"


def library_file_name() -> str:
    if sys.platform == "win32":
        return LIBRARY_NAME + ".dll"
    if sys.platform == "darwin":
        return "lib" + LIBRARY_NAME + ".dylib"
    return "lib" + LIBRARY_NAME + ".so"


def candidate_paths() -> tuple[str, list[Path]]:
    """(rid, ordered candidate paths) for the current interpreter."""
    rid = _rid()
    name = library_file_name()
    candidates: list[Path] = []

    env = os.environ.get("VCNATIVE_HOME")
    if env:
        p = Path(env)
        candidates.append(p if p.is_file() else p / name)

    package_dir = Path(__file__).resolve().parent
    candidates.append(package_dir / "_binaries" / rid / name)

    # repository layout (development): python/src/hcvault -> ... -> <repo>/native
    d = package_dir
    for _ in range(6):
        d = d.parent
        candidates.append(d / "native" / "runtimes" / rid / "native" / name)

    return rid, candidates


def _check_api_version(lib: ctypes.CDLL, origin: str) -> None:
    try:
        fn = lib.vc_api_version
    except AttributeError:
        raise VcLibraryNotFoundError(
            f"{origin} does not export vc_api_version - it is not an hcvault-core library"
        ) from None
    fn.restype = ctypes.c_int32
    version = fn()
    if version != VC_API_VERSION:
        raise VcUnsupportedError(
            f"hcvault-core at {origin} reports API version {version}, "
            f"but this wrapper requires {VC_API_VERSION}. "
            "Update the native library or the hcvault package."
        )


def load() -> ctypes.CDLL:
    """Load (once) and return the native library handle."""
    global _lib
    if _lib is not None:
        return _lib
    with _lock:
        if _lib is not None:
            return _lib

        rid, candidates = candidate_paths()
        rejected: list[str] = []
        for cand in candidates:
            if not cand.is_file():
                continue
            try:
                lib = ctypes.CDLL(str(cand))
            except OSError as exc:
                rejected.append(f"  {cand}: {exc}")
                continue
            _check_api_version(lib, str(cand))
            _lib = lib
            return _lib

        try:
            lib = ctypes.CDLL(LIBRARY_NAME)
            _check_api_version(lib, "system loader")
            _lib = lib
            return _lib
        except (OSError, VcUnsupportedError) as exc:
            details = ("\n".join(rejected)) if rejected else ""
            raise VcLibraryNotFoundError(
                f"Could not load {library_file_name()} for {rid}.\n"
                "Searched:\n"
                "  $VCNATIVE_HOME\n"
                "  the hcvault wheel bundle\n"
                "  <repo>/native/runtimes/{rid}/native (6 levels up)\n"
                "  the system loader\n"
                + (f"Rejected candidates:\n{details}\n" if details else "")
                + f"System loader: {exc}\n"
                "Build it from the repository sources (cmake -S native -B build "
                "&& cmake --build build), install a wheel that bundles it, or "
                "point VCNATIVE_HOME at it."
            ) from exc
