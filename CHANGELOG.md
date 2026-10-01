# Changelog

All notable changes to this project are documented here. The format is
based on [Keep a Changelog](https://keepachangelog.com/) and the project
follows semantic versioning.

## 1.4.0 — 2026-09-26

- **New: Python bindings.** `python/` adds a pure-ctypes package (`hcvault`,
  no compiled extension, no runtime dependencies, Python 3.8+) wrapping the
  same 35 `vcapi.h` functions: volume create/open (cipher, KDF, keyfiles,
  PIM, hidden), sector-level I/O, FAT/exFAT file access through the bridge
  (list, read, write, mkdir, delete, stat, space), and password/KDF
  rotation. Tests mirror the C# suite (21 checks, including FAT through the
  FatFs bridge, which the C# suite does not cover); CI runs them on every
  push and builds wheels for GitHub Releases: linux-x64 (manylinux tag
  derived from the .so's symbol versions), Windows x64/x86/ARM64 (cross-arch
  via HCVAULT_WHEEL_RID, each bundled DLL's PE architecture verified against
  the wheel tag) and macOS arm64 (best effort - its native library is
  otherwise untested, and a failed macOS build does not block a release).
  Not on PyPI.

- **New: free-space API.** `vc_exfat_get_space` (vcapi v4 — 35 exported
  functions) reports the total / free / cluster bytes of a mounted exFAT
  filesystem. The managed wrapper exposes it for both filesystems as
  `IVolumeFileSystem.GetSpace()`, returning a `VolumeSpace` record
  (`TotalBytes`, `FreeBytes`, `UsedBytes`, `ClusterBytes`); on FAT volumes
  it is computed live from the in-memory FAT.
- **New: .NET 8 support.** `HCVault.Core` now targets `net8.0;net10.0`.
  The test suite runs one pass per framework (`build.ps1` / `build.sh`
  skip legs whose runtime is not installed), and CI tests both.
- Clearer native-library failures: the loader now distinguishes "not
  found" from "found but refused by the OS" (architecture mismatch,
  missing dependency) and prints the offending paths, the OS error and the
  process architecture. The build scripts point the tests at the right
  library, skip them with a note when none exists (only the tests need
  it - the managed and app builds do not), and `build.ps1 -Arch` defaults
  to the host architecture (so ARM64 PCs build ARM64 automatically).
- Build-script fixes: both scripts are plain ASCII (Windows PowerShell 5.1
  read them with the system code page and garbled non-ASCII output), and
  `build.ps1 -Arch All -Android` / `build.sh --all` now build the
  Windows/Linux libraries in addition to the Android ABIs - previously the
  Android switch silently suppressed them, which would have produced an
  `HCVault.Native` package without Windows binaries in CI. Also fixed the
  `windows-ARM64-release` preset name (CMake preset names are case-sensitive)
  and hardened the CMake RID inference for the Visual Studio generator
  (prefer `CMAKE_GENERATOR_PLATFORM` over `CMAKE_SIZEOF_VOID_P`).
