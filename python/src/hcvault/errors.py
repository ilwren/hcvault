"""Exception hierarchy mirroring the native vc_status codes."""

from __future__ import annotations


class VcError(Exception):
    """Base class for all hcvault errors."""


class VcWrongPasswordError(VcError):
    """The password / keyfile / PIM combination was rejected (status 2)."""


class VcArgumentError(VcError):
    """Invalid argument or volume state (status 3)."""


class VcVolumeNotFoundError(VcError):
    """Volume file missing or no valid volume header (status 4)."""


class VcUnsupportedError(VcError):
    """Feature not available in this build / API-version mismatch (status 5)."""


class VcLibraryNotFoundError(VcError):
    """The hcvault-core native library could not be found or loaded."""


_STATUS_MAP = {
    2: VcWrongPasswordError,
    3: VcArgumentError,
    4: VcVolumeNotFoundError,
    5: VcUnsupportedError,
}


def status_to_exception(status: int, message: str) -> VcError:
    """Map a vc_status code to the matching exception class."""
    return _STATUS_MAP.get(status, VcError)(message)
