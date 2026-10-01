"""hcvault - Python bindings for the HCVault VeraCrypt engine.

Pure-ctypes wrapper around hcvault-core (vcapi, C API v4).

    from hcvault import SecurePassword, Volume, VcCipher, VcKdf

    with SecurePassword.from_str("hunter2") as pw:
        Volume.create("/tmp/volume.hc", 8 * 1024 * 1024, pw,
                      filesystem=VcFilesystem.EXFAT, quick=True)

    with SecurePassword.from_str("hunter2") as pw, \\
            Volume.open("/tmp/volume.hc", pw) as vol, \\
            ExFatFileSystem.mount(vol) as fs:
        fs.write_file("/hello.txt", b"Hello from hcvault!")
        print(fs.read_file("/hello.txt"))

Not audited. AI-assisted. See README and THIRD-PARTY-NOTICES.
"""

from __future__ import annotations

from ._loader import VC_API_VERSION, candidate_paths, library_file_name
from ._native import initialize, shutdown, verify_algorithm_tables
from .enums import VcCipher, VcCreationStage, VcFilesystem, VcKdf
from .errors import (
    VcArgumentError,
    VcError,
    VcLibraryNotFoundError,
    VcUnsupportedError,
    VcVolumeNotFoundError,
    VcWrongPasswordError,
)
from .filesystem import ExFatFile, ExFatFileSystem, FileInfo
from .password import SecurePassword
from .volume import Volume

__version__ = "1.4.0"

__all__ = [
    "ExFatFile",
    "ExFatFileSystem",
    "FileInfo",
    "SecurePassword",
    "VC_API_VERSION",
    "VcArgumentError",
    "VcCipher",
    "VcCreationStage",
    "VcError",
    "VcFilesystem",
    "VcKdf",
    "VcLibraryNotFoundError",
    "VcUnsupportedError",
    "VcVolumeNotFoundError",
    "VcWrongPasswordError",
    "Volume",
    "__version__",
    "candidate_paths",
    "initialize",
    "library_file_name",
    "shutdown",
    "verify_algorithm_tables",
]
