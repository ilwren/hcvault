using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using HCVault.Core.Fat;
using HCVault.Core.Interop;

namespace HCVault.Core.ExFat;

/// <summary>
///   exFAT filesystem inside a VeraCrypt volume, implemented by ChaN's FatFs
///   (compiled into the native library with FF_FS_EXFAT enabled). All reads
///   and writes pass through the decrypted data area, so nothing touches the
///   host filesystem. Volumes it creates are readable by VeraCrypt itself and
///   by Windows (exFAT "super-floppy", the layout VeraCrypt uses).
/// </summary>
public sealed class ExFatVolume : IVolumeFileSystem
{
    private IntPtr _fs;
    private bool _disposed;

    private ExFatVolume(IntPtr fs) => _fs = fs;

    /// <summary>Mount the exFAT filesystem inside an open volume.</summary>
    public static ExFatVolume Mount(Volume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        HCVaultLibrary.Initialize();

        IntPtr fs = NativeMethods.vc_exfat_mount(volume.NativeHandle);
        if (fs == IntPtr.Zero)
            throw Status.ToException(NativeMethods.vc_last_status());
        return new ExFatVolume(fs);
    }

    /// <inheritdoc />
    public IReadOnlyList<FatEntry> ListDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();

        var list = new List<FatEntry>();
        IntPtr dir = NativeMethods.vc_exfat_opendir(_fs, path);
        if (dir == IntPtr.Zero)
            throw Status.ToException(NativeMethods.vc_last_status());

        try
        {
            while (true)
            {
                int r = NativeMethods.vc_exfat_readdir(dir, out NativeMethods.VcExFatEntry entry);
                if (r < 0)
                    throw Status.ToException(r);
                if (r == 0)
                    break;
                list.Add(ToFatEntry(entry));
            }
        }
        finally
        {
            NativeMethods.vc_exfat_closedir(dir);
        }
        return list;
    }

    /// <inheritdoc />
    public byte[] ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();

        IntPtr f = NativeMethods.vc_exfat_open(_fs, path, mode: 0);
        if (f == IntPtr.Zero)
            throw Status.ToException(NativeMethods.vc_last_status());

        try
        {
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                long n = NativeMethods.vc_exfat_read(f, buffer, (nuint)buffer.Length);
                if (n < 0)
                    throw Status.ToException((int)n);
                if (n == 0)
                    break;
                ms.Write(buffer, 0, (int)n);
            }
            return ms.ToArray();
        }
        finally
        {
            NativeMethods.vc_exfat_close(f);
        }
    }

    /// <inheritdoc />
    public void WriteFile(string path, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();

        IntPtr f = NativeMethods.vc_exfat_open(_fs, path, mode: 1);
        if (f == IntPtr.Zero)
            throw Status.ToException(NativeMethods.vc_last_status());

        try
        {
            if (data.Length > 0)
            {
                long n = NativeMethods.vc_exfat_write(f, data, (nuint)data.Length);
                if (n < 0)
                    throw Status.ToException((int)n);
                if ((nuint)n != (nuint)data.Length)
                    throw new VcException($"Short write: {n} of {data.Length} bytes (volume full?).");
            }
        }
        finally
        {
            NativeMethods.vc_exfat_close(f); // flushes
        }
    }

    /// <inheritdoc />
    public void CreateDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        Status.ThrowIfFailed(NativeMethods.vc_exfat_mkdir(_fs, path));
    }

    /// <inheritdoc />
    public void Delete(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        Status.ThrowIfFailed(NativeMethods.vc_exfat_delete(_fs, path));
    }

    /// <inheritdoc />
    public VolumeSpace GetSpace()
    {
        ThrowIfDisposed();
        Status.ThrowIfFailed(NativeMethods.vc_exfat_get_space(_fs, out NativeMethods.VcExFatSpace s));
        return new VolumeSpace
        {
            TotalBytes = (long)s.TotalBytes,
            FreeBytes = (long)s.FreeBytes,
            ClusterBytes = (long)s.ClusterBytes,
        };
    }

    /// <summary>Release the exFAT mount (the volume itself stays open).</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_fs != IntPtr.Zero)
            {
                NativeMethods.vc_exfat_unmount(_fs);
                _fs = IntPtr.Zero;
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static unsafe FatEntry ToFatEntry(NativeMethods.VcExFatEntry entry)
    {
        int len = 0;
        while (len < 255 && entry.Name[len] != 0)
            len++;
        var nameBytes = new byte[len];
        for (int i = 0; i < len; i++)
            nameBytes[i] = entry.Name[i];
        string name = Encoding.UTF8.GetString(nameBytes);

        return new FatEntry
        {
            Name = name,
            IsDirectory = entry.IsDirectory != 0,
            SizeBytes = (long)entry.Size,
            ModifiedUtc = FatDateTime.Decode(entry.ModifiedDate, entry.ModifiedTime),
            FirstCluster = 0, // not exposed by the exFAT bridge
        };
    }
}
