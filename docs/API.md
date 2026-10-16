# HCVault API documentation

English | [简体中文](API.zh-CN.md)

There are two ways to use HCVault: the managed `HCVault.Core` package for
.NET, or the native `hcvault-core` library through `vcapi.h` from any
language with a C FFI. The managed package is a thin wrapper over the C API,
so the two are interchangeable — pick whichever fits the project. Python
users get a ready-made thin wrapper of the same C API in
[python/](../python/README.md) — its names follow this document, so the
reference below applies to it as well.

Both layers are safe to call from multiple threads, with one rule: a single
open volume (or mounted filesystem) belongs to one thread at a time.

---

## 1. HCVault.Core (C# / .NET 8 / .NET 10)

### Installation

```bash
dotnet add package HCVault.Core     # the wrapper (net8.0 + net10.0)
```

`HCVault.Core` depends on the exact matching `HCVault.Native` version, so
installing it pulls the prebuilt native libraries (runtimes/ layout) in
automatically; reference `HCVault.Native` explicitly only to force a
different version.

Build your application with a `RuntimeIdentifier` (`-r linux-x64`,
`-r win-x64`, …) and the matching `hcvault-core` binary is copied to the
output automatically. On Android it is packaged into the APK at
`lib/<abi>/`. A plain build **without** a RuntimeIdentifier (the default
when you hit F5) copies the library for the machine you are building on —
fine for debugging; set a RuntimeIdentifier for deployment builds.

The wrapper locates the native library at runtime, in this order:

| # | Location | Typical use |
|---|---|---|
| 1 | `$VCNATIVE_HOME` (directory or full library path) | CI, unusual layouts |
| 2 | application directory | manual deployment |
| 3 | `<app>/runtimes/{rid}/native/` | NuGet `runtimes/` convention |
| 4 | `native/runtimes/{rid}/native/` up to 6 levels above the app | building inside the repository |
| 5 | OS default probing | everything else |

If loading fails, the thrown `DllNotFoundException` says which files were
found but rejected, what each file actually is (PE/ELF architecture), and
what to rebuild.

### A complete example

```csharp
using HCVault.Core;

// ---- create (exFAT inside, AES + Argon2id) --------------------------------
using (var pw = SecurePassword.FromText("correct horse battery staple"))
{
    Volume.Create(new VolumeCreationOptions
    {
        Path        = "demo.hc",
        SizeBytes   = 64 * 1024 * 1024,          // minimum is 200 KiB
        Password    = pw,
        Cipher      = VcCipher.Aes,              // 15 cascades available
        Kdf         = VcKdf.Argon2id,            // 6 KDFs available
        Filesystem  = VcFilesystem.ExFat,        // ExFat | Fat | None
        Quick       = true,                      // false = wipe data area first
        Progress    = p => Console.WriteLine($"{p.Fraction:P0} {p.Stage}"),
    });
}

// ---- open + file I/O inside the volume ------------------------------------
using (var pw  = SecurePassword.FromText("correct horse battery staple"))
using (var vol = Volume.Open(new VolumeOpenOptions { Path = "demo.hc", Password = pw }))
{
    Console.WriteLine($"{vol.CipherName} / {vol.KdfName}, data {vol.DataSize} B");

    using var fs = VolumeFilesystem.Mount(vol);   // sniffs FAT vs exFAT
    fs.CreateDirectory("\\Docs");
    fs.WriteFile("\\Docs\\notes.txt", "top secret"u8.ToArray());
    byte[] back = fs.ReadFile("\\Docs\\notes.txt");

    foreach (var e in fs.ListDirectory("\\"))
        Console.WriteLine($"{(e.IsDirectory ? "<dir>" : $"{e.SizeBytes} B"),10}  {e.Name}");

    VolumeSpace s = fs.GetSpace();
    Console.WriteLine($"{s.FreeBytes:N0} free of {s.TotalBytes:N0} (cluster {s.ClusterBytes} B)");

    fs.Delete("\\Docs\\notes.txt");
}

// ---- raw sector access (works on Filesystem.None volumes too) -------------
using (var pw  = SecurePassword.FromText("correct horse battery staple"))
using (var vol = Volume.Open(new VolumeOpenOptions { Path = "demo.hc", Password = pw }))
using (var s   = vol.OpenStream(writable: true))  // Stream over the decrypted data
{
    s.Position = 0x1000;
    s.Write(new byte[] { 1, 2, 3, 4 });
}   // disposing flushes the partially written sector
```

