using System.Runtime.InteropServices;
using HCVault.Core.Interop;

namespace HCVault.Core;

/// <summary>Options for opening an existing volume.</summary>
public sealed class VolumeOpenOptions
{
    public required string Path { get; init; }

    public required SecurePassword Password { get; init; }

    /// <summary>PIM used when the volume was created (0 = default).</summary>
    public int Pim { get; init; }

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

    public bool ReadOnly { get; init; }

    /// <summary>Try the embedded backup header first (damaged primary header).</summary>
    public bool UseBackupHeader { get; init; }
}

/// <summary>
///   An open VeraCrypt volume providing sector-level encrypted access.
///   Instances are NOT thread-safe; open one per thread or synchronize externally.
/// </summary>
public sealed partial class Volume : IDisposable
{
    private readonly VolumeHandle _handle;

    private Volume(VolumeHandle handle) => _handle = handle;

    private IntPtr H => _handle.IsClosed
        ? throw new ObjectDisposedException(nameof(Volume))
        : _handle.DangerousGetHandle();

    /// <summary>Native vc_volume handle — for the exFAT bridge (same assembly only).</summary>
    internal IntPtr NativeHandle => H;

    /// <summary>
    ///   <see cref="Open"/> as a thread-pool task (the header-key derivation
    ///   takes seconds with Argon2). The native call is not cancellable - the
    ///   token only prevents a run that has not started yet. The returned
    ///   volume follows the usual rule: one thread at a time.
    /// </summary>
    public static Task<Volume> OpenAsync(VolumeOpenOptions options, CancellationToken cancellationToken = default)
        => Task.Run(() => Open(options), cancellationToken);

    /// <summary>Open a volume with password + optional keyfiles + optional PIM.</summary>
    public static Volume Open(VolumeOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Path);
        ArgumentNullException.ThrowIfNull(options.Password);

        HCVaultLibrary.Initialize();

        var keyfiles = options.KeyFiles.ToArray();
        SecurePassword? mixed = null;
        if (options.KeyFileStreams is { Count: > 0 })
        {
            mixed = KeyfileMixer.Apply(options.Password, options.KeyFiles, options.KeyFileStreams);
            keyfiles = Array.Empty<string>();
        }

        try
        {
            SecurePassword password = mixed ?? options.Password;
            IntPtr handle = NativeMethods.vc_open_volume(
                options.Path,
                password.Span, password.Length,
                keyfiles, (nuint)keyfiles.Length,
                options.Pim,
                options.ReadOnly ? 1 : 0,
                options.UseBackupHeader ? 1 : 0);

            if (handle == IntPtr.Zero)
                throw Status.ToException(NativeMethods.vc_last_status());

            return new Volume(new VolumeHandle(handle));
        }
        finally
        {
            mixed?.Dispose();
        }
    }

    // ------------------------------------------------------------- properties

    /// <summary>Usable data-area size in bytes (the decrypted view).</summary>
    public long DataSize => (long)NativeMethods.vc_get_data_size(H);

    /// <summary>Sector size in bytes (typically 512 for file-hosted volumes).</summary>
    public int SectorSize => (int)NativeMethods.vc_get_sector_size(H);

    /// <summary>Cipher actually protecting the volume.</summary>
    public string CipherName =>
        NativeLoader.StringFromIntPtr(NativeMethods.vc_get_cipher_used(H));

    /// <summary>Header KDF actually protecting the volume.</summary>
    public string KdfName =>
        NativeLoader.StringFromIntPtr(NativeMethods.vc_get_kdf_used(H));

    /// <summary>PIM the volume was opened with.</summary>
    public int Pim => NativeMethods.vc_get_pim(H);

    /// <summary>True if this handle opened the hidden volume inside a container.</summary>
    public bool IsHidden => NativeMethods.vc_is_hidden(H) != 0;

    public bool IsOpen => !_handle.IsClosed;

    // ------------------------------------------------------------------- I/O

    /// <summary>
    ///   Read <paramref name="length" /> sector-aligned bytes at <paramref name="offset" />
    ///   (relative to the data area) into <paramref name="buffer" />.
    /// </summary>
    public void ReadSectors(byte[] buffer, ulong offset, nuint length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        long done = NativeMethods.vc_read_sectors(H, buffer, offset, length);
        if (done < 0)
            throw Status.ToException((int)done);
        if ((nuint)done != length)
            throw new VcException($"Short read: got {done} of {length} bytes.");
    }

    /// <summary>Write <paramref name="length" /> sector-aligned bytes at <paramref name="offset" />.</summary>
    public void WriteSectors(byte[] buffer, ulong offset, nuint length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        long done = NativeMethods.vc_write_sectors(H, buffer, offset, length);
        if (done < 0)
            throw Status.ToException((int)done);
        if ((nuint)done != length)
            throw new VcException($"Short write: got {done} of {length} bytes.");
    }

    /// <summary>A <see cref="Stream" /> over the decrypted data area (handles unaligned access).</summary>
    public Stream OpenStream(bool writable = true) => new SectorStream(this, writable);

    public void Dispose() => _handle.Dispose();

    internal void ReadSectorsUnchecked(byte[] buffer, ulong offset, nuint length)
        => ReadSectors(buffer, offset, length);

    internal void WriteSectorsUnchecked(byte[] buffer, ulong offset, nuint length)
        => WriteSectors(buffer, offset, length);

    private sealed class VolumeHandle : SafeHandle
    {
        public VolumeHandle(IntPtr handle) : base(IntPtr.Zero, true) => SetHandle(handle);

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            NativeMethods.vc_close_volume(handle);
            return true;
        }
    }
}
