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

import re
import shutil
import subprocess
import sys
import sysconfig
from pathlib import Path

from setuptools import setup
from setuptools.command.bdist_wheel import bdist_wheel as _bdist_wheel

HERE = Path(__file__).resolve().parent
NATIVE_RUNTIMES = HERE.parent / "native" / "runtimes"
BINARIES_DIR = HERE / "src" / "hcvault" / "_binaries"


def _rid_and_lib():
    """(runtime identifier, library file name) for the build machine."""
    import platform

    if sys.platform == "win32":
        machine = platform.machine().lower()
        if machine == "arm64":
            return "win-arm64", "hcvault-core.dll"
        return ("win-x86", "hcvault-core.dll") if sys.maxsize <= 2**32 else ("win-x64", "hcvault-core.dll")
    if sys.platform == "darwin":
        machine = platform.machine().lower()
        return ("osx-arm64", "libhcvault-core.dylib") if machine == "arm64" else ("osx-x64", "libhcvault-core.dylib")
    machine = platform.machine().lower()
    machine_map = {
        "x86_64": "linux-x64", "amd64": "linux-x64", "aarch64": "linux-arm64",
        "armv8l": "linux-arm64", "armv7l": "linux-arm", "armv6l": "linux-arm",
        "i386": "linux-x86", "i686": "linux-x86",
    }
    rid = machine_map.get(machine, "linux-x64" if sys.maxsize > 2**32 else "linux-x86")
    return rid, "libhcvault-core.so"


def _linux_tag(lib: Path) -> str:
    """manylinux_<glibc>_<arch> from the maximum GLIBC symbol version."""
    arch = {"x86_64": "x86_64", "aarch64": "aarch64", "armv7l": "armv7l", "i686": "i686"}[
        platform_machine()
    ]
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


def platform_machine():
    import platform

    return platform.machine().lower()


class bdist_wheel(_bdist_wheel):
    def run(self):
        rid, libname = _rid_and_lib()
        # start from a clean slate: a stale _binaries from an earlier build
        # must never leak into a wheel that bundles nothing (mismatched tag).
        # Includes the incremental build/ tree - build_py skips unchanged
        # files, so a leftover copy there would survive the rmtree above.
        if BINARIES_DIR.is_dir():
            shutil.rmtree(BINARIES_DIR)
        incremental_build = HERE / "build"
        if incremental_build.is_dir():
            shutil.rmtree(incremental_build)
        src = NATIVE_RUNTIMES / rid / "native" / libname
        self.hcvault_bundled_rid = None
        if src.is_file():
            dest = BINARIES_DIR / rid / libname
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dest)
            self.hcvault_bundled_rid = rid
            self.hcvault_bundled_lib = dest
            print(f"hcvault: bundling {src} -> {dest.relative_to(HERE)}")
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
            if rid.startswith("linux-"):
                tag[2] = _linux_tag(self.hcvault_bundled_lib)
            else:
                tag[2] = sysconfig.get_platform().replace("-", "_").replace(".", "_")
            print(f"hcvault: wheel platform tag: {tag[2]}")
        return tuple(tag)


setup(cmdclass={"bdist_wheel": bdist_wheel})
