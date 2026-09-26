namespace HCVault.Core;

/// <summary>VeraCrypt encryption algorithms (all cascade combinations included).</summary>
public enum VcCipher
{
    Aes,
    Serpent,
    Twofish,
    Camellia,
    Kuznyechik,
    AesTwofish,
    AesTwofishSerpent,
    CamelliaKuznyechik,
    CamelliaSerpent,
    KuznyechikAes,
    KuznyechikSerpentCamellia,
    KuznyechikTwofish,
    SerpentAes,
    SerpentTwofishAes,
    TwofishSerpent,
}

/// <summary>Header key-derivation functions (PRF) used to protect the volume header.</summary>
public enum VcKdf
{
    /// <summary>HMAC-SHA-512 with PBKDF2 (classic VeraCrypt default).</summary>
    HmacSha512,

    /// <summary>HMAC-SHA-256 with PBKDF2.</summary>
    HmacSha256,

    /// <summary>HMAC-BLAKE2s-256 with PBKDF2.</summary>
    HmacBlake2s256,

    /// <summary>HMAC-Whirlpool with PBKDF2.</summary>
    HmacWhirlpool,

    /// <summary>HMAC-Streebog (GOST R 34.11-2012) with PBKDF2.</summary>
    HmacStreebog,

    /// <summary>Argon2id (modern VeraCrypt default recommendation).</summary>
    Argon2id,
}

/// <summary>Filesystem written into a newly created volume.</summary>
public enum VcFilesystem
{
    /// <summary>
    ///   No filesystem — the data area stays raw; use sector-level access or format later.
    /// </summary>
    None,

    /// <summary>Built-in FAT formatter (readable by VeraCrypt itself and by <see cref="Fat.FatVolume" />).</summary>
    Fat,

    /// <summary>
    ///   exFAT via the bundled FatFs (ChaN R0.15) — large-cluster, no-BS-tail
    ///   filesystem; readable by VeraCrypt itself and by Windows/macOS/Linux.
    /// </summary>
    ExFat,
}

/// <summary>Stage reported by volume-creation progress callbacks.</summary>
public enum VcCreationStage
{
    None = 0,
    WritingData = 1,
    WritingBackupHeader = 2,
    Flushing = 3,
    Finished = 4,
    Error = 5,
}

internal static class EnumNames
{
    internal static string ToNative(this VcCipher cipher) => cipher switch
    {
        VcCipher.Aes => "AES",
        VcCipher.Serpent => "Serpent",
        VcCipher.Twofish => "Twofish",
        VcCipher.Camellia => "Camellia",
        VcCipher.Kuznyechik => "Kuznyechik",
        VcCipher.AesTwofish => "AES-Twofish",
        VcCipher.AesTwofishSerpent => "AES-Twofish-Serpent",
        VcCipher.CamelliaKuznyechik => "Camellia-Kuznyechik",
        VcCipher.CamelliaSerpent => "Camellia-Serpent",
        VcCipher.KuznyechikAes => "Kuznyechik-AES",
        VcCipher.KuznyechikSerpentCamellia => "Kuznyechik-Serpent-Camellia",
        VcCipher.KuznyechikTwofish => "Kuznyechik-Twofish",
        VcCipher.SerpentAes => "Serpent-AES",
        VcCipher.SerpentTwofishAes => "Serpent-Twofish-AES",
        VcCipher.TwofishSerpent => "Twofish-Serpent",
        _ => throw new ArgumentOutOfRangeException(nameof(cipher)),
    };

    internal static string ToNative(this VcKdf kdf) => kdf switch
    {
        VcKdf.HmacSha512 => "HMAC-SHA-512",
        VcKdf.HmacSha256 => "HMAC-SHA-256",
        VcKdf.HmacBlake2s256 => "HMAC-BLAKE2s-256",
        VcKdf.HmacWhirlpool => "HMAC-Whirlpool",
        VcKdf.HmacStreebog => "HMAC-Streebog",
        VcKdf.Argon2id => "Argon2",
        _ => throw new ArgumentOutOfRangeException(nameof(kdf)),
    };
}
