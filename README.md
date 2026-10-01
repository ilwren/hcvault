# HCVault

A library for creating, opening and editing VeraCrypt-compatible encrypted
container files — no driver, no mounting, no admin rights.

English | [简体中文](README.zh-CN.md)

If you need VeraCrypt volumes inside your own application — an encrypted
document store, a mobile vault app, a backup tool — this library does the
volume work directly: create containers, open them with password / keyfiles /
PIM, and read or write the decrypted contents at the sector or file level.
Containers made here open in the official VeraCrypt application, and the
other way round.

The volume core is the actual VeraCrypt source (1.26.29), vendored and
patched only where portability required it. The cryptography is untouched.
It is exposed through a small C API (`vcapi.h`, 35 functions), with a
.NET 8 / .NET 10 wrapper and a pure-ctypes Python package on top.

```
 your app (C#, C/C++, or anything with an FFI)
        │                │
        │ P/Invoke       │ C ABI (vcapi.h)
        ▼                ▼
 HCVault.Core      hcvault-core
 (.NET wrapper)    (VeraCrypt core + FatFs, CMake)
```

## Status and disclaimer

This is an independent project. It is not affiliated with, endorsed by, or
in any way connected to IDRIX or the VeraCrypt project. "VeraCrypt" is their
trademark; the VeraCrypt License forbids derivative names, which is why this
project is called HCVault (after the `.hc` container extension). See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The code and documentation of this project were produced with AI assistance
and have **not** been security-audited. The cryptographic core is inherited
from VeraCrypt, but the integration around it is new and unreviewed. Read
what you ship, keep backups of your data, and do not trust either this
library or any encryption software with irreplaceable files as the only
copy.

## Security

Is a volume created here as secure as one created by the official
application? For the container itself, yes: it is the same volume format,
produced by the same source code — the header layout, cipher cascades, KDFs,
XTS mode, self-tests and RNG design are all literally VeraCrypt's. This is
not a reimplementation.

What differs is everything around the container:

- This project has no independent audit. VeraCrypt has been audited; the
  glue code here (the C API, the .NET wrapper, the build packaging) has not
  been reviewed by anyone.
- Quick mode (the default) does not wipe the data area before formatting —
  same as official VeraCrypt's quick mode. Pass full format if you need the
  wipe.
- On the .NET side, `SecurePassword` is pinned and wiped on disposal, but
  ordinary file buffers (`byte[]`) handed to the API live in managed memory
  and are not zeroed afterwards.
- Smartcard / security-token keyfiles are not supported (the PKCS#11 chain
  was removed; file keyfiles work as usual). TrueCrypt-mode volumes are not
  supported either.
- As with all software encryption, a compromised host defeats everything.

## What it can and cannot do

It can:

- create normal and hidden file-hosted volumes (200 KiB minimum), with any
  cipher / KDF / keyfile / PIM combination
- open volumes from current and legacy (V1 layout) VeraCrypt releases,
  including reading through the embedded backup header when the primary one
  is damaged
- read and write the decrypted data area at sector level, or through the
  built-in FAT driver / bundled exFAT (FatFs R0.15) — list, read, write,
  mkdir, delete, and query total / free space
- rotate a volume's password, KDF and keyfiles by re-encrypting only the
  header
- run on Android down to API 21 inside your own app

It cannot:

- mount volumes, show drive letters, or encrypt system disks / full drives
  (that needs kernel drivers, deliberately out of scope)
- act as a replacement for the VeraCrypt application — it is a library for
  building your own tools

## Compatibility

| Layer | Supported |
|---|---|
| Volume format | creates V2 (current); opens V2 and legacy V1; normal + hidden volumes. Official VeraCrypt opens these volumes and vice versa (Argon2id volumes require VeraCrypt 2016 or later) |
| Native core | Windows x64 / x86 / ARM64 (MSVC), Linux x64 (GCC/Clang), Android arm64-v8a / armeabi-v7a / x86 / x86_64 (API 21+, NDK r26/r27, 16 KB page aligned). macOS builds on its POSIX base but is not tested |
| Managed wrapper | .NET 8 and .NET 10 (`net8.0;net10.0`) |
| Python package | 1.4.0 — Python 3.8+ (pure ctypes, no dependencies; the wheel bundles the native library, linux-x64 from CI) |
| Demos | WPF (Windows), MAUI (Android 7.0 / API 24+), plain .NET Android (Android 5.0 / API 21+) |
| Based on | VeraCrypt 1.26.29, FatFs R0.15 |

## Quick start

C#:

```bash
dotnet add package HCVault.Core     # the wrapper
dotnet add package HCVault.Native   # prebuilt native libraries (all platforms)
                                    # build your app with a RuntimeIdentifier
```

