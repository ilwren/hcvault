#!/usr/bin/env python3
"""Verify that each wheel's bundled native library matches its platform tag.

Used for the cross-compiled Windows wheels (win-x86 / win-arm64), which are
built on an x64 runner: pip refuses to install them there (tag mismatch), so
their DLL cannot be loaded during CI. Instead this script unpacks the wheel
and checks the PE machine field of the bundled DLL against the tag promised
by the wheel filename - the same architecture self-verification the build
scripts do for bare libraries.

Wheels with a non-Windows tag are reported and skipped (the Linux and macOS
jobs smoke-test theirs by actually loading the library).

Usage:  python check_wheel_arch.py wheelhouse/*.whl
"""

from __future__ import annotations

import glob
import struct
import sys
import zipfile

PE_MACHINES = {"win_amd64": 0x8664, "win32": 0x014C, "win_arm64": 0xAA64}


def pe_machine(data: bytes) -> int:
    if data[:2] != b"MZ":
        raise ValueError("not a PE file (no MZ signature)")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe_offset:pe_offset + 4] != b"PE\x00\x00":
        raise ValueError("bad PE signature")
    return struct.unpack_from("<H", data, pe_offset + 4)[0]


def wheel_tag(path: str) -> str:
    """Platform tag from the wheel filename (last dash-separated field)."""
    name = path.replace("\\", "/").rsplit("/", 1)[-1]
    if not name.endswith(".whl"):
        raise ValueError(f"not a wheel: {path}")
    return name.rsplit("-", 1)[1][:-4]


def expand(args: list[str]) -> list[str]:
    """Expand shell-style globs ourselves (pwsh passes them through raw)."""
    out: list[str] = []
    for a in args:
        if not a:
            continue
        matches = sorted(glob.glob(a))
        out.extend(matches if matches else [a])
    return out


def main(argv: list[str]) -> int:
    wheels = expand(argv[1:])
    if not wheels:
        print("no wheels given")
        return 2

    failures = 0
    for path in wheels:
        tag = wheel_tag(path)
        expected = PE_MACHINES.get(tag)
        if expected is None:
            print(f"SKIP {path} (tag {tag}: not a Windows wheel, no PE mapping)")
            continue
        try:
            with zipfile.ZipFile(path) as z:
                libs = [n for n in z.namelist() if n.endswith(".dll")]
                if len(libs) != 1:
                    raise ValueError(f"expected exactly 1 DLL, found {libs}")
                machine = pe_machine(z.read(libs[0]))
        except (OSError, ValueError, zipfile.BadZipFile, struct.error) as exc:
            print(f"FAIL {path}: {exc}")
            failures += 1
            continue
        if machine == expected:
            print(f"OK   {path}: tag {tag}, DLL machine 0x{machine:04X}")
        else:
            print(f"FAIL {path}: tag {tag} but DLL machine 0x{machine:04X} "
                  f"(expected 0x{expected:04X})")
            failures += 1

    if failures:
        print(f"{failures} wheel(s) failed the architecture check")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
