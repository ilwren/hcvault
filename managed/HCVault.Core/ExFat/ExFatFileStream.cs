using HCVault.Core.Interop;

namespace HCVault.Core.ExFat;

/// <summary>
///   Random-access stream over a file in a mounted exFAT volume: seek,
///   partial reads, in-place writes and appends, without holding the whole
///   file in memory. Backed by the native FatFs bridge (f_open / f_lseek /
///   f_read / f_write / f_sync).
/// </summary>
internal sealed class ExFatFileStream : Stream
{
    private readonly IntPtr _file;
    private readonly bool _canWrite;
    private bool _open = true;

    internal ExFatFileStream(IntPtr file, VcFileMode mode)
    {
        _file = file;
        _canWrite = mode != VcFileMode.Read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => _canWrite;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return (long)NativeMethods.vc_exfat_size(_file);
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return (long)NativeMethods.vc_exfat_tell(_file);
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return 0;

        long n;
        if (offset == 0)
        {
            n = NativeMethods.vc_exfat_read(_file, buffer, (nuint)count);
        }
        else
        {
            var scratch = new byte[count];
            n = NativeMethods.vc_exfat_read(_file, scratch, (nuint)count);
            if (n > 0)
                Array.Copy(scratch, 0, buffer, offset, (int)n);
        }
        if (n < 0)
            throw Status.ToException((int)n);
        return (int)n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ThrowIfDisposed();
        if (!_canWrite)
            throw new NotSupportedException("The stream was opened for reading.");
        if (count == 0)
            return;

        long n;
        if (offset == 0)
        {
            n = NativeMethods.vc_exfat_write(_file, buffer, (nuint)count);
        }
        else
        {
            var scratch = new byte[count];
            Array.Copy(buffer, offset, scratch, 0, count);
            n = NativeMethods.vc_exfat_write(_file, scratch, (nuint)count);
        }
        if (n < 0)
            throw Status.ToException((int)n);
        if (n != count)
            throw new VcException($"Short write: {n} of {count} bytes (volume full?).");
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
            throw new IOException("Cannot seek before the beginning of the file.");

        int rc = NativeMethods.vc_exfat_seek(_file, (ulong)target);
        if (rc < 0)
            throw Status.ToException(rc);
        return target;
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        int rc = NativeMethods.vc_exfat_flush(_file);
        if (rc < 0)
            throw Status.ToException(rc);
    }

    /// <summary>Not supported by the exFAT bridge; read/write streams grow implicitly.</summary>
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_open)
        {
            _open = false;
            NativeMethods.vc_exfat_close(_file); // flushes + destroys the handle
        }
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        if (!_open)
            throw new ObjectDisposedException(nameof(ExFatFileStream));
    }
}