- Architecture self-verification: the build scripts now parse the PE/ELF
  header of every built library and refuse wrong-architecture outputs (a
  mislabeled `runtimes` folder previously surfaced later as a cryptic
  0x8007000B load error), and the managed loader's failure message reports
  what the rejected file actually is ("the file is a Windows x86 (32-bit)
  executable").
- CI: the Windows jobs no longer use `android-actions/setup-android` (it
  tries to install Google's removed legacy `tools` package and fails on
  every run); they export the runner's preinstalled Android SDK instead.
  `build.ps1` now detects the installed Visual Studio via vswhere (VS 2022
  and 2026 both supported) instead of hardcoding the VS 2026 CMake
  generator, so it works on machines - and runners - without VS 2026.
- CI is now two-mode: pushes and pull requests run the automated test
  suite only (Linux job, both target frameworks); releases are published
  by running the workflow manually from the Actions tab with a version
  tag (e.g. `v1.4.0`, validated against the package version). That run
  builds every platform and attaches the binaries to a GitHub Release —
  the WPF demo as a framework-dependent zip (includes `hcvault-core.dll`,
  needs the .NET 10 runtime), one APK per ABI for the MAUI demo, the
  universal Android 5.0 demo APK and both NuGet packages. The WPF job now
  builds the native library and publishes a zip instead of a bare
  `dotnet build`.
- CI speed: superseded push/PR runs are cancelled automatically (release
  runs never are), the package job reuses the Android job's native
  libraries instead of rebuilding all four ABIs (and no longer needs the
  NDK at all), the Windows jobs cache the NuGet packages between runs, and
  every job has a timeout cap.
- API documentation expanded (both languages): full options/property/field
  tables, all 15 ciphers and 6 KDFs with their native names, hidden-volume
  workflow and safety rules, exception-to-status mapping (fixed a
  documented-but-nonexistent `VcVolumeNotFoundException`), the complete
  C function reference with status codes and struct layouts, volume
  geometry (where the 262,144 header bytes go) and performance notes with
  measured numbers.
- vcapi v4 is additive (no signature changes), but `HCVault.Core` 1.4.0
  requires 1.4.0 binaries: older `hcvault-core` builds report API version 3
  and are rejected at startup. Rebuild or update the `HCVault.Native`
  package together with `HCVault.Core`.

## 1.3.0 — 2026-09-26

- **`HCVault.Native` now ships prebuilt native libraries** (standard
  `runtimes/{rid}/native` layout: Windows x64/x86/ARM64, Linux x64, Android
  4 ABIs) instead of compiling sources on the consumer machine. Reference the
  package and build with a `RuntimeIdentifier` — no CMake needed at consume
  time anymore. The repository CMake project remains the way to build for
  other platforms. `HCVault.Core` 1.3.0 aligns its description.
- The WPF demo now follows the OS light/dark theme (Fluent Light/Dark
  switched at startup and on theme changes).
- The Android demos produce per-ABI APKs on RID-specific builds
  (`dotnet publish -r android-arm64 …`); plain builds still yield the
  universal APK with all four ABIs.
- CI: per-ABI MAUI APK artifacts, and a package job that assembles
  `HCVault.Native` with the full runtime set (Windows + Android on the
  Windows runner, linux-x64 from an Ubuntu job).

## 1.2.0 — 2026-09-25

**Project renamed to HCVault.** The previous name was confusingly similar to
the "VeraCrypt" trademark, which the VeraCrypt License expressly forbids for
derivative works. Functionality is unchanged; namespaces, package IDs and
the native library name were renamed accordingly:

- `VeraCryptNet` → **HCVault** (repository / solution)
- `VeraCrypt.Core` → **HCVault.Core** (NuGet, namespace)
- `VeraCrypt.Native` → **HCVault.Native** (NuGet, MSBuild targets)
- native library `veracrypt-core(.dll|.so)` → **hcvault-core** (the C API
  keeps the `vc_*` prefixes and `vcapi.h`; API version 3, unchanged)

Other changes in this release:

- Formal open-source layout: `LICENSE` (Apache-2.0),
  `THIRD-PARTY-NOTICES.md`, `.gitignore`, bilingual documentation
  (`README.md` / `README.zh-CN.md`, `docs/API.md` / `docs/API.zh-CN.md`).
- Added an explicit disclaimer: independent project, AI-assisted code, no
  security audit.
- NuGet package icon; packages bumped to 1.2.0.
- Build scripts: `.\build.ps1 -Arch All` and `./build.sh --all` build every
  platform the host supports (Windows x64/x86/ARM64, Linux x64, Android
  4 ABIs) — combined with `-Pack` / `--pack` this is the release recipe.
- CI: `.github/workflows/build-demos.yml` builds the WPF and Android demos
  on every push.
- Every CMake preset now configures into its own build directory
  (`build-win-<arch>`, `build-android-<abi>`, `build-local`, `build-local-debug`).
  Previously the Windows presets shared one directory, so building a second
  architecture failed with "generator platform ... does not match".
- Fixed the Windows x86 (Win32) build: `port/HwRandom.c` now draws two
  32-bit RDRAND/RDSEED halves on 32-bit x86 instead of calling the
  64-bit-only `_rdrand64_step`/`_rdseed64_step` intrinsics, which MSVC
  declares but does not implement on x86 (LNK2019).
- The WPF demo now uses the .NET 9+ WPF Fluent theme.
- `app/VeraCrypt.AndroidDemo` → `app/HCVault.AndroidDemo`: plain .NET
  Android twin of the MAUI demo that also runs on Android 5.0 (API 21),
  which MAUI 10 (API 24+) cannot target.

## 1.1.0 — 2026-09 (pre-release, former name)

- exFAT support (bundled FatFs R0.15, `vc_exfat_*` C API + `ExFatVolume`).
- Android: all 4 ABIs (arm64-v8a / armeabi-v7a / x86 / x86_64), API 21+,
  NDK r26/r27, 16 KB page alignment, static libc++ linked explicitly
  (works around an NDK-on-Windows bug with spaced install paths).
- Windows-hosted Android builds fixed (case-insensitive include capture in
  `Common/Endian.h`, compiler-predefined byte-order branch).
- Token / smartcard / PKCS#11 keyfile sources removed; upstream tree
  trimmed to the vendored subset (`tools/vendor.sh` reproduces it
  byte-for-byte).
- Build scripts `build.ps1` / `build.sh` with NDK + Ninja auto-detection
  and portable-Ninja download fallback.

## 1.0.0 — 2026-09 (pre-release, former name)

- Initial feature set: VeraCrypt volume create/open/sector-I/O, header
  re-encryption (password/KDF/keyfile rotation), normal + hidden volumes,
  all cipher cascades and header KDFs, keyfiles, PIM, built-in managed FAT
  driver, `vcapi` C API (v2), managed test suite, WPF demo app.
