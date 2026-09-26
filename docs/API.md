# HCVault API documentation

English | [简体中文](API.zh-CN.md)

There are two ways to use HCVault: the managed `HCVault.Core` package for
.NET, or the native `hcvault-core` library through `vcapi.h` from any
language with a C FFI. The managed package is a thin wrapper over the C API,
so the two are interchangeable — pick whichever fits the project.

Both layers are safe to call from multiple threads, with one rule: a single
open volume (or mounted filesystem) belongs to one thread at a time.

---

## 1. HCVault.Core (C# / .NET 8 / .NET 10)

### Installation

```bash
dotnet add package HCVault.Core     # the wrapper
dotnet add package HCVault.Native   # prebuilt native libraries (runtimes/ layout)
```

`HCVault.Core` targets `net8.0` and `net10.0`; the package picks the
matching build automatically. Build your application with a
`RuntimeIdentifier` (`-r linux-x64`, `-r win-x64`, …) and
`HCVault.Native`'s prebuilt library is copied to the output automatically.
Without one, the loader still looks in `$VCNATIVE_HOME`, the application
directory, `runtimes/{rid}/native` and the OS default search path — which
also covers a manual build of `native/` with CMake.

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
    fs.Delete("\\Docs\\notes.txt");
}

// ---- raw sector access (works on Filesystem.None volumes too) -------------
using (var pw  = SecurePassword.FromText("correct horse battery staple"))
using (var vol = Volume.Open(new VolumeOpenOptions { Path = "demo.hc", Password = pw }))
using (var s   = vol.OpenStream(writable: true))  // Stream over the decrypted data
{
    s.Position = 0x1000;
    s.Write(new byte[] { 1, 2, 3, 4 });
}
```

### The API surface

`HCVaultLibrary` — entry point:

- `Initialize()` / `Shutdown()` — run VeraCrypt's crypto self-tests, start
  and stop the RNG / KDF thread pools. The first volume operation calls
  `Initialize` automatically.
- `SupportedCiphers`, `SupportedKdfs` — the 15 cipher cascades and 6 KDFs.
- `VerifyAlgorithmTables()` — cross-checks the managed tables against the
  native core.

`Volume` — a volume, open or being created:

- `Volume.Create(VolumeCreationOptions)` — creates a normal or hidden
  volume. This takes seconds to minutes; call it from a worker thread.
- `Volume.Open(VolumeOpenOptions)` — opens with password / keyfiles / PIM,
  optionally read-only or through the backup header.
- `Volume.ChangePassword(ChangePasswordOptions)` — re-encrypts the header:
  rotate password, KDF and keyfiles without touching the data area.
- `DataSize`, `SectorSize`, `CipherName`, `KdfName`, `Pim`, `IsHidden` —
  properties of an open volume.
- `ReadSectors` / `WriteSectors` — sector-aligned encrypted I/O;
  `OpenStream(writable)` wraps them in a `Stream` that handles unaligned
  access.

The options records:

| Record | Members |
|---|---|
| `VolumeCreationOptions` | `Path`, `SizeBytes` (≥ 200 KiB), `Password`, `Pim`, `KeyFiles`, `Cipher`, `Kdf`, `Filesystem` (`ExFat`/`Fat`/`None`), `Quick`, `Hidden`, `Progress` |
| `VolumeOpenOptions` | `Path`, `Password`, `Pim`, `KeyFiles`, `ReadOnly`, `UseBackupHeader` |
| `ChangePasswordOptions` | `Path`, `OldPassword`/`OldPim`/`OldKeyFiles`, `NewPassword`/`NewPim`/`NewKeyFiles`, `NewKdf`, `WipeCount` |
| `CreationProgress` | `BytesDone`, `BytesTotal`, `Stage`, `Fraction` |

Filesystem access: `VolumeFilesystem.Mount(volume)` sniffs the boot sector
and returns an `IVolumeFileSystem` — `ExFatVolume` (native FatFs) or
`FatVolume` (managed). Both implement:

- `ListDirectory(path)` → `IReadOnlyList<FatEntry>` (`Name`, `IsDirectory`,
  `SizeBytes`, `ModifiedUtc`)
- `ReadFile(path)`, `WriteFile(path, data)` (create or overwrite)
- `CreateDirectory(path)` (parent must exist), `Delete(path)` (file or empty
  directory)
- `GetSpace()` → `VolumeSpace` (`TotalBytes`, `FreeBytes`, `UsedBytes`,
  `ClusterBytes`) — live values. `TotalBytes` counts only the clusters
  available to files, so it stays below `Volume.DataSize`; on exFAT it comes
  from FatFs (`f_getfree`), on FAT from the in-memory FAT.

Paths are rooted, `"\\file.txt"` or `"/file.txt"`, either separator.

Errors throw `VcException` subtypes matching the native status codes:
`VcWrongPasswordException`, `VcVolumeNotFoundException`,
`VcArgumentException`, `VcUnsupportedException`, and so on.

`SecurePassword` holds the password as pinned bytes, wiped on `Dispose`.
Construct with `FromText` or `FromBytes`; do not keep real passwords in
`string`s.

### Things worth knowing

- Volumes below 1 MiB are too small for a FAT filesystem — use
  `Filesystem.None` there (200 KiB is the volume minimum).
- Dispose volumes before calling `ChangePassword` on the same file; it opens
  the file exclusively.
- `Hidden = true` creates a hidden volume inside an existing outer volume:
  `Path` points at the outer volume, `SizeBytes` is the hidden volume's
  size.
- On Android, package `libhcvault-core.so` as an `AndroidNativeLibrary`
  (it ends up at `lib/<abi>/` inside the APK); the default loader probing
  finds it there.

---

## 2. hcvault-core (C ABI)

### Building and linking

```bash
# standalone
cmake -S native -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build
#    -> native/runtimes/<rid>/native/hcvault-core.{dll,so}

