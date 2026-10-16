namespace HCVault.Core.Fat;

/// <summary>
///   Random-access stream over a file in the managed FAT driver: seek,
///   partial reads, in-place writes, zero-filled hole writes and appends,
///   without holding the whole file in memory. The cluster chain is
///   materialized lazily (the FAT itself is already resident); the directory
///   entry's size is updated as soon as a write grows the file, so an
///   interrupted stream never lengthens a file beyond what was written.
/// </summary>
internal sealed class FatFileStream : Stream
{
    private readonly FatVolume _v;
    private readonly List<uint> _parentClusters;
    private readonly bool _parentIsRoot;
    private readonly string _name;
    private readonly List<uint> _chain = new();
    private uint _first;
    private long _position;
    private long _length;
    private readonly bool _canWrite;
    private bool _open = true;

    internal FatFileStream(FatVolume volume, string path, VcFileMode mode)
    {
        _v = volume;
        (_parentClusters, _parentIsRoot, _name) = volume.ResolveParent(path);
        var entry = volume.FindEntryIn(_parentClusters, _parentIsRoot, _name);
        if (entry is { IsDirectory: true })
            throw new VcArgumentException($"'{path}' is a directory.");

        switch (mode)
        {
            case VcFileMode.Read:
                if (entry is null)
                    throw new VcArgumentException($"'{path}' not found.");
                _first = entry.FirstCluster;
                _length = entry.SizeBytes;
                _canWrite = false;
                break;

            case VcFileMode.Create:
                if (entry is not null)
                    volume.FreeChain(entry.FirstCluster);
                _canWrite = true;
                volume.WriteDirectoryEntry(_parentClusters, _parentIsRoot, _name,
                    firstCluster: 0, size: 0, isDirectory: false, overwriteName: _name);
                volume.FlushFat();
                break;

            case VcFileMode.Append:
                if (entry is null)
                    throw new VcArgumentException($"'{path}' not found.");
                _first = entry.FirstCluster;
                _length = entry.SizeBytes;
                _position = _length;
                _canWrite = true;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => _canWrite;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ThrowIfDisposed();
        if (count == 0)
            return 0;
        if (_position >= _length)
            return 0;

        long want = Math.Min(count, _length - _position);
        int cb = _v.ClusterBytes;
        long done = 0;
        while (done < want)
        {
            long idx = (_position + done) / cb;
            int inOff = (int)((_position + done) % cb);
            uint c = ChainAt(idx);
            int take = (int)Math.Min(cb - inOff, want - done);
            var cluster = _v.ReadCluster(c);
            Array.Copy(cluster, inOff, buffer, offset + done, take);
            done += take;
        }
        _position += done;
        return (int)done;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ThrowIfDisposed();
        if (!_canWrite)
            throw new NotSupportedException("The stream was opened for reading.");
        if (count == 0)
            return;

        // a write past the current end leaves a zero-filled hole
        if (_position > _length)
            FillZeros(_length, _position - _length);

        int cb = _v.ClusterBytes;
        long end = _position + count;
        long done = 0;
        while (done < count)
        {
            long abs = _position + done;
            long idx = abs / cb;
            int inOff = (int)(abs % cb);
            int take = (int)Math.Min(cb - inOff, count - done);
            uint c = ChainAt(idx);
            if (take == cb)
            {
                var full = new byte[cb];
                Array.Copy(buffer, offset + done, full, 0, cb);
                _v.WriteCluster(c, full);
            }
            else
            {
                var cluster = _v.ReadCluster(c);
                Array.Copy(buffer, offset + done, cluster, inOff, take);
                _v.WriteCluster(c, cluster);
            }
            done += take;
        }
        _position = end;

        if (end > _length)
        {
            _length = end;
            _v.WriteDirectoryEntry(_parentClusters, _parentIsRoot, _name,
                _first, (uint)_length, isDirectory: false, overwriteName: _name);
            _v.FlushFat();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
            throw new IOException("Cannot seek before the beginning of the file.");
        _position = target;
        return target;
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        _v.FlushFat(); // data clusters are written through; the FAT mirrors flush here
    }

    /// <summary>Not supported by the FAT driver; use Create mode to truncate.</summary>
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_open)
        {
            _open = false;
            if (_canWrite)
                _v.FlushFat();
        }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------- internals

    /// <summary>
    ///   The cluster holding byte <paramref name="index" />'s cluster slot,
    ///   extending (and zero-filling) the chain as needed.
    /// </summary>
    private uint ChainAt(long index)
    {
        if (_first == 0)
        {
            // first allocation covers everything up to index at once
            _first = _v.AllocateChain((uint)(index + 1));
            uint c = _first;
            for (long i = 0; i <= index; i++)
            {
                _chain.Add(c);
                _v.WriteCluster(c, new byte[_v.ClusterBytes]); // fresh clusters are zeroed
                c = _v.Next(c);
            }
            return _chain[(int)index];
        }

        if (_chain.Count == 0)
            _chain.Add(_first);

        while (_chain.Count <= index)
        {
            uint last = _chain[^1];
            uint next = _v.Next(last);
            if (next < 2 || next >= _v.BadOrEof)
            {
                _v.ExtendChain(last, 1);
                next = _v.Next(last);
                _v.WriteCluster(next, new byte[_v.ClusterBytes]); // zero the fresh cluster
            }
            _chain.Add(next);
        }
        return _chain[(int)index];
    }

    private void FillZeros(long from, long count)
    {
        int cb = _v.ClusterBytes;
        long done = 0;
        while (done < count)
        {
            long abs = from + done;
            long idx = abs / cb;
            int inOff = (int)(abs % cb);
            int take = (int)Math.Min(cb - inOff, count - done);
            uint c = ChainAt(idx);
            if (inOff == 0 && take == cb)
            {
                _v.WriteCluster(c, new byte[cb]);
            }
            else
            {
                var cluster = _v.ReadCluster(c);
                Array.Clear(cluster, inOff, take);
                _v.WriteCluster(c, cluster);
            }
            done += take;
        }
    }

    private void ThrowIfDisposed()
    {
        if (!_open)
            throw new ObjectDisposedException(nameof(FatFileStream));
    }
}
