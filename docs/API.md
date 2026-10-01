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
dotnet add package HCVault.Native   # prebuilt native libraries (runtimes/ layout)
```

Build your application with a `RuntimeIdentifier` (`-r linux-x64`,
`-r win-x64`, …) and the matching `hcvault-core` binary is copied to the
output automatically. On Android it is packaged into the APK at
`lib/<abi>/`.

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
| `Cipher` | `VcCipher` | `Aes` | any of the 15 cascades |
| `Kdf` | `VcKdf` | `Argon2id` | header key derivation |
| `Filesystem` | `VcFilesystem` | `Fat` | `Fat`, `ExFat` or `None` (raw sectors) |
| `Quick` | `bool` | `true` | `false` additionally overwrites the data area with random bytes before formatting |
| `Hidden` | `bool` | `false` | create a hidden volume inside the outer volume at `Path` |
| `Progress` | `Action<CreationProgress>?` | null | invoked on the creating thread |

Progress stages (`VcCreationStage`): `WritingData` → `WritingBackupHeader`
→ `Flushing` → `Finished` (or `Error`). `CreationProgress` exposes
`BytesDone`, `BytesTotal`, `Stage` and the computed `Fraction`.

Filesystem limits: the built-in FAT formatter needs roughly ≥ 1 MiB; exFAT
needs ≥ 4 MiB. Below that, create with `Filesystem.None` and use
`Volume.OpenStream` — or format the data area yourself.

### Opening volumes

`Volume.Open(VolumeOpenOptions)` runs the header KDF once per attempt —
including on a wrong password (that is what makes guessing expensive).

| `VolumeOpenOptions` | Type | Default | Notes |
|---|---|---|---|
| `Path` | `string` | required | |
| `Password` | `SecurePassword` | required | |
| `Pim` | `int` | 0 | must match the value used at creation |
| `KeyFiles` | `IReadOnlyList<string>` | empty | same files, same order as at creation |
| `ReadOnly` | `bool` | `false` | write-protected handle (reads and `GetSpace` still work) |
| `UseBackupHeader` | `bool` | `false` | try the embedded backup header first — use when the primary header is damaged |

Volumes created by this library use the current V2 layout; the official
application's V2 **and** legacy V1 volumes open as well. Argon2id volumes
require VeraCrypt 2016+ on the other side.

Properties of an open `Volume`:

| Property | Meaning |
|---|---|
| `DataSize` | usable data area in bytes (file size − 262,144 for V2 volumes, see §3) |
| `SectorSize` | 512 for file-hosted volumes |
| `CipherName` / `KdfName` | algorithms actually protecting this volume (e.g. `"AES"`, `"Argon2"`) |
| `Pim` | the PIM this handle was opened with |
| `IsHidden` | true if this handle opened the hidden volume inside the container |
| `IsOpen` | false after `Dispose` |

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
| `NewKdf` | `null` keeps the current KDF, otherwise switch (e.g. to `Argon2id`) |
| `WipeCount` | header wipe passes, default 1 |

Close any open `Volume` handles on the same file first — the operation
opens the file exclusively.

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

Paths are rooted, `"\\file.txt"` or `"/file.txt"`, either separator,
Unicode names supported (LFN on FAT, UTF-8 on exFAT).

| Member | Behavior |
|---|---|
| `ListDirectory(path)` | one `FatEntry` per item: `Name`, `IsDirectory`, `SizeBytes`, `ModifiedUtc` (UTC), `FirstCluster` (FAT only; 0 on exFAT) |
| `ReadFile(path)` | whole file into a new `byte[]` |
| `WriteFile(path, data)` | create or **overwrite** (truncate or grow) |
| `CreateDirectory(path)` | parent must exist |
| `Delete(path)` | file, or an **empty** directory |
| `GetSpace()` | live `VolumeSpace` (below) |

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
                                  NULL, 0, 0, 0, 0);
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
| `vc_open_volume(path, password, password_len, keyfile_paths, keyfile_count, pim, read_only, use_backup_header)` | returns `NULL` on failure |
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
  and the threat model allows it.
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
