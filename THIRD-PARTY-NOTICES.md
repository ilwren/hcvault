# Third-party notices

HCVault embeds and builds upon the following third-party software. Each
component remains governed by its own license; the full license texts are
shipped with the source trees and inside the NuGet packages.

| Component | Path | License | Notes |
|---|---|---|---|
| VeraCrypt (volume core) | `native/src/veracrypt/` | Apache-2.0 **or** TrueCrypt License 3.0 (dual-licensed, at your option) — see `native/src/veracrypt/LICENSE-VeraCrypt.txt` | Derived from VeraCrypt 1.26.29 and, transitively, TrueCrypt 7.1a. Only a build/platform-patched subset is vendored (see `native/tools/patches/`). |
| FatFs (exFAT/FAT filesystem) | `native/src/fatfs/` | BSD-style license included with the sources — see `native/src/fatfs/LICENSE-FatFs.txt` | R0.15 by ChaN, used via the `vc_exfat_*` C API. |
| Argon2 (KDF) | `native/src/veracrypt/Crypto/Argon2/` | CC0 1.0 Universal | Vendored together with the VeraCrypt sources. |

## Trademark & naming

"VeraCrypt" and "TrueCrypt" are trademarks of their respective owners
(IDRIX / AM Crypto; TrueCrypt Developers Association). The VeraCrypt License
expressly forbids derivative names such as *VeraCrypt+, iVeraCrypt,
Vera-Crypt* or any name confusingly similar to "VeraCrypt". This project is
therefore named **HCVault** (after the customary `.hc` container file
extension) and is **not affiliated with, endorsed by, or sponsored by IDRIX
or the VeraCrypt project**. The marks are used here only descriptively, to
state interoperability with the VeraCrypt volume format, which is the
customary use the license permits.

## Attribution

This software as a whole (packaging, C API, .NET wrapper, demos):
Copyright 2026 HCVault contributors, Apache-2.0.

Vendored portions: Copyright (c) 2013-2025 IDRIX / AM Crypto (VeraCrypt);
Copyright (c) 2003-2012 TrueCrypt Developers Association; Copyright (c)
1998-2000 Paul Le Roux; FatFs Copyright (c) 20xx, ChaN.

## Provenance

The packaging, C API, .NET wrapper, demo apps and documentation of HCVault
were written with AI assistance and have not been independently reviewed or
security-audited. The cryptographic core is vendored, unmodified VeraCrypt
source.
