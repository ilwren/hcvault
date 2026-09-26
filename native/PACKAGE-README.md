# HCVault.Native

NuGet package with the prebuilt native libraries of **hcvault-core** — a
portable build of the VeraCrypt volume core behind a small C API (`vcapi.h`):
create, open and modify VeraCrypt containers, normal and hidden volumes, all
cipher/KDF combinations, keyfiles, PIM, header re-encryption, exFAT via the
bundled FatFs (ChaN R0.15).

The package follows the standard NuGet `runtimes/` layout:

| Runtime | Library |
|---|---|
| `win-x64`, `win-x86`, `win-arm64` | `hcvault-core.dll` |
| `linux-x64` | `libhcvault-core.so` |
| `android-arm64`, `android-arm`, `android-x86`, `android-x64` | `libhcvault-core.so` (API 21+, 16 KB-page aligned) |

## Usage

Reference `HCVault.Core` (the managed wrapper) plus this package, and build
your application with a `RuntimeIdentifier` — the matching library is copied
to your output (or packaged into the APK `lib/<abi>/` on Android) and found
by the wrapper's loader automatically.

Building without a `RuntimeIdentifier` copies no native library; either set
one (`-r linux-x64` …), place the library next to the application manually,
or point the `VCNATIVE_HOME` environment variable at it.

Need a platform not listed above (macOS, other architectures)? Build the core
from the repository sources instead: it is a plain CMake project
(`cmake -S native -B build && cmake --build build`), and `HCVault.Core`
picks up manually placed libraries through the same probing rules.

## License and provenance

Packaging and port layer: Apache-2.0. The vendored VeraCrypt sources are
under the VeraCrypt license (`LICENSE-VeraCrypt.txt` in this package),
dual-licensed Apache-2.0 / TrueCrypt 3.0. FatFs is BSD-style.

This project is independent and not affiliated with IDRIX or the VeraCrypt
project. The code was written with AI assistance and has not been
security-audited.