Error handling: every failure throws a `VcException` subtype (see the table
at the end of this section), with the native error text in `Message`. A
wrong password throws `VcWrongPasswordException` — catch it and retry:

```csharp
try { vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw }); }
catch (VcWrongPasswordException) { /* ask again / give up */ }
```

### Creating volumes

`Volume.Create(VolumeCreationOptions)` writes the volume file synchronously;
it takes seconds to minutes (see *Performance* below) — call it from a
worker thread in UI applications.

| `VolumeCreationOptions` | Type | Default | Notes |
|---|---|---|---|
| `Path` | `string` | required | volume file to create (for hidden volumes: the **existing outer** volume) |
| `SizeBytes` | `long` | required | total file size (hidden: size of the hidden area); ≥ 200 KiB |
| `Password` | `SecurePassword` | required | may be empty if keyfiles carry the entropy |
| `Pim` | `int` | 0 | 0 = library default; a custom PIM must be re-supplied on every open |
| `KeyFiles` | `IReadOnlyList<string>` | empty | mixed into the password **in list order**, exactly like the VeraCrypt application |
| `KeyFileStreams` | `IReadOnlyList<Stream>?` | null | keyfile contents as streams (or wrap bytes in a `MemoryStream`) instead of / in addition to paths; see below |
| `Cipher` | `VcCipher` | `Aes` | any of the 15 cascades |
| `Kdf` | `VcKdf` | `Argon2id` | header key derivation |
| `Filesystem` | `VcFilesystem` | `Fat` | `Fat`, `ExFat` or `None` (raw sectors) |
| `Quick` | `bool` | `true` | `false` additionally overwrites the data area with random bytes before formatting |
| `Hidden` | `bool` | `false` | create a hidden volume inside the outer volume at `Path` |
| `Progress` | `Action<CreationProgress>?` | null | invoked on the creating thread |

Progress stages (`VcCreationStage`): `WritingData` → `WritingBackupHeader`
→ `Flushing` → `Finished` (or `Error`). `CreationProgress` exposes
`BytesDone`, `BytesTotal`, `Stage` and the computed `Fraction`.

Filesystem limits (exact, enforced with a clear error before the native
call): after 256 KiB of volume headers, FAT needs a 36,864-byte data area
(container ≥ 299,008 B) and is limited to a 2 TiB data area; exFAT needs
2 MiB (container ≥ 2,359,296 B); `Filesystem.None` accepts anything above
262,145 B. Hidden volumes (different layout) work from 200 KiB.

**Keyfile streams.** When `KeyFileStreams` is non-empty, the keyfile mixing
happens in this library instead of the native core: each stream is read (not
disposed), only its first 1 MiB is mixed in, and the `KeyFiles` paths are read
as regular files first, then the streams, in list order. The result is
byte-identical to native keyfile paths (the test suite verifies both
directions), so volumes created either way open with either form. Unlike the
path-only mode, directory entries are not expanded — every source must be a
file or stream. At least one byte per source is required. Every source is
read from its start (seekable streams are rewound automatically, so the
same instances can back a later open), and the streams must stay alive —
not disposed — until the call returns; `new MemoryStream(bytes)` is the
simplest source.

`Volume.CreateAsync(options, cancellationToken)` runs the same creation on
a thread-pool thread for `await`-based callers. The token only prevents a
creation that has not started yet — the native formatting itself cannot be
interrupted.

### Opening volumes

`Volume.Open(VolumeOpenOptions)` runs the header KDF once per attempt —
including on a wrong password (that is what makes guessing expensive).

