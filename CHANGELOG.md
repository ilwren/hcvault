# Changelog

All notable changes to this project are documented here. The format is
based on [Keep a Changelog](https://keepachangelog.com/) and the project
follows semantic versioning.

## Unreleased

- **Fixed: the first volume creation made the process look ~300 MB heavier.**
  `vc_init` (reached from the first `Volume.Create`) started the encryption
  thread pool with one worker per logical CPU and an 8 MiB stack each, so a
  32-CPU machine reserved 256 MiB the moment the library initialised - and it
  stayed reserved for the life of the process. The pool now uses 512 KiB
  stacks and is capped at 8 workers (auto-detect probing has at most 6
  candidates, and 8 workers already saturate sector I/O). On the same 32-CPU
  measurement the pool start went from 256 MiB to 4 MiB; on a 2-CPU host from
  16 MiB to 1 MiB. The `POSIX` thread start also sets the stack size
  unconditionally - the old "raise if smaller" check never fired against
  glibc's 8 MiB default, which is why the minimum was an exact size in
  practice. Windows `CreateThread` keeps the value as the reserve size.
  Note for readers of `Process.PrivateMemorySize64`: on Linux and Android that
  property is `/proc/self/status`'s `VmData`, i.e. *reserved* private address
  space, so thread stacks show up there in full even though RSS barely moves.

- **Fixed: every `Create()` leaked one worker thread's stack.**
  `VolumeCreator::CreateVolume` started its worker as a local `Thread` that
  went out of scope without `Join()` or `Detach()`. A joinable POSIX thread
  keeps its stack mapped after it exits, so each create added 8 MiB of VmData
  (and a leaked handle per thread on Windows) for good. The creator now owns
  the thread and joins it in `CheckResult()` (and in its destructor as a
  safety net); `Thread` additionally reaps abandoned threads in its
  destructor, releases the Win32 handle in `Join()`, and tolerates repeated
  join/detach. Ten create/open/write cycles now hold at 2.4 MB of VmData
  instead of growing by 8 MB each.

- **Fixed: the native test suite did not compile against the current ABI.**
  `native/tests/vcapi_tests.c` still called `vc_open_volume` with the
  pre-`kdf_name` signature. All calls pass the kdf now (NULL = auto-detect,
  which also exercises the parallel probing path).

## 1.6.4 — 2026-10-21

- **Fixed: the native loader crashed on Android instead of loading the
  library.** `AppContext.BaseDirectory` is an empty string on .NET for
  Android, and the loader passed it straight to `DirectoryInfo`, so the
  first call into HCVault.Core died with
  `ArgumentException: The value cannot be an empty string (Parameter 'path')`
  before any load was attempted - the library could never be used from an
  Android app. Directory probing is now best effort (a host without an
  application directory simply contributes no candidates), Android asks the
  platform loader by name first (the .so lives inside the APK at
  `lib/{abi}/`), and a failure there reports the missing ABI with the exact
  `RuntimeIdentifiers` / `AndroidNativeLibrary` fix - emulators are x86_64
  while phones are arm64-v8a, which is the usual cause. The report also
  lists the app's real native library directory and what was packaged into
  it, so "the .so never shipped" is distinguishable from "it shipped but
  the OS refused it" at a glance. Platforms where the host publishes no
  runtime identifier (Android reports `"unknown"`) now derive one from the
  process architecture instead of probing a `runtimes/unknown/` path.

- **`ImportFile` / `ExportFile` and their directory forms are now streamed.**
  They used to go through `File.ReadAllBytes` / `IVolumeFileSystem.ReadFile`,
  which materialised the entire file as a single `byte[]`. Importing a 200 MB
  file pushed the process from 37 MB to 239 MB resident, and a second import
  added another 200 MB because large-object-heap segments are neither reused
  promptly nor compacted. Transfers now run through `OpenFile` and a pooled
  1 MiB buffer that is wiped before it returns to the pool, so peak memory is
  independent of file size: the same 200 MB import measures 39 MB resident and
  1 MB of total managed allocation. Signatures are unchanged; `ReadFile` /
  `WriteFile` still offer whole-file semantics for callers that want them.

- **Automatic KDF detection no longer runs Argon2 next to the cheap candidates.**
  When no KDF is given, the header is probed against every supported KDF in
  parallel. Argon2 holds its entire working matrix - 416 MiB at the default
  PIM, up to 1 GiB - for the whole derivation, so it was inflating the peak of
  every auto-detected unlock, and `Volume::Open` repeats the probe once per
  layout. The probe now runs in two rounds: the memory-light KDFs first, the
  memory-hard ones only if none of them matched. The candidate set and the
  result are identical. Opening an HMAC volume with the correct password and
  no explicit KDF drops from 421 MB peak to 5 MB; Argon2 volumes are still
  found (verified: opened in round two at 69 MB with PIM 1). A wrong password
  still has to rule Argon2 out, so it still reaches the Argon2 figure - pass
  an explicit `Kdf` to skip the search entirely.

- **Fixed: non-English file names in a FAT volume were shown as `??????~1`.**
  The long-name entries were being matched against their 8.3 alias with a
  checksum computed from the *decoded* alias rather than from its 11 raw
  on-disk bytes. Windows builds the alias of a non-English name in the OEM
  code page, so it contains bytes above 0x7F; decoding them as ASCII turned
  every one into `?`, the checksum no longer matched, and the long name was
  discarded as an orphan - the listing fell back to the mangled alias. The
  checksum is now taken from the on-disk bytes as the FAT specification
  requires, and the 8.3 fallback decodes as Latin-1 so distinct aliases stay
  distinct. Volumes written by this library were unaffected (it always emits
  an ASCII alias); volumes written by Windows, VeraCrypt or any other
  implementation now list correctly.

- **The Argon2 self-test no longer runs at startup.** Initializing the
  library ran VeraCrypt's full self-test suite, and its Argon2id section
  derives two keys at PIM 1 - two 64 MiB / 3-iteration passes, repeated for
  both the hardware-accelerated and the software cipher pass. Every process
  paid roughly 64 MiB and several hundred milliseconds on the first call
  into HCVault even when it only ever used HMAC-SHA-512, and on Windows the
  freed blocks stayed committed, so the cost was visible in Task Manager for
  the lifetime of the process. The Argon2 vectors are now verified on first
  use instead: `Pkcs5Argon2::DeriveKey` runs them once per process before it
  returns any key, so nothing is trusted untested, while callers that never
  touch Argon2 never pay for it. Measured on linux-x64: initialization drops
  from ~600 ms / 68 MiB peak RSS to ~15 ms / 3 MiB; the managed test suite
  runs in 27.7 s instead of 35.3 s.

- **Fixed: creating an exFAT volume derived the header key twice.** After
  writing the volume, the creator re-opens it to format the data area and
  asked for KDF auto-detection even though the KDF had just been chosen by
  the caller, so the header key was derived a second time by trying every
  algorithm in turn. With Argon2id that is a second 416 MiB / 6-iteration
  pass inside a single `Volume.Create` call (`Pkcs5Kdf` lists Argon2 last,
  so the probe pays for every other KDF before reaching it). The re-open
  now reuses the KDF the volume was created with - noticeably faster and,
  on memory-constrained hosts, one large allocation instead of two.

## 1.6.3 — 2026-10-16

- **New: streaming file access.** `IVolumeFileSystem.OpenFile(path,
  VcFileMode.Read | Create | Append)` returns a `Stream` over a file inside
  the volume - random-position reads, in-place writes, zero-filled holes and
  appends in constant memory, so a multi-gigabyte file no longer has to be
  loaded whole. exFAT streams are backed by new vcapi v6 functions
  (`vc_exfat_tell` / `vc_exfat_size` / `vc_exfat_flush`); the FAT driver
  implements its own cluster-chain stream (growth updates the directory
  entry immediately). The Python wrapper keeps the whole-file API for now.

- **New: in-place exFAT re-format.** `VolumeFilesystem.FormatExFat(volume)`
  formats an open volume's data area as exFAT (contents destroyed) - a FAT
  volume converts without recreating the container, and a `Filesystem.None`
  volume gains a filesystem.

- **New: open with an explicit KDF.** `VolumeOpenOptions.Kdf` (and the
  `kdf=` argument of the Python `Volume.open`, plus a `kdf_name`
  parameter on the C `vc_open_volume` - vcapi v5) tells the core which
  KDF the volume was created with. The default stays auto-detection
  (every supported KDF is tried, which is what a failed attempt pays for,
  including a full Argon2 pass per volume layout); with the KDF given,
  wrong credentials fail after a single derivation. No downside when the
  value is known.

- **Fixed: exFAT operations were not safe across concurrently used
  volumes.** FatFs resolves any path without a drive prefix against a
  process-global "current drive" that the most recent mount sets, and
  the exFAT bridge passed bare paths. With two exFAT volumes mounted at
  the same time - or one being formatted while another was in use - file
  operations could land on the wrong volume, fail with
  `not enabled (fr=12)`, or hit spurious short writes. Every FatFs call
  is now pinned to its own mount's drive slot and serialized behind a
  single lock, so independent volumes no longer interfere. The managed
  test suite now runs its independent scenarios in parallel as a
  regression guard (`HCV_TEST_SERIAL=1` restores the serial order when
  debugging).

- **Fixed: `ContainerSize.GetRequiredContainerSize` could return
  uncreatable FAT sizes.** Its internal FAT floor was 64 data sectors
  (a 294,912-byte container), but the native FAT formatter requires 72
  (36,864 bytes, TC_MIN_FAT_FS_SIZE) - so for small files it answered
  294,912 and `Volume.Create` then rejected that very size. The floors
  are now the exact native minimums (FAT 299,008 / exFAT 2,359,296 /
  None 262,145), and a regression test creates and fills a volume at
  every returned minimum.

- **Build scripts: safer offline behavior.** `build.ps1` / `build.sh` accept `-SkipTests` / `HCV_SKIP_TESTS` to build everything except the
  managed test suite, probe the git origin safely for the repository URL (falling back to the project's home URL outside a checkout), and
  fail with an actionable message instead of a PowerShell error when git is absent or the tree is not a checkout.

## 1.6.2 — 2026-10-15

- **Fixed: the native package layout was broken in every published
  version.** The nuspec mapped the prebuilt libraries to
  `runtimes/runtimes/{rid}/native/` (a doubled prefix - MSBuild joins
  wildcard matches and the target directory differently on the Windows
  build agent than locally), so NuGet never copied them into consuming
  builds: referencing HCVault.Native never actually delivered a loadable
  library, with or without a RuntimeIdentifier, and the first volume
  operation failed with `DllNotFoundException`. The libraries are now
  listed explicitly (`runtimes/{rid}/native`, platform-independent), and
  CI verifies the package contents before anything is released: all 8
  native binaries at their exact paths, no doubled prefixes. 1.6.2 is the
  first release whose packages actually work when installed.

- **Clear size validation on volume creation.** Sizes the native core
  cannot handle used to surface as a bare source position
  (`VeraCrypt::VolumeCreator::CreationThread:74`). The managed layer now
  rejects them up front with the exact limits: FAT containers >= 299,008
  bytes (and <= 2 TiB data area), exFAT >= 2,359,296, `Filesystem.None`
  >= 262,145; hidden volumes keep their 200 KiB floor. All boundaries are
  covered by tests at the exact byte.

- **Hardened: keyfile streams.** Seekable sources are rewound to the
  start on every use, so the same stream instances can back both the
  creation and a later open. A stream that was disposed before the call
  now fails with an actionable message (a bare `MemoryStream(byte[])`
  is always readable - "not readable" always means disposed/closed).

## 1.6.1 — 2026-10-15

- **Fixed: RID-less debug builds were missing the native library.** NuGet
  copies `runtimes/{rid}/native` assets only when a RuntimeIdentifier is
  known (and not even reliably then), so a plain `dotnet build` or IDE F5
  could produce an output containing only HCVault.Core.dll - the first
  volume operation then failed with `DllNotFoundException`. The
  HCVault.Native package now ships a build-translation targets file that
  copies the library matching the build host into the output when no RID
  is set, with a visible build note. Deployment builds should still set a
  RuntimeIdentifier so the correct platform is selected.

- **New: async filesystem operations.** `ReadFileAsync`, `WriteFileAsync`,
  `ListDirectoryAsync`, `ImportFileAsync`, `ExportFileAsync`,
  `ImportDirectoryAsync` and `ExportDirectoryAsync` run the filesystem
  layer on thread-pool threads - same semantics as `Volume.CreateAsync`:
  the cancellation tokens only prevent operations that have not started
  yet.

- The release workflow's version-consistency check now names the two
  project files a version mismatch lives in.

  *Known issue (fixed in 1.6.2): the nupkgs attached to the v1.6.1 GitHub
  Release were built with the broken package layout - do not install
  them; use 1.6.2 or later.*

## 1.6.0 — 2026-10-14

- **New: keyfile streams.** `KeyFileStreams` on `VolumeCreationOptions`,
  `VolumeOpenOptions` and (as `OldKeyFileStreams` / `NewKeyFileStreams`)
  `ChangePasswordOptions` accepts keyfile contents directly as streams -
  wrap a `byte[]` in a `MemoryStream` for the byte form - instead of, or in
  addition to, file paths. The mixing is a byte-exact managed port of
  VeraCrypt's algorithm (1 MiB processed per source, CRC-32 based), verified
  bidirectionally against the native implementation in the test suite.
  Directory expansion stays a path-only feature.

- **New: folder import / export.** `ImportFile`, `ExportFile`,
  `ImportDirectory` and `ExportDirectory` on the filesystem layer move
  single files or whole directory trees between the host and a volume,
  preserving names, Unicode and empty directories. Every filesystem read,
  write and transfer also has an `...Async` counterpart
  (`ReadFileAsync`, `ImportDirectoryAsync`, …) running on a thread-pool
  thread, like `Volume.CreateAsync`.

- **Simpler paths.** Volume paths no longer require a leading separator:
  `"file.txt"`, `"docs/note.txt"` and the classic `"\\file.txt"` forms
  all work, on both FAT and exFAT (the exFAT bridge now also accepts an
  empty path as the volume root).

- **Packaging: RID-less debug builds get the native library.** NuGet copies
  `runtimes/{rid}/native` assets only when a RuntimeIdentifier is known, so
  a plain `dotnet build` / IDE F5 produced no `hcvault-core` binary and the
  first volume operation failed with `DllNotFoundException`. The
  HCVault.Native package now ships a build-translation targets file that
  copies the library matching the build host into the output when no RID is
  set (with a visible note; deployment builds should still set a RID).

- **Packaging: HCVault.Core now depends on the exact matching HCVault.Native
  version.** Installing `HCVault.Core` pulls the prebuilt native libraries
  in automatically (verified: a plain console app with a single
  `PackageReference` builds, copies `libhcvault-core` to its output and
  creates volumes). The metadata moved into a checked-in nuspec; building
  the library itself stays dependency-free and offline. Reference
  `HCVault.Native` explicitly only to force a different version.

## 1.5.0 — 2026-10-11

- **New: container-size helpers.** `ContainerSize.GetFileCapacity` and
  `ContainerSize.GetRequiredContainerSize` compute exactly how large a file
  a given container can hold - or how small a container can be for a given
  file (FAT, exFAT or raw). The math mirrors the bundled formatters
  (VeraCrypt's FatFormatter and FatFs's exFAT format: header regions,
  FAT/exFAT tables, system clusters, automatic cluster-size steps); the
  managed test suite cross-checks the model against real volumes byte for
  byte, including that a container 512 bytes below the computed size no
  longer fits the file.

- **New: async API.** `Volume.CreateAsync`, `Volume.OpenAsync` and
  `Volume.ChangePasswordAsync` run the native operations on thread-pool
  threads for `await`-based callers. The cancellation tokens only prevent
  an operation that has not started yet - the native calls themselves
  cannot be interrupted.

- **Fixed: FAT16 volumes were encoded as FAT12.** The managed FAT driver
  decoded and wrote FAT16 volumes (FAT containers with data areas of
  roughly 4-64 MiB) with the 12-bit FAT12 entry codec: free space on a
  fresh volume was reported one cluster short, and file chains written
  back through the managed driver were packed in a nonstandard on-disk
  layout. FAT16 now uses standard 16-bit entries, verified at the
  raw-sector level to match what VeraCrypt and OS FAT drivers expect.
  Volumes only read (or formatted by VeraCrypt / OS tools) are unaffected
  and now decode correctly; files written through HCVault.Core 1.4.0 or
  earlier into that size range should be re-imported, as their FAT chains
  are stored in the old 12-bit layout.

- **Packaging: real repository URLs and NuGet trusted publishing.** Both
  NuGet packages now carry the URL of the repository they were built from:
  CI injects the GitHub context at pack time, the build scripts derive it
  from the git origin (or `PACK_REPO_URL`), and `dotnet pack` without a
  URL fails fast instead of shipping a placeholder. A new `publish-nuget`
  workflow job pushes both packages to nuget.org via trusted publishing
  (OIDC - no long-lived API key; requires a one-time policy registration
  on nuget.org and the `NUGET_USER` secret). The native library itself is
  unchanged from 1.4.0.

## 1.4.0 — 2026-09-26

- **The desktop demo is now an Avalonia app published with Native AOT.**
  `HCVault.Explorer` was rewritten from WPF (Windows-only, could not be
  AOT-published) to Avalonia 11 with the same UI (volume bar, credentials
  with keyfile/PIM, file list with import/export, Fluent theme following
  the OS light/dark setting - now natively on all three desktop OSes).
  Releases carry it as Native AOT single-file executables: a win-x64 zip
  and a linux-x64 tar.gz (both built and required in CI) plus a best-effort
  macos-arm64 tar.gz. No .NET runtime is needed to run them.

- **Fixed: Android per-ABI publish failure (NU1101).** Publishing the MAUI
  demo for a 32-bit ABI (`dotnet publish -r android-arm` / `-r android-x86`)
  failed while restoring `HCVault.Core`: a plain library restored with an
  Android RID derives its .NET 8 runtime pack from the RID graph's parent
  RID (`linux-bionic-*`), and the 32-bit variants of those packages were
  never published to nuget.org. The library now drops those (nonexistent,
  and unnecessary - Android apps get their managed runtime from the
  Android workload) `PackageDownload` items during restore (workaround from
  dotnet/android#8170), verified with `dotnet restore -r android-arm/-x86`
  against the exact failure. The 64-bit ABIs keep restoring as before.
- **New: Native AOT compatibility.** `HCVault.Core` builds with
  `IsAotCompatible` (the interop layer is entirely source-generated
  `[LibraryImport]` bindings and the code uses no reflection). Verified by
  publishing an AOT test consumer (`PublishAot`) on net8.0 and net10.0 -
  zero trim/AOT warnings, and the full smoke test (create with progress
  callback, open with keyfile + PIM, sector I/O, exFAT mount with unicode
  filenames, space query) passes as a single native executable. CI keeps
  it that way: every push publishes and runs the AOT smoke consumer
  (`tests/HCVault.AotTests`) on linux-x64.

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