```csharp
using HCVault.Core;

// create a container with an exFAT filesystem inside
using (var pw = SecurePassword.FromText("correct horse battery staple"))
{
    Volume.Create(new VolumeCreationOptions
    {
        Path = "demo.hc", SizeBytes = 64 * 1024 * 1024,
        Password = pw, Cipher = VcCipher.Aes, Kdf = VcKdf.Argon2id,
        Filesystem = VcFilesystem.ExFat,
    });
}

// open it and work with files inside
using (var pw = SecurePassword.FromText("correct horse battery staple"))
using (var vol = Volume.Open(new VolumeOpenOptions { Path = "demo.hc", Password = pw }))
using (var fs = VolumeFilesystem.Mount(vol))      // FAT or exFAT, auto-detected
{
    fs.WriteFile("\\notes.txt", "hello, encryption"u8.ToArray());
    foreach (var e in fs.ListDirectory("\\"))
        Console.WriteLine($"{e.Name}  {e.SizeBytes} B");
}
```

Python:

```python
# pip install the hcvault wheel from GitHub Releases
from hcvault import SecurePassword, Volume, VcFilesystem, ExFatFileSystem

with SecurePassword.from_str("correct horse battery staple") as pw:
    Volume.create("demo.hc", 64 * 1024 * 1024, pw,
                  filesystem=VcFilesystem.EXFAT)

with SecurePassword.from_str("correct horse battery staple") as pw, \
        Volume.open("demo.hc", pw) as vol, ExFatFileSystem.mount(vol) as fs:
    fs.write_file("/notes.txt", b"hello, encryption")
    for e in fs.listdir("/"):
        print(e.name, e.size, "bytes")
```

C:

```c
#include "vcapi.h"

vc_init();
vc_create_volume("demo.hc", 64ULL << 20, "pw", 2, NULL, 0,
                 "AES", "Argon2", "FAT", 0, 1, 0, NULL, NULL);
vc_volume *v = vc_open_volume("demo.hc", "pw", 2, NULL, 0, 0, 0, 0);
uint8_t sector[512];
vc_read_sectors(v, sector, 0, sizeof sector);   /* decrypted first sector */
vc_close_volume(v);
```

Full API documentation for the C and .NET layers:
[docs/API.md](docs/API.md) ([中文](docs/API.zh-CN.md)). Python usage:
[python/README.md](python/README.md).

## Building

Prerequisites: .NET 10 SDK (the class library also targets .NET 8 — the
test suite runs one pass per installed runtime), CMake ≥ 3.15, a C++
toolchain. Android also needs NDK r26/r27; the MAUI demo needs the
`maui-android` workload.

```powershell
.\build.ps1                              # native (host arch) + managed + tests + WPF demo
.\build.ps1 -Arch All                   # native x64 + x86 + ARM64
.\build.ps1 -Android                    # all 4 Android ABIs
.\build.ps1 -Arch All -Android -Pack    # everything + both NuGet packages
```

```bash
./build.sh                               # native linux-x64 + managed + tests
./build.sh --android                     # all 4 Android ABIs
./build.sh --all --pack                  # everything + both NuGet packages
```

The scripts find CMake, the NDK and Ninja on their own (and can fetch a
portable Ninja — `-NoDownload` / `VCN_NO_DOWNLOAD=1` disables that).

CI runs the automated test suite (native + managed, both target
frameworks, plus the Python wrapper) on every push: [.github/workflows/build-demos.yml](.github/workflows/build-demos.yml).
Releases are published manually: Actions → build-demos → Run workflow →
enter the version tag (e.g. `v1.4.0`, must match the project version).
That run builds every platform and attaches the binaries to a GitHub
Release: the WPF demo as a framework-dependent zip (needs the .NET 10
runtime), one APK per ABI for the MAUI demo, the universal Android 5.0
demo APK, both NuGet packages, and the Python wheel (linux-x64, native
library bundled).

Android demo APKs: a plain build produces the default package (arm64-v8a +
x86_64); `dotnet publish -c Release -r android-arm64` (also `-arm`/`-x64`/
`-x86`) builds a single-ABI APK for smaller downloads.

## Repository layout

| Path | Contents |
|---|---|
| `native/` | CMake project: vendored VeraCrypt core + FatFs + `vcapi.h` + C test suite |
| `managed/HCVault.Core/` | .NET 8/10 wrapper (NuGet `HCVault.Core`) |
| `python/` | Python bindings (pure ctypes) + tests + wheel packaging |
| `app/HCVault.Explorer/` | WPF demo |
| `app/HCVault.MauiDemo/` | MAUI demo, Android 7.0+ |
| `app/HCVault.AndroidDemo/` | plain .NET Android demo, Android 5.0+ |
| `tests/` | managed end-to-end tests |
| `docs/` | API documentation (EN/中文) |

## License

Apache-2.0 — see [LICENSE](LICENSE). The vendored components keep their own
licenses (VeraCrypt: Apache-2.0 / TrueCrypt-3.0 dual; FatFs: BSD-style;
Argon2: CC0), listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Release history:
[CHANGELOG.md](CHANGELOG.md).