| `VolumeOpenOptions` | Type | Default | Notes |
|---|---|---|---|
| `Path` | `string` | required | |
| `Password` | `SecurePassword` | required | |
| `Pim` | `int` | 0 | must match the value used at creation |
| `KeyFiles` | `IReadOnlyList<string>` | empty | same files, same order as at creation |
| `KeyFileStreams` | `IReadOnlyList<Stream>?` | null | stream form of the same keyfiles (see the note under *Creating volumes*) |
| `ReadOnly` | `bool` | `false` | write-protected handle (reads and `GetSpace` still work) |
| `UseBackupHeader` | `bool` | `false` | try the embedded backup header first — use when the primary header is damaged |
| `Kdf` | `VcKdf?` | null | the KDF the volume was created with — skips the try-every-KDF search; wrong credentials then fail after a single derivation (a fraction of the time and memory). No downside when the value is known |

Volumes created by this library use the current V2 layout; the official
application's V2 **and** legacy V1 volumes open as well. Argon2id volumes
require VeraCrypt 2016+ on the other side.

Because the header is encrypted, a volume does not reveal which KDF it
uses: by default every open derives keys with every supported KDF (and
every failed attempt pays that full search, including a complete Argon2
pass per layout). Set `Kdf` to the value used at creation and the open
runs exactly that KDF - wrong credentials then fail in a fraction of the
time and memory.

Properties of an open `Volume`:

| Property | Meaning |
|---|---|
| `DataSize` | usable data area in bytes (file size − 262,144 for V2 volumes, see §3) |
| `SectorSize` | 512 for file-hosted volumes |
| `CipherName` / `KdfName` | algorithms actually protecting this volume (e.g. `"AES"`, `"Argon2"`) |
| `Pim` | the PIM this handle was opened with |
| `IsHidden` | true if this handle opened the hidden volume inside the container |
| `IsOpen` | false after `Dispose` |

`Volume.OpenAsync(options)` is the awaitable counterpart of `Open`.

### Sector-level I/O

- `ReadSectors(buffer, offset, length)` / `WriteSectors(…)` — the raw
  native access. `offset` is relative to the start of the **data area**, and
  both `offset` and `length` must be multiples of `SectorSize`.
- `OpenStream(writable)` — a buffered `Stream` over the same view that
  accepts unaligned positions and lengths. It caches one sector, so partial
  writes are combined with the sector's original content; `Dispose`/`Flush`
  writes the cached sector back. `Length` equals `DataSize`.

`Volume` instances are not thread-safe; open one per thread or synchronize
externally. Internally the library parallelizes encryption across cores.

### Changing credentials

`Volume.ChangePassword(ChangePasswordOptions)` re-encrypts only the volume
header (a fraction of a second of I/O, plus two KDF runs) — the data area
is untouched. Works on normal and hidden volumes: the first header that
opens with the old credentials is the one re-encrypted.

| `ChangePasswordOptions` | Notes |
|---|---|
| `OldPassword` / `OldPim` / `OldKeyFiles` | current credentials |
| `NewPassword` / `NewPim` / `NewKeyFiles` | replacement (all optional individually) |
| `OldKeyFileStreams` / `NewKeyFileStreams` | stream form of the keyfiles, one list per side (see *Creating volumes*) |
| `NewKdf` | `null` keeps the current KDF, otherwise switch (e.g. to `Argon2id`) |
| `WipeCount` | header wipe passes, default 1 |

Close any open `Volume` handles on the same file first — the operation
opens the file exclusively.

`Volume.ChangePasswordAsync(options)` is the awaitable counterpart.

### Hidden volumes

A hidden volume lives in the free space of an outer volume; only its size
is fixed at creation, its position is not recorded anywhere:

```csharp
// outer: Filesystem.None, quick
Volume.Create(new VolumeCreationOptions { Path = "outer.hc", SizeBytes = 64 << 20,
                                          Password = outerPw, Filesystem = VcFilesystem.None, Quick = true });
// inner: inside outer.hc, its own password and filesystem
Volume.Create(new VolumeCreationOptions { Path = "outer.hc", SizeBytes = 16 << 20,
                                          Password = innerPw, Filesystem = VcFilesystem.Fat,
                                          Quick = true, Hidden = true });
```

Which volume opens depends solely on which password is supplied. Two rules
from the VeraCrypt threat model apply here too: keep some plausible files
in the outer volume, and **writing to the outer volume can destroy the
hidden one** — the outer filesystem does not know the hidden area is in
use.

### Algorithms

Ciphers (`VcCipher`, all XTS, key size 256 bit per cascade member):

