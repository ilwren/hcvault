using System.Runtime.InteropServices;
using HCVault.Core.Interop;

namespace HCVault.Core;

/// <summary>
///   Library entry point: initialization, shutdown and algorithm enumeration.
///   The first call to any HCVault.Core API initializes the native library
///   automatically; explicit <see cref="Initialize" /> is optional.
/// </summary>
public static class HCVaultLibrary
{
    private static int _initialized;

    /// <summary>
    ///   Runs the official VeraCrypt cipher/hash/XTS self-tests and starts the
    ///   RNG pool and the key-derivation thread pool. Thread-safe and idempotent.
    /// </summary>
    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 1)
            return;

        NativeLoader.EnsureLoaded();

        int version = NativeMethods.vc_api_version();
        if (version != NativeMethods.ApiVersion)
            throw new VcUnsupportedException(
                $"hcvault-core API version mismatch: expected {NativeMethods.ApiVersion}, native reports {version}. " +
                "Update the native library or the managed wrapper.");

        Status.ThrowIfFailed(NativeMethods.vc_init());
    }

    /// <summary>Stops the native background threads. Optional; a safety net runs at exit.</summary>
    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 1)
            NativeMethods.vc_shutdown();
    }

    /// <summary>All cipher combinations supported by the native core.</summary>
    public static IReadOnlyList<VcCipher> SupportedCiphers { get; } =
        Enum.GetValues<VcCipher>().ToList().AsReadOnly();

    /// <summary>All header KDFs supported by the native core.</summary>
    public static IReadOnlyList<VcKdf> SupportedKdfs { get; } =
        Enum.GetValues<VcKdf>().ToList().AsReadOnly();

    /// <summary>
    ///   Verifies that the managed enum tables match the native algorithm list
    ///   (a guard against version skew between wrapper and core).
    /// </summary>
    public static void VerifyAlgorithmTables()
    {
        Initialize();

        int cipherCount = NativeMethods.vc_get_cipher_count();
        var nativeCiphers = new HashSet<string>();
        for (int i = 0; i < cipherCount; i++)
            nativeCiphers.Add(NativeLoader.StringFromIntPtr(NativeMethods.vc_get_cipher_name(i)));

        foreach (var cipher in Enum.GetValues<VcCipher>())
            if (!nativeCiphers.Contains(cipher.ToNative()))
                throw new VcUnsupportedException($"Cipher '{cipher}' is not available in the native library.");

        int kdfCount = NativeMethods.vc_get_kdf_count();
        var nativeKdfs = new HashSet<string>();
        for (int i = 0; i < kdfCount; i++)
            nativeKdfs.Add(NativeLoader.StringFromIntPtr(NativeMethods.vc_get_kdf_name(i)));

        foreach (var kdf in Enum.GetValues<VcKdf>())
            if (!nativeKdfs.Contains(kdf.ToNative()))
                throw new VcUnsupportedException($"KDF '{kdf}' is not available in the native library.");
    }
}
