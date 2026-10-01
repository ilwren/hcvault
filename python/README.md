# hcvault (Python)

Python bindings for [HCVault](../README.md) — create, open and edit
VeraCrypt-compatible encrypted containers from Python.

Pure ctypes: no compiler, no extension modules, no runtime dependencies
(Python 3.8+). The wrapper talks to the same `vcapi.h` C API the .NET
wrapper uses, so volumes created here open in the official VeraCrypt
application and the other way round.

English | [简体中文](README.zh-CN.md)

Same project disclaimers apply: this is an independent, unofficial project,
produced with AI assistance and **not** security-audited. See the
[root README](../README.md#status-and-disclaimer) and
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## Installation

Install the wheel from the GitHub Releases page - it bundles the native
library. CI-built wheels cover linux-x64 (glibc 2.34+), Windows
x64 / x86 / ARM64 and macOS arm64 (best effort: the macOS native library
is otherwise untested - a wheel only ships after its smoke test passes):

```bash
pip install hcvault-1.4.0-py3-none-manylinux_2_34_x86_64.whl
```

Or build one from a repository checkout (it bundles whatever native
libraries are present under `native/runtimes/`; without them you get a pure
wheel that finds the library via `VCNATIVE_HOME` or the system loader):

```bash
pip wheel python/
```

Runs on Python 3.8+ (`py3` wheel, no upper limit - the suite passes on
3.8, 3.13 and 3.14; CI tests 3.8 plus the runner's current Python). On
older systems pip must be >= 20.3 to recognize the `manylinux_2_XX`
filename (Ubuntu 20.04's stock pip 20.0 needs
`pip install --upgrade pip` first).

The native library is located at import time, first hit wins:

1. `$VCNATIVE_HOME` — a directory or the full path to the library file
2. `hcvault/_binaries/<rid>/` — bundled inside the wheel
3. `native/runtimes/<rid>/native/` — a repository checkout (up to 6 levels up)
4. the system loader (`ctypes.CDLL("hcvault-core")`)

## Quick start

```python
from hcvault import SecurePassword, Volume, VcFilesystem, ExFatFileSystem

# create a container with an exFAT filesystem inside
with SecurePassword.from_str("correct horse battery staple") as pw:
    Volume.create("demo.hc", 64 * 1024 * 1024, pw,
                  filesystem=VcFilesystem.EXFAT)

# open it and work with files inside
with SecurePassword.from_str("correct horse battery staple") as pw, \
        Volume.open("demo.hc", pw) as vol, \
        ExFatFileSystem.mount(vol) as fs:
    fs.mkdir("/Documents")
    fs.write_file("/Documents/notes.txt", b"hello, encryption")
    print(fs.read_file("/Documents/notes.txt"))
    for e in fs.listdir("/"):
        print(e.name, e.size, "bytes", "(dir)" if e.is_directory else "")

    total, free, cluster = fs.get_space()
```

FAT volumes work through the same `ExFatFileSystem` (FatFs handles both).
Also available: sector-level access (`Volume.read_data` / `write_data`),
hidden volumes (`hidden=True`), keyfiles and PIM, password/KDF rotation
(`Volume.change_password`), and a creation progress callback. The full API
is documented in [docs/API.md](../docs/API.md) for the C layer — the Python
names follow it closely.

## Notes worth knowing

- **One thread per handle.** A volume, its mount and its files belong to one
  thread at a time — same rule as the C and .NET layers.
- **Passwords are zeroed best-effort.** `SecurePassword` uses a mutable
  buffer and wipes it on `close()`/`__exit__`. Python cannot guarantee this
  (GC, copies), so keep real secrets out of `str` objects and let the
  context manager do the wiping.
- **Errors carry the native message.** After a failed call the wrapper reads
  the thread-local error text immediately, before any other call can
  invalidate it — catch `VcError` subclasses (`VcWrongPasswordError`,
  `VcVolumeNotFoundError`, ...) as usual.
- **Progress callback exceptions are printed, not raised.** The callback runs
  on a C stack frame; there is nowhere to propagate an exception to.

## Tests

```bash
python tests/run_tests.py     # from the python/ directory
```

21 checks mirroring the C# test suite (create/open/keyfiles/PIM, FAT and
exFAT file I/O through the bridge, space accounting, hidden volumes,
password rotation), plus FAT-through-the-bridge coverage the C# suite does
not have.

## Building

Nothing to build for using the package (building a wheel from source needs
Python 3.9+ / setuptools 77 - installing a built wheel does not). To
produce a wheel that bundles the
native library, build it first (e.g. `cmake -S native -B build && cmake
--build build` puts it in `native/runtimes/<rid>/native/`), then
`pip wheel python/`. On Linux the wheel tag (`manylinux_2_XX_x86_64`) is
derived from the glibc symbol versions the `.so` actually requires.
Cross-arch wheels: set `HCVAULT_WHEEL_RID` (e.g. `win-x86`) to bundle a
library for another architecture of the same OS - the library is never
loaded during the build. On macOS, pass `-D VC_RID=osx-arm64` to CMake
(the RID inference has no Darwin branch).

Not on PyPI (yet); get the wheel from GitHub Releases.
