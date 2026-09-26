using System.Runtime.InteropServices;
using HCVault.Core.Interop;

namespace HCVault.Core;

/// <summary>Progress report for volume creation.</summary>
public readonly record struct CreationProgress(ulong BytesDone, ulong BytesTotal, VcCreationStage Stage)
{
    public double Fraction => BytesTotal == 0 ? 0 : (double)BytesDone / BytesTotal;
}

/// <summary>Options for creating a new (normal or hidden) VeraCrypt volume.</summary>
public sealed class VolumeCreationOptions
{
    /// <summary>Path of the volume file to create. For hidden volumes: the existing outer volume.</summary>
    public required string Path { get; init; }

    /// <summary>Volume size in bytes (normal volume: total file size; hidden: hidden area size).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Password; required unless keyfiles alone are used.</summary>
    public required SecurePassword Password { get; init; }

    /// <summary>
    ///   VeraCrypt PIM (Personal Iterations Multiplier). 0 = library default.
    ///   With HMAC PRFs the iteration count scales with the PIM; keep the value
    ///   you choose — it is needed to open the volume again.
    /// </summary>
    public int Pim { get; init; }

    /// <summary>Keyfiles whose contents are mixed into the password (apply in this order).</summary>
    public IReadOnlyList<string> KeyFiles { get; init; } = Array.Empty<string>();

    public VcCipher Cipher { get; init; } = VcCipher.Aes;

    public VcKdf Kdf { get; init; } = VcKdf.Argon2id;

    /// <summary>FAT (interoperable with VeraCrypt itself) or raw (None).</summary>
    public VcFilesystem Filesystem { get; init; } = VcFilesystem.Fat;

    /// <summary>Skip wiping the data area (recommended for fast creation; the area is still encrypted).</summary>
    public bool Quick { get; init; } = true;

    /// <summary>Create a hidden volume inside the outer volume at <see cref="Path" />.</summary>
    public bool Hidden { get; init; }

    /// <summary>Optional progress sink (invoked on the creating thread).</summary>
    public Action<CreationProgress>? Progress { get; init; }
}

public sealed partial class Volume
{
    /// <summary>
    ///   Create a volume on disk. Long-running — run on a worker thread.
    ///   For hidden volumes <paramref name="options" />.Path must reference an
    ///   existing outer volume whose password is <paramref name="options" />.Password.
    /// </summary>
    public static void Create(VolumeCreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Path);
        ArgumentNullException.ThrowIfNull(options.Password);

        HCVaultLibrary.Initialize();

        if (options.SizeBytes < 200 * 1024)
            throw new VcArgumentException("Volume size must be at least 200 KiB.");

        if (options.Hidden && options.SizeBytes < 200 * 1024)
            throw new VcArgumentException("Hidden volume size must be at least 200 KiB.");

        var keyfiles = options.KeyFiles.ToArray();

        NativeMethods.VcProgressCallback? cb = null;
        try
        {
            if (options.Progress is { } sink)
            {
                var box = sink; // keep the delegate alive for the native duration
                cb = (done, total, stage, _) => box(new CreationProgress(done, total, (VcCreationStage)stage));
            }

            int status = NativeMethods.vc_create_volume(
                options.Path,
                (ulong)options.SizeBytes,
                options.Password.Span, options.Password.Length,
                keyfiles, (nuint)keyfiles.Length,
                options.Cipher.ToNative(),
                options.Kdf.ToNative(),
                options.Filesystem switch
                {
                    VcFilesystem.Fat => "FAT",
                    VcFilesystem.ExFat => "EXFAT",
                    VcFilesystem.None => "NONE",
                    _ => throw new VcArgumentException($"Unknown filesystem {options.Filesystem}"),
                },
                options.Pim,
                options.Quick ? 1 : 0,
                options.Hidden ? 1 : 0,
                cb, IntPtr.Zero);

            Status.ThrowIfFailed(status);
        }
        finally
        {
            GC.KeepAlive(cb);
        }
    }
}