| Enum | Native name | Enum | Native name |
|---|---|---|---|
| `Aes` | AES | `CamelliaKuznyechik` | Camellia-Kuznyechik |
| `Serpent` | Serpent | `CamelliaSerpent` | Camellia-Serpent |
| `Twofish` | Twofish | `KuznyechikAes` | Kuznyechik-AES |
| `Camellia` | Camellia | `KuznyechikSerpentCamellia` | Kuznyechik-Serpent-Camellia |
| `Kuznyechik` | Kuznyechik | `KuznyechikTwofish` | Kuznyechik-Twofish |
| `AesTwofish` | AES-Twofish | `SerpentAes` | Serpent-AES |
| `AesTwofishSerpent` | AES-Twofish-Serpent | `SerpentTwofishAes` | Serpent-Twofish-AES |
| `TwofishSerpent` | Twofish-Serpent | | |

Header KDFs (`VcKdf`): `HmacSha512` (classic default), `HmacSha256`,
`HmacBlake2s256`, `HmacWhirlpool`, `HmacStreebog`, `Argon2id` (native name
`"Argon2"`). Enumerate at runtime with `HCVaultLibrary.SupportedCiphers` /
`SupportedKdfs` instead of hard-coding the name mapping.

### The filesystem layer

`VolumeFilesystem.Mount(volume)` reads the boot sector and returns an
`ExFatVolume` (native FatFs) or `FatVolume` (managed implementation); a
data area without a recognized filesystem throws `VcException` (e.g.
`Filesystem.None` volumes).

Paths accept `\\` or `/` interchangeably, and the leading separator is
optional — `"file.txt"`, `"/file.txt"` and `"\\file.txt"` are the same
file, `"docs/note.txt"` addresses a subdirectory, and `""` (or `"/"`) is
the root. Unicode names are supported (LFN on FAT, UTF-8 on exFAT).

| Member | Behavior |
|---|---|
| `ListDirectory(path)` | one `FatEntry` per item: `Name`, `IsDirectory`, `SizeBytes`, `ModifiedUtc` (UTC), `FirstCluster` (FAT only; 0 on exFAT) |
| `ReadFile(path)` | whole file into a new `byte[]` |
| `WriteFile(path, data)` | create or **overwrite** (truncate or grow) |
| `CreateDirectory(path)` | parent must exist |
| `Delete(path)` | file, or an **empty** directory |
| `GetSpace()` | live `VolumeSpace` (below) |
| `OpenFile(path, mode)` | `Stream` over one file — random-position reads, in-place writes, zero-filled holes, appends (`VcFileMode.Read` / `Create` / `Append`) |

`VolumeSpace` (all in bytes, live values):

| Field | Meaning |
|---|---|
| `TotalBytes` | capacity available to files = all clusters. Below `Volume.DataSize` (see §3) |
| `FreeBytes` | unallocated space |
| `UsedBytes` | `TotalBytes − FreeBytes` (files, directories, filesystem bookkeeping) |
| `ClusterBytes` | allocation granularity — every file occupies a multiple of this |

Writing a 1 MiB file decreases `FreeBytes` by exactly 1 MiB (rounded up to
whole clusters), deleting it restores the value. On exFAT the numbers come
from FatFs (`f_getfree`, allocation-bitmap scan); on FAT they are computed
from the in-memory FAT table.

### Import and export

Extension methods on `IVolumeFileSystem` move files and whole directory
trees between the host and the volume:

| Method | Effect |
|---|---|
| `ImportFile(localPath, volumePath = null)` | copy one local file in; the default target is the volume root with the same file name |
| `ExportFile(volumePath, localPath)` | copy one file out (local parent directories are created) |
| `ImportDirectory(localDir, volumePath = "\\")` | copy a local directory tree in (target and missing parents are created; subdirectories first, then files, sorted by name) |
| `ExportDirectory(volumePath, localDir)` | copy a directory tree out |

```csharp
using var vol = Volume.Open(new VolumeOpenOptions { Path = "vault.hc", Password = pw });
using var fs = VolumeFilesystem.Mount(vol);
fs.ImportDirectory(@"C:\Users\me\Documents");     // -> \\Documents\...
fs.ExportDirectory("\\Documents", @"D:\restore\Documents");
```