# or as part of your own CMake tree
add_subdirectory(native)
target_link_libraries(myapp PRIVATE hcvault-core)
```

The `HCVault.Native` NuGet package compiles the same sources as part of your
build instead. For Android use the CMake presets
(`android-{arm64,x64,arm,x86}-release`, API 21, NDK r26/r27).

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

### Function reference

35 functions, `VC_API_VERSION 4`. The header
([native/src/vcapi/vcapi.h](../native/src/vcapi/vcapi.h)) is self-contained
and fully commented — this is the summary.

| Group | Functions |
|---|---|
| Lifecycle | `vc_init`, `vc_shutdown`, `vc_api_version`, `vc_last_error`, `vc_last_status` |
| Algorithms | `vc_get_cipher_count`, `vc_get_cipher_name`, `vc_get_kdf_count`, `vc_get_kdf_name` |
| Create | `vc_create_volume` |
| Open / I/O | `vc_open_volume`, `vc_close_volume`, `vc_read_sectors`, `vc_write_sectors` |
| Info | `vc_get_data_size`, `vc_get_sector_size`, `vc_get_pim`, `vc_get_cipher_used`, `vc_get_kdf_used`, `vc_is_hidden` |
| Header | `vc_change_password` |
| exFAT | `vc_exfat_format`, `vc_exfat_mount`, `vc_exfat_unmount`, `vc_exfat_mkdir`, `vc_exfat_delete`, `vc_exfat_opendir`, `vc_exfat_readdir`, `vc_exfat_closedir`, `vc_exfat_open`, `vc_exfat_read`, `vc_exfat_write`, `vc_exfat_seek`, `vc_exfat_close`, `vc_exfat_get_space` |

Conventions:

- Functions return `VC_OK` (0) or an error status; handle getters like
  `vc_open_volume` return `NULL`. Details come from `vc_last_error()` /
  `vc_last_status()` — per-thread, valid until the next call on that thread.
- All strings are UTF-8.
- In `vc_read_sectors` / `vc_write_sectors`, `offset` and `len` must be
  multiples of the volume's sector size, and `offset` counts from the start
  of the data area (the decrypted view — no header).
- One `vc_volume` / `vc_exfat` handle per thread at a time.
- After loading the library, check `vc_api_version() == VC_API_VERSION`.
  The `vc_*` ABI is append-only: functions are never removed or re-signatured.

---

## 3. Notes on interop and security

Volumes are interchangeable in every direction: created here, opened by the
official application, and vice versa. The security discussion (what is
inherited from VeraCrypt, what is different here, and the caveats) is in the
[README](../README.md), under "Security".

Two small things that bite people occasionally: KDF names are the canonical
VeraCrypt strings (`"Argon2"` is Argon2id) — enumerate them at runtime
instead of hard-coding; and quick mode skips the pre-format wipe, so pass a
full format when the previous contents of the disk matter.
