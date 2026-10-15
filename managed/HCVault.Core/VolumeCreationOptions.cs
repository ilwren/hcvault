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

    /// <summary>
    ///   Keyfile contents supplied directly (streams are read, not owned).
    ///   Applied after the <see cref="KeyFiles" /> paths; only the first
    ///   1 MiB per source is mixed in, exactly like VeraCrypt. When this
    ///   list is non-empty, the mixing happens in this library and the
    ///   paths in <see cref="KeyFiles" /> must be regular files (no
    ///   directory expansion).
    /// </summary>
    public IReadOnlyList<Stream>? KeyFileStreams { get; init; }

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

        // Size validation with the exact native minimums, so a bad size
        // fails here with a readable message instead of deep inside the
        // VeraCrypt core (which reports only a source position).
        if (options.Hidden)
        {
            if (options.SizeBytes < 200 * 1024)
                throw new VcArgumentException("Hidden volume size must be at least 200 KiB.");
        }
        else
        {
            const long headerBytes = 2 * 128 * 1024;   // V2 layout: header + backup header region
            long minData = options.Filesystem switch
            {
                VcFilesystem.None => 1,                          // raw: any data area
                VcFilesystem.Fat => 36_864,                      // TC_MIN_FAT_FS_SIZE (9 x 4096)
                VcFilesystem.ExFat => 2L * 1024 * 1024,          // f_mkfs: 4096 sectors
                _ => throw new VcArgumentException($"Unknown filesystem {options.Filesystem}"),
            };
            long minSize = headerBytes + minData;
            if (options.SizeBytes < minSize)
                throw new VcArgumentException(
                    $"{options.Filesystem} volumes need at least {minSize} bytes " +
                    $"({minData:N0} data area + {headerBytes:N0} of volume headers); got {options.SizeBytes}. " +
                    $"Use Filesystem.None for smaller volumes, or increase SizeBytes.");

            // the FAT formatter is limited to 0x100000000 sectors of 512 bytes
            if (options.Filesystem == VcFilesystem.Fat
                && options.SizeBytes > headerBytes + 2_199_023_255_552L)
                throw new VcArgumentException(
                    "FAT volumes cannot exceed a 2 TiB data area; use ExFat or Filesystem.None.");
        }

        var keyfiles = options.KeyFiles.ToArray();
        SecurePassword? mixed = null;
        if (options.KeyFileStreams is { Count: > 0 })
        {
            mixed = KeyfileMixer.Apply(options.Password, options.KeyFiles, options.KeyFileStreams);
            keyfiles = Array.Empty<string>();
        }

        NativeMethods.VcProgressCallback? cb = null;
        try
        {
            if (options.Progress is { } sink)
            {
                var box = sink; // keep the delegate alive for the native duration
                cb = (done, total, stage, _) => box(new CreationProgress(done, total, (VcCreationStage)stage));
            }

            SecurePassword password = mixed ?? options.Password;
            int status = NativeMethods.vc_create_volume(
                options.Path,
                (ulong)options.SizeBytes,
                password.Span, password.Length,
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
            mixed?.Dispose();
            GC.KeepAlive(cb);
        }
    }

    /// <summary>
    ///   <see cref="Create"/> as a thread-pool task, so UI threads stay
    ///   responsive during the header-key derivation (Argon2) and the data
    ///   write. The native call itself is not cancellable - the token only
    ///   prevents a run that has not started yet. The progress callback fires
    ///   on the worker thread; marshal to your own context as needed.
    /// </summary>
    public static Task CreateAsync(VolumeCreationOptions options, CancellationToken cancellationToken = default)
        => Task.Run(() => Create(options), cancellationToken);
}
