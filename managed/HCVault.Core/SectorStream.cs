namespace HCVault.Core;

/// <summary>
///   A buffered <see cref="Stream" /> over a <see cref="Volume" />'s data area.
///   The underlying native API is sector-aligned; this stream transparently
///   rounds accesses up to sector boundaries and caches the current sector.
/// </summary>
internal sealed class SectorStream : Stream
{
    private readonly Volume _volume;
    private readonly bool _writable;
    private readonly int _sectorSize;
    private readonly byte[] _sector;
    private readonly long _length;

    private bool _sectorLoaded;
    private bool _sectorDirty;
    private long _sectorIndex = -1;
    private long _position;

    public SectorStream(Volume volume, bool writable)
    {
        _volume = volume ?? throw new ArgumentNullException(nameof(volume));
        _writable = writable;
        _sectorSize = volume.SectorSize;
        _sector = new byte[_sectorSize];
        _length = volume.DataSize;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => _writable;
    public override long Length => _length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (_position + count > _length)
            count = (int)Math.Max(0, _length - _position);
        if (count == 0)
            return 0;

        int copied = 0;
        while (copied < count)
        {
            EnsureSectorLoaded(_position / _sectorSize);
            int within = (int)(_position % _sectorSize);
            int take = Math.Min(_sectorSize - within, count - copied);
            Array.Copy(_sector, within, buffer, offset + copied, take);
            _position += take;
            copied += take;
        }
        return copied;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!_writable)
            throw new NotSupportedException("Stream opened read-only.");
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (_position + count > _length)
            throw new IOException("Write beyond the end of the volume data area.");
        if (count == 0)
            return;

        int copied = 0;
        while (copied < count)
        {
            long index = _position / _sectorSize;
            EnsureSectorLoaded(index);
            int within = (int)(_position % _sectorSize);
            int take = Math.Min(_sectorSize - within, count - copied);
            Array.Copy(buffer, offset + copied, _sector, within, take);
            _sectorDirty = true;
            _position += take;
            copied += take;
        }
    }

    public override void Flush()
    {
        if (_sectorDirty)
        {
            _volume.WriteSectors(_sector, (ulong)(_sectorIndex * _sectorSize), (nuint)_sectorSize);
            _sectorDirty = false;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => origin switch
    {
        SeekOrigin.Begin => _position = offset,
        SeekOrigin.Current => _position += offset,
        SeekOrigin.End => _position = _length + offset,
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { Flush(); } catch { /* best effort on close */ }
        }
        base.Dispose(disposing);
    }

    private void EnsureSectorLoaded(long index)
    {
        if (_sectorLoaded && _sectorIndex == index)
            return;

        if (_sectorDirty)
            Flush();

        _volume.ReadSectors(_sector, (ulong)(index * _sectorSize), (nuint)_sectorSize);
        _sectorIndex = index;
        _sectorLoaded = true;
    }
}