Files are transferred whole (one file fully in memory at a time).

Every read, write and transfer above has an `...Async` counterpart
(`ReadFileAsync`, `WriteFileAsync`, `ListDirectoryAsync`,
`ImportFileAsync`, `ExportFileAsync`, `ImportDirectoryAsync`,
`ExportDirectoryAsync`) running the operation on a thread-pool thread, with
the same semantics as `Volume.CreateAsync`: the cancellation token only
prevents an operation that has not started yet.

### Streaming access and re-formatting

`OpenFile` returns a `Stream` over a single file inside the volume:
random-position reads, in-place writes, zero-filled holes and appends, in
constant memory — `ReadFile`/`WriteFile` remain the simple whole-file
option.

```csharp
using var s = fs.OpenFile("video.mkv", VcFileMode.Read);
s.Seek(64 * 1024, SeekOrigin.Begin);   // read a header without loading gigabytes
```

Writes that grow the file update its directory entry immediately (an
interrupted stream never lengthens a file beyond what was written);
`SetLength` is not supported — open with `VcFileMode.Create` to truncate.
On exFAT the stream is backed by the native FatFs bridge (vcapi v6 adds
tell/size/flush), on FAT by the managed driver.

`VolumeFilesystem.FormatExFat(volume)` formats the data area of an open
volume as exFAT in place, destroying its contents — a FAT volume converts
without recreating the container, and a `Filesystem.None` volume gains a
filesystem. Mount afterwards to use it.

### Sizing a container

`ContainerSize` answers sizing questions exactly, using the same geometry
the bundled formatters produce (256 KiB of volume headers, FAT/exFAT tables
and system clusters, automatic cluster-size steps):

| Method | Returns |
|---|---|
| `GetFileCapacity(containerSizeBytes, filesystem)` | the largest single file a freshly created container of that size can hold |
| `GetRequiredContainerSize(fileSizeBytes, filesystem)` | the smallest container (multiple of 512) that can hold one file of that size; never below what `Volume.Create` accepts for that filesystem |

```csharp
long container = ContainerSize.GetRequiredContainerSize(
    700L * 1000 * 1000, VcFilesystem.Fat);   // smallest FAT container for a 700 MB file
long capacity  = ContainerSize.GetFileCapacity(container, VcFilesystem.Fat);   // >= 700,000,000
```

The values are exact for the formatter versions shipped in the native
library — the test suite creates real volumes at the computed sizes and
matches the free space byte for byte. `VcFilesystem.None` reduces to the
raw data-area size (container minus 256 KiB of headers).

### Errors

| Exception | Native status | Typical cause |
|---|---|---|
| `VcWrongPasswordException` | `VC_ERR_WRONG_PASSWORD` (2) | wrong password / keyfiles / PIM |
| `VcArgumentException` | `VC_ERR_ARG` (3) | invalid path, size below minimum, … |
| `VcUnsupportedException` | `VC_ERR_UNSUPPORTED` (5) | API-version mismatch, unknown algorithm |
| `VcException` | `VC_ERR_VOLUME_NOT_FOUND` (4) | file missing or no valid volume header |
| `VcException` | `VC_ERR_GENERIC` (1) | everything else; `Message` carries the native error text |

`DllNotFoundException` from the first volume operation means the native
library was not found/loadable — the message explains where it looked.

### `SecurePassword`

Passwords are held as pinned, wipe-on-dispose UTF-8 bytes
(`FromText` / `FromBytes`); the CLR `string` type is avoided because its
contents cannot be erased deterministically. Dispose the objects as soon
as the API call has used them, and never store real passwords in strings.

### `HCVaultLibrary`

`Initialize()` (idempotent, thread-safe; runs VeraCrypt's crypto self-tests
and starts the RNG and KDF thread pools — the first volume operation calls
it automatically) and `Shutdown()`. `VerifyAlgorithmTables()` cross-checks
the managed enum tables against the native core — a cheap guard against
wrapper/core version skew, useful in test suites.

---

## 2. hcvault-core (C ABI)

### Building and linking

