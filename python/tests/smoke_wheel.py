#!/usr/bin/env python3
"""Smoke test for an INSTALLED hcvault wheel.

Run from OUTSIDE the repository (the CI jobs install the wheel into a clean
venv and run this from a temp directory), so the loader cannot accidentally
find a native library through the repository layout - only the copy bundled
inside the wheel can satisfy it.

Usage:  python smoke_wheel.py
Exit code 0 = the wheel is self-contained and functional.
"""

from __future__ import annotations

import os
import sys
import tempfile

import hcvault
from hcvault import ExFatFileSystem, SecurePassword, Volume, VcFilesystem


def main() -> int:
    print("hcvault", hcvault.__version__)
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, "smoke.hc")
        with SecurePassword.from_str("ci-smoke-test") as pw:
            Volume.create(p, 8 * 1024 * 1024, pw, filesystem=VcFilesystem.EXFAT)
        with SecurePassword.from_str("ci-smoke-test") as pw, \
                Volume.open(p, pw) as vol, ExFatFileSystem.mount(vol) as fs:
            fs.mkdir("/Docs")
            fs.write_file("/Docs/hello.txt", b"bundled wheel works")
            assert fs.read_file("/Docs/hello.txt") == b"bundled wheel works"
            total, free, _cluster = fs.get_space()
            assert total > free > 0, (total, free)
    print("WHEEL SMOKE TEST PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
