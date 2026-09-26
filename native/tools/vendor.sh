#!/bin/bash
# ---------------------------------------------------------------------------
# vendor.sh — reproduce native/src/veracrypt from an upstream VeraCrypt clone
# plus the local patches in tools/patches/.
#
# Usage:   tools/vendor.sh [upstream-src-dir] [dest-dir]
#   upstream defaults to $UPSTREAM_SRC or /home/user/veracrypt/src
#   dest      defaults to <repo>/native/src/veracrypt
#
# Upstream: https://github.com/hcvault/hcvault @ b48e31f5 (verified 2026-07-15)
#
# Patches applied (all marked with "PORT:" comments):
#   001-Platform-File.h.patch               — Win HANDLE as void*, timestamp/seek members
#   002-Core-RandomNumberGenerator.cpp.patch— Windows CNG + JitterEntropy entropy
#   003-Platform-Windows-System.h           — NEW lean windows.h glue header
#   005-Volume-EncryptionThreadPool.cpp     — move_ptr() -> std::move (macro undefined on MSVC >= 1600)
#   006-Volume-Keyfile.cpp                  — drop the security-token/EMV keyfile branch
#                                            (the whole smartcard/PKCS11 header chain is NOT
#             vendored: GUI/smartcard code is out of scope for this library)
#   007-Common-Endian.h                     — compiler-predefined byte order (clang) BEFORE
#             #include <endian.h>: on case-insensitive filesystems the angle include is
#             captured by the vendored Common/Endian.h itself (Windows-hosted NDK builds)
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
UP="${1:-${UPSTREAM_SRC:-/home/user/veracrypt/src}}"
DST="${2:-$(cd "$HERE/.." && pwd)/src/veracrypt}"

[ -d "$UP/Platform" ] || { echo "upstream not found: $UP (set UPSTREAM_SRC=...)" >&2; exit 1; }
mkdir -p "$DST"
DST="$(cd "$DST" && pwd)"
SRC="$(cd "$UP" && pwd)"

echo "vendoring: $SRC -> $DST"

# --- Platform: headers + shared cpp (Unix/*.cpp are replaced by src/port/) ----
mkdir -p "$DST/Platform/Windows" "$DST/Platform/Unix"
cp "$SRC"/Platform/*.h "$DST/Platform/"
cd "$SRC/Platform"
for f in Buffer.cpp Exception.cpp Event.cpp FileCommon.cpp MemoryStream.cpp Memory.cpp \
         PlatformTest.cpp Serializable.cpp Serializer.cpp SerializerFactory.cpp \
         StringConverter.cpp TextReader.cpp; do
  cp "$f" "$DST/Platform/"
done
# header referenced by shared sources (the port layer replaces the Unix glue)
cp Unix/Process.h "$DST/Platform/Unix/"

# --- Volume: everything except WolfCrypt (optional backend) and build files ---
mkdir -p "$DST/Volume"
cd "$SRC/Volume"
for f in *.cpp *.h; do
  case "$f" in EncryptionModeWolfCryptXTS*|Volume.make) ;; *) cp "$f" "$DST/Volume/";; esac
done

# --- Core: mount-free subset ---------------------------------------------------
mkdir -p "$DST/Core"
cd "$SRC/Core"
cp Core.h CoreBase.cpp CoreBase.h CoreException.h FatFormatter.cpp FatFormatter.h \
   HostDevice.cpp HostDevice.h MountOptions.h \
   RandomNumberGenerator.cpp RandomNumberGenerator.h \
   VolumeCreator.cpp VolumeCreator.h "$DST/Core/"

# --- Common: C helpers + headers needed by include chain ------------------------
mkdir -p "$DST/Common"
cd "$SRC/Common"
# (no smartcard/token headers: that subsystem is patched out via 006)
cp Crc.c Crc.h Endian.c Endian.h GfMul.c GfMul.h Pkcs5.c Pkcs5.h \
   Tcdefs.h Crypto.h Password.h Volumes.h Random.h "$DST/Common/"

# --- Boot headers (VolumeLayout dependency, constants only) ---------------------
mkdir -p "$DST/Boot/Windows"
cp "$SRC/Boot/Windows/BootCommon.h" "$SRC/Boot/Windows/BootDefs.h" "$DST/Boot/Windows/"

# --- Crypto: portable set --------------------------------------------------------
mkdir -p "$DST/Crypto/Argon2/include" "$DST/Crypto/Argon2/src/blake2"
cd "$SRC/Crypto"
cp Aes.h Aesopt.h Aes_hw_cpu.h Aeskey.c Aestab.c Aestab.h Aescrypt.c \
   SerpentFast.c SerpentFast.h SerpentFast_sbox.h \
   Twofish.c Twofish.h Camellia.c Camellia.h \
   kuznyechik.c kuznyechik.h Streebog.c Streebog.h \
   Sha2.c Sha2.h Whirlpool.c Whirlpool.h \
   blake2s.c blake2s.h config.h cpu.c cpu.h misc.h rdrand.h \
   chacha256.h chachaRng.h t1ha.h \
   jitterentropy-base.c jitterentropy.h jitterentropy-base-user.h \
   "$DST/Crypto/"
cp Argon2/include/*.h "$DST/Crypto/Argon2/include/"
cp Argon2/src/argon2.c Argon2/src/core.c Argon2/src/core.h Argon2/src/ref.c \
   Argon2/src/opt_sse2.c Argon2/src/opt_avx2.c "$DST/Crypto/Argon2/src/"
cp Argon2/src/blake2/*.h Argon2/src/blake2/*.c "$DST/Crypto/Argon2/src/blake2/"

# --- Licenses ----------------------------------------------------------------------
cp "$SRC/../License.txt" "$DST/LICENSE-VeraCrypt.txt"
cp "$SRC/../README.md"   "$DST/README-upstream.md" 2>/dev/null || true

# =============================================================================
# Local patches (see tools/patches/)
# =============================================================================
cd "$HERE"
patch -d "$DST" -p1 --forward < patches/001-Platform-File.h.patch
patch -d "$DST" -p1 --forward < patches/002-Core-RandomNumberGenerator.cpp.patch
cp patches/003-Platform-Windows-System.h "$DST/Platform/Windows/System.h"
patch -d "$DST" -p1 --forward < patches/005-Volume-EncryptionThreadPool.cpp.patch
patch -d "$DST" -p1 --forward < patches/006-Volume-Keyfile.cpp.patch
patch -d "$DST" -p1 --forward < patches/007-Common-Endian.h.patch

# --- verify ---------------------------------------------------------------------
echo
echo "vendored file count: $(find "$DST" -type f | wc -l)  (expect 158)"
# (006 check below)
grep -q "PORT:" "$DST/Platform/File.h" && echo "File.h: patch OK"
grep -q "BCryptGenRandom" "$DST/Core/RandomNumberGenerator.cpp" && echo "RandomNumberGenerator.cpp: patch OK"
[ -f "$DST/Platform/Windows/System.h" ] && echo "Windows/System.h: OK"
grep -q "security-token / EMV keyfile support removed" "$DST/Volume/Keyfile.cpp" && echo "Keyfile.cpp: patch OK"
grep -q "__BYTE_ORDER__" "$DST/Common/Endian.h" && echo "Endian.h: patch OK"
[ ! -f "$DST/Common/Token.h" ] && [ ! -d "$DST/PKCS11" ] && echo "token chain absent: OK"
echo "done."