```bash
# standalone (output: native/runtimes/<rid>/native/hcvault-core.{dll,so})
cmake -S native -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build

# or as part of your own CMake tree
add_subdirectory(native)
target_link_libraries(myapp PRIVATE hcvault-core)

# Android (NDK r26/r27, API 21, 16 KB-page aligned):
#   presets android-{arm64,x64,arm,x86}-release
# Windows (MSVC, static CRT — no Visual C++ Redistributable needed):
#   presets windows-{x64,x86,arm64}-release
```

Link with `-lhcvault-core` (Linux) or `hcvault-core.lib` (Windows), call
`vc_api_version()` once after loading and compare against `VC_API_VERSION`.
The Windows library is built with `/MT`; the Linux/Android libraries have
no runtime dependencies beyond libc/libstdc++ (statically linked on
Android).

### A complete example (C)

```c
#include <stdio.h>
#include "vcapi.h"

int main(void)
{
    vc_init();                       /* crypto self-tests + RNG pool */

    /* create a 64 MiB volume: AES + Argon2id + built-in FAT, quick format */
    int rc = vc_create_volume("demo.hc", 64ULL << 20,
                              "correct horse battery staple", 27,
                              NULL, 0,                 /* no keyfiles */
                              "AES", "Argon2", "FAT",
                              0,                       /* PIM 0 = default */
                              1, 0,                    /* quick, not hidden */
                              NULL, NULL);             /* no progress cb */
    if (rc != VC_OK) { fprintf(stderr, "create: %s\n", vc_last_error()); return 1; }

    vc_volume *v = vc_open_volume("demo.hc", "correct horse battery staple", 27,
                                  NULL, 0, 0, 0, 0, NULL);  /* last arg: KDF hint, NULL = auto */
    if (!v) { fprintf(stderr, "open: %s\n", vc_last_error()); return 1; }
    printf("data area: %llu bytes, sector %u, %s/%s\n",
           (unsigned long long)vc_get_data_size(v),
           vc_get_sector_size(v), vc_get_cipher_used(v), vc_get_kdf_used(v));

    unsigned char buf[512];
    if (vc_read_sectors(v, buf, 0, sizeof buf) < 0)
        fprintf(stderr, "read: %s\n", vc_last_error());

    vc_close_volume(v);
    return 0;
}
```

exFAT file operations on an open volume (FatFs-backed):

```c
vc_exfat      *fs = vc_exfat_mount(v);        /* or vc_exfat_format(v) */
vc_exfat_mkdir(fs, "/Docs");

vc_exfat_file *f = vc_exfat_open(fs, "/Docs/notes.txt", 1 /*create/truncate*/);
vc_exfat_write(f, (const uint8_t *)"hello", 5);
vc_exfat_close(f);

vc_exfat_dir  *d = vc_exfat_opendir(fs, "/Docs");
vc_exfat_entry e;
while (vc_exfat_readdir(d, &e) == 1)
    printf("%s %llu\n", e.name, (unsigned long long)e.size);
vc_exfat_closedir(d);

vc_exfat_space sp;                          /* total / free / cluster bytes */
if (vc_exfat_get_space(fs, &sp) == VC_OK)
    printf("free %llu of %llu bytes\n",
           (unsigned long long)sp.free_bytes,
           (unsigned long long)sp.total_bytes);

vc_exfat_unmount(fs);
```

### Conventions

- Functions return `VC_OK` (0) or a `vc_status`; handle-returning functions
  (`vc_open_volume`, `vc_exfat_mount`, …) return `NULL` on failure.
- `vc_last_error()` / `vc_last_status()` describe the last failure **on the
  calling thread**; the string is valid until the next call on that thread.
