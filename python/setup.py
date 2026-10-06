"""setuptools shim.

The package itself is pure Python (ctypes). This file exists for one thing:
`bdist_wheel` support so a wheel can bundle the hcvault-core native library
built from the repository sources (native/runtimes/<rid>/native/...) inside
hcvault/_binaries/<rid>/, and so the wheel gets a correct platform tag:

  - native library bundled  -> py3-none-<platform tag>
      Linux: manylinux_<glibc>_<arch>, derived from the symbol versions the
             .so actually requires (probed with objdump; falls back loudly
             if objdump is unavailable)
      Windows / macOS: the sysconfig platform tag (win_amd64, macosx_..._arm64)
  - nothing bundled         -> py3-none-any (the user supplies hcvault-core
                               via VCNATIVE_HOME or the system loader)

The pure-py3-none-any wheel can always be built explicitly with:

    python -m build --no-isolation   # after clearing native/runtimes
"""

import os
import re
import shutil
import subprocess
import sys
import sysconfig
from pathlib import Path

from setuptools import setup
from setuptools.command.bdist_wheel import bdist_wheel as _bdist_wheel
from setuptools.command.build_py import build_py as _build_py

HERE = Path(__file__).resolve().parent
NATIVE_RUNTIMES = HERE.parent / "native" / "runtimes"


# platform tag per Windows RID; Linux tags are derived from the .so's symbol
# versions, macOS from sysconfig
_WINDOWS_TAG = {"win-x64": "win_amd64", "win-x86": "win32", "win-arm64": "win_arm64"}
# ELF architecture name per Linux RID suffix (wheel tags use these)
_LINUX_ARCH = {"x64": "x86_64", "arm64": "aarch64", "arm": "armv7l", "x86": "i686"}


def _forced_rid():
    """$HCVAULT_WHEEL_RID overrides the RID (cross-arch wheels: the win-x86
    and win-arm64 wheels are built on an x64 CI runner - the bundled library
    is never loaded during the build, so the machine need not match). The
    override must belong to the build OS family."""
    rid = os.environ.get("HCVAULT_WHEEL_RID")
    if not rid:
        return None
    prefix = {"win32": "win-", "darwin": "osx-", "linux": "linux-"}.get(sys.platform)
    if prefix is None or not rid.startswith(prefix):
        wanted = prefix or "an OS-matching"
        raise SystemExit(
            f"hcvault: HCVAULT_WHEEL_RID={rid} does not match this build OS "
            f"(expected a {wanted}* RID)"
        )
    return rid


def _rid_and_lib():
    """(runtime identifier, library file name); $HCVAULT_WHEEL_RID wins."""
    import platform

    forced = _forced_rid()
    if sys.platform == "win32":
        machine = platform.machine().lower()
        if machine == "arm64":
            rid = "win-arm64"
        elif sys.maxsize <= 2**32:
            rid = "win-x86"
        else:
            rid = "win-x64"
        rid = forced or rid
        if rid not in _WINDOWS_TAG:
            raise SystemExit(f"hcvault: unknown Windows RID: {rid}")
        return rid, "hcvault-core.dll"
    if sys.platform == "darwin":
        machine = platform.machine().lower()
        rid = forced or ("osx-arm64" if machine == "arm64" else "osx-x64")
        return rid, "libhcvault-core.dylib"
    machine = platform.machine().lower()
    machine_map = {
        "x86_64": "linux-x64", "amd64": "linux-x64", "aarch64": "linux-arm64",
        "armv8l": "linux-arm64", "armv7l": "linux-arm", "armv6l": "linux-arm",
        "i386": "linux-x86", "i686": "linux-x86",
    }
    rid = forced or machine_map.get(machine, "linux-x64" if sys.maxsize > 2**32 else "linux-x86")
    if rid.rsplit("-", 1)[-1] not in _LINUX_ARCH:
        raise SystemExit(f"hcvault: unknown Linux RID: {rid}")
    return rid, "libhcvault-core.so"


def _linux_tag(lib: Path, rid: str) -> str:
    """manylinux_<glibc>_<arch> from the maximum GLIBC symbol version.

    The architecture comes from the RID (not from the build machine), so a
    cross-arch override produces the correct tag.
    """
    arch = _LINUX_ARCH[rid.rsplit("-", 1)[-1]]
    try:
        out = subprocess.run(
            ["objdump", "-T", str(lib)],
            capture_output=True, text=True, check=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError) as exc:
        raise SystemExit(
            f"hcvault: cannot inspect {lib.name} with objdump ({exc}); the "
            "Linux wheel tag (manylinux_...) cannot be verified. Install "
            "binutils or build a pure wheel without the native library."
        ) from None
    versions = [
        tuple(int(p) for p in v.split("."))
        for v in re.findall(r"GLIBC_(\d+\.\d+)", out)
    ]
    if not versions:
        versions = [(2, 5)]
    major, minor = max(versions)
    # manylinux_2_28 is the floor we are willing to claim
    if (major, minor) < (2, 28):
        major, minor = 2, 28
    return f"manylinux_{major}_{minor}_{arch}"


class build_py(_build_py):
    """Place the bundled native library into the wheel layout explicitly
    (build_lib/hcvault/_binaries/<rid>/). See pyproject.toml for why this is
    a plain copy and not package_data/include_package_data: those resolve at
    egg-info time and replay a stale manifest when several wheels are built
    in a row in the same tree (the CI cross-arch builds)."""

    def run(self):
        super().run()
        bundle = getattr(self.distribution, "hcvault_bundle", None)
        if bundle is None:
            return
        rid, src = bundle
        dest_dir = os.path.join(self.build_lib, "hcvault", "_binaries", rid)
        os.makedirs(dest_dir, exist_ok=True)
        dest = os.path.join(dest_dir, os.path.basename(src))
        shutil.copy2(src, dest)
        print(f"hcvault: bundled into wheel: hcvault/_binaries/{rid}/{os.path.basename(src)}")


class bdist_wheel(_bdist_wheel):
    def run(self):
        rid, libname = _rid_and_lib()
        src = NATIVE_RUNTIMES / rid / "native" / libname
        # wipe the incremental build/ tree: leftovers from an earlier wheel
        # build in this directory must never leak into this wheel
        incremental_build = HERE / "build"
        if incremental_build.is_dir():
            shutil.rmtree(incremental_build)
        # handed to build_py through the shared Distribution object
        self.distribution.hcvault_bundle = None
        self.hcvault_bundled_rid = None
        if src.is_file():
            self.distribution.hcvault_bundle = (rid, str(src))
            self.hcvault_bundled_rid = rid
            self.hcvault_bundled_lib = src
            print(f"hcvault: bundling {src} ({rid})")
        else:
            print(
                f"hcvault: {src} not found - building a pure wheel "
                "(no bundled native library)"
            )
        super().run()

    def get_tag(self):
        tag = list(super().get_tag())
        rid = getattr(self, "hcvault_bundled_rid", None)
        if rid:
            if rid.startswith("win-"):
                tag[2] = _WINDOWS_TAG[rid]
            elif rid.startswith("linux-"):
                tag[2] = _linux_tag(self.hcvault_bundled_lib, rid)
            else:  # osx-*
                tag[2] = sysconfig.get_platform().replace("-", "_").replace(".", "_")
            print(f"hcvault: wheel platform tag: {tag[2]}")
        return tuple(tag)


setup(cmdclass={"bdist_wheel": bdist_wheel, "build_py": build_py})
