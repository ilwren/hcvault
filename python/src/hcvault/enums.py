"""Algorithm and option enums.

The enum *values* are the canonical VeraCrypt names expected by the native
API - the same strings ``vc_get_cipher_name`` / ``vc_get_kdf_name`` report.
"""

from __future__ import annotations

import enum


class VcCipher(str, enum.Enum):
    """Encryption algorithms (all XTS, 256 bit per cascade member)."""

    AES = "AES"
    SERPENT = "Serpent"
    TWOFISH = "Twofish"
    CAMELLIA = "Camellia"
    KUZNYECHIK = "Kuznyechik"
    AES_TWOFISH = "AES-Twofish"
    AES_TWOFISH_SERPENT = "AES-Twofish-Serpent"
    CAMELLIA_KUZNYECHIK = "Camellia-Kuznyechik"
    CAMELLIA_SERPENT = "Camellia-Serpent"
    KUZNYECHIK_AES = "Kuznyechik-AES"
    KUZNYECHIK_SERPENT_CAMELLIA = "Kuznyechik-Serpent-Camellia"
    KUZNYECHIK_TWOFISH = "Kuznyechik-Twofish"
    SERPENT_AES = "Serpent-AES"
    SERPENT_TWOFISH_AES = "Serpent-Twofish-AES"
    TWOFISH_SERPENT = "Twofish-Serpent"

    @property
    def native_name(self) -> str:
        return self.value


class VcKdf(str, enum.Enum):
    """Header key-derivation functions."""

    HMAC_SHA512 = "HMAC-SHA-512"
    HMAC_SHA256 = "HMAC-SHA-256"
    HMAC_BLAKE2S256 = "HMAC-BLAKE2s-256"
    HMAC_WHIRLPOOL = "HMAC-Whirlpool"
    HMAC_STREEBOG = "HMAC-Streebog"
    ARGON2ID = "Argon2"

    @property
    def native_name(self) -> str:
        return self.value


class VcFilesystem(str, enum.Enum):
    """Filesystem written into a newly created volume."""

    NONE = "NONE"
    FAT = "FAT"
    EXFAT = "EXFAT"

    @property
    def native_name(self) -> str:
        return self.value


class VcCreationStage(enum.IntEnum):
    """Stage reported by volume-creation progress callbacks."""

    NONE = 0
    WRITING_DATA = 1
    WRITING_BACKUP_HEADER = 2
    FLUSHING = 3
    FINISHED = 4
    ERROR = 5