- All strings are UTF-8. Paths for the exFAT layer are rooted at the data
  area and accept `/` or `\`.
- `vc_read_sectors` / `vc_write_sectors`: `offset` and `len` must be
  multiples of the sector size; `offset` counts from the start of the data
  area (the decrypted view, no header).
- One `vc_volume` / `vc_exfat` handle per thread at a time; the library
  itself is thread-safe and initializes lazily.
- The ABI is append-only: functions are never removed or re-signatured, so
  a newer library always works with older callers. Check
  `vc_api_version() == VC_API_VERSION` after loading.

Status codes (`vc_status`):

| Value | Name | Meaning |
|---|---|---|
| 0 | `VC_OK` | success |
| 1 | `VC_ERR_GENERIC` | see `vc_last_error()` |
| 2 | `VC_ERR_WRONG_PASSWORD` | password/keyfiles/PIM rejected |
| 3 | `VC_ERR_ARG` | invalid argument |
| 4 | `VC_ERR_VOLUME_NOT_FOUND` | file missing / no valid header |
| 5 | `VC_ERR_UNSUPPORTED` | feature not available in this build |

### Function reference

35 functions, `VC_API_VERSION 4`. The header
([native/src/vcapi/vcapi.h](../native/src/vcapi/vcapi.h)) is self-contained
and fully commented — this is the summary.

**Lifecycle**

| Function | Description |
|---|---|
| `vc_init()` | one-time init: crypto self-tests, RNG pool; idempotent, thread-safe |
| `vc_shutdown()` | stop background threads (optional; safety net runs at exit) |
| `vc_api_version()` | ABI version of the loaded library (`VC_API_VERSION`) |
| `vc_last_error()` | UTF-8 text of the last error on this thread |
| `vc_last_status()` | `vc_status` of the last failed call on this thread |

**Algorithms** — names are the canonical VeraCrypt strings; count first,
then index `0..count-1` (out-of-range returns NULL, not an error):

| Function | Description |
|---|---|
| `vc_get_cipher_count()` / `vc_get_cipher_name(i)` | 15 ciphers/cascades |
| `vc_get_kdf_count()` / `vc_get_kdf_name(i)` | 6 header KDFs |

**Creation**

`vc_create_volume(path, size_bytes, password, password_len, keyfile_paths,
keyfile_count, cipher, kdf, filesystem, pim, quick, hidden, progress,
progress_user)` — create a normal or hidden volume. `filesystem` is
`"FAT"`, `"EXFAT"` or `"NONE"`; `pim` 0 = default; `quick` nonzero skips
the data-area wipe; `hidden` nonzero creates inside the existing outer
volume at `path` (`size_bytes` = hidden size). `progress` is invoked on the
calling thread with the stage constants `VC_STAGE_WRITING_DATA` (1),
`VC_STAGE_WRITING_BACKUP_HEADER` (2), `VC_STAGE_FLUSHING` (3),
`VC_STAGE_FINISHED` (4), `VC_STAGE_ERROR` (5).

**Open / sector I/O / info**

| Function | Description |
|---|---|
| `vc_open_volume(path, password, password_len, keyfile_paths, keyfile_count, pim, read_only, use_backup_header, kdf_name)` | returns `NULL` on failure; `kdf_name` NULL/"" = try every KDF, e.g. `"Argon2"` / `"SHA-512"` = use only that one |
| `vc_close_volume(v)` | destroy the handle |
| `vc_read_sectors(v, buf, offset, len)` / `vc_write_sectors(v, buf, offset, len)` | sector-aligned I/O; byte count or negative status |
| `vc_get_data_size(v)` | usable data area in bytes |
| `vc_get_sector_size(v)` | 512 for file-hosted volumes |
| `vc_get_pim(v)` / `vc_get_cipher_used(v)` / `vc_get_kdf_used(v)` / `vc_is_hidden(v)` | properties of the open volume |

**Header re-encryption**

`vc_change_password(path, old_password, …, new_password, …, new_kdf,
wipe_count)` — same semantics as the managed `ChangePassword` (§1);
`new_kdf` NULL keeps the current KDF.

**exFAT (FatFs bridge)**

| Function | Description |
|---|---|
| `vc_exfat_format(v)` | format the data area of an **open** volume as exFAT (destroys contents) |
| `vc_exfat_mount(v)` / `vc_exfat_unmount(fs)` | mount (validates the filesystem) / release |
| `vc_exfat_mkdir(fs, path)` / `vc_exfat_delete(fs, path)` | delete takes a file or an **empty** directory |
| `vc_exfat_opendir(fs, path)` / `vc_exfat_readdir(dir, out)` / `vc_exfat_closedir(dir)` | `readdir` returns 1 = entry, 0 = end, < 0 = error |
| `vc_exfat_open(fs, path, mode)` | mode 0 = read existing, 1 = create/truncate, 2 = open/append |
| `vc_exfat_read(f, buf, len)` / `vc_exfat_write(f, buf, len)` | byte counts, or negative status; a short write means the volume is full |
| `vc_exfat_tell(f)` / `vc_exfat_size(f)` / `vc_exfat_flush(f)` | stream position, file length, explicit flush (f_sync) |
| `vc_exfat_format(volume)` | format an open volume's data area as exFAT (contents destroyed) |
| `vc_exfat_seek(f, position)` / `vc_exfat_close(f)` | close flushes |

`vc_exfat_entry` fields: `name[256]` (UTF-8, NUL-terminated), `is_directory`,
`size`, `modified_date` / `modified_time` (FAT date/time encoding, UTC),
`reserved0` / `reserved1` (alignment, must be ignored).

`vc_exfat_get_space(fs, out)` fills `vc_exfat_space`
{ `total_bytes`, `free_bytes`, `cluster_bytes` } with live values — the
same numbers as the managed `GetSpace()`.

---

## 3. Notes on interop, geometry and performance

### Where the bytes of a volume go

A V2 volume file is: 64 KiB header area + 64 KiB reserved hidden-volume
slot + the data area + 128 KiB backup header group. So for an 8 MiB
container:

| Layer | Bytes |
|---|---|
| Volume file | 8,388,608 |
| `DataSize` (decrypted area) | 8,126,464 (= file − 262,144) |
| exFAT `TotalBytes` (all clusters, 4 KiB each) | 8,101,888 |
| exFAT `FreeBytes` right after formatting | 8,085,504 |

The difference between `DataSize` and `TotalBytes` is the filesystem's own
bookkeeping (boot sectors, FAT/bitmap, root directory); `UsedBytes` grows
in whole clusters as files are written.

### Compatibility

Volumes are interchangeable in every direction with the official
application: created here, opened there, and vice versa (Argon2id requires
VeraCrypt 2016+). Keyfiles are applied exactly like the VeraCrypt
application does — a keyfile pool built from the listed files in order,
combined with the password bytes. Legacy V1 volumes open read/write.

### Performance

- Creating a volume runs the header KDF **twice** (main + backup header),
  every open runs it **once — including failed attempts**. With the default
  PIM, Argon2id uses 416 MiB of memory and 6 passes (VeraCrypt defaults);
  on mobile hardware prefer `VcKdf.HmacSha512` when startup time matters
  and the threat model allows it. The Argon2 memory cost scales with the
  PIM (64 MiB + 32 MiB per step, capped at 1 GiB — PIM 22 means 736 MiB)
  and is allocated and released for a few seconds on every open: this is
  the memory-hard design, not a leak, and the official application behaves
  the same way.
- **A wrong password / keyfile / PIM is expensive on purpose.** The header
  is encrypted, so the core cannot know which KDF the volume uses - a
  failed attempt derives keys with **every** supported KDF (including a
  full Argon2 pass) and, for normal opens, additionally tries the hidden
  volume layouts before giving up. Expect several seconds and the Argon2
  memory spike per attempt, then a `VcWrongPasswordException` - which you
  should catch instead of letting it terminate the process. Use the exact
  PIM (and keyfiles) to keep failed attempts as rare as real ones.
- `Quick = true` skips the pre-format overwrite of the data area — the area
  is still fully encrypted, but pre-existing plaintext patterns remain
  visible in the ciphertext statistics. Use a full format when the previous
  contents of the disk matter.
- `Volume.OpenStream` caches a single sector; sequential streaming is
  close to raw sector throughput. Random unaligned writes pay one extra
  read-modify-write per sector.
- exFAT (FatFs) and the managed FAT driver both cache aggressively in
  memory; `Delete`/overwrite calls flush what they change.

### Security notes

The security discussion (what is inherited from VeraCrypt, what differs
here, and the caveats — including that this project is independent,
AI-assisted and not security-audited) is in the [README](../README.md),
under "Security". Passwords live in pinned, zero-on-dispose buffers
(`SecurePassword`); the native library never writes plaintext outside the
volume's own decrypted view; no admin rights or drivers are involved —
everything is file-hosted userspace I/O.
