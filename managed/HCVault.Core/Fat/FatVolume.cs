using System.Text;

namespace HCVault.Core.Fat;

/// <summary>
///   A minimal FAT12/FAT16/FAT32 driver over a <see cref="Volume" /> data area.
///   Supports: directory listing (LFN), file read, file create/overwrite,
///   file delete, directory create. Volumes created with
///   <see cref="VcFilesystem.Fat" /> are readable by VeraCrypt itself and
///   vice versa.
/// </summary>
public sealed class FatVolume : IVolumeFileSystem
{
    private readonly Volume _volume;
    private readonly byte[] _sector;
    private readonly uint[] _fat;          // decoded FAT (first copy; mirrors written too)
    private readonly int _bytesPerSector;
    private readonly int _sectorsPerCluster;
    private readonly uint _clusterCount;
    private readonly uint _firstDataSector;
    private readonly uint _fatStartSector; // sector of FAT copy 0
    private readonly uint _fatSectors;
    private readonly uint _rootDirSectors; // FAT12/16 only; 0 for FAT32
    private readonly uint _rootCluster;    // FAT32 only
    private readonly bool _isFat32;
    private readonly Stream _stream;

    private FatVolume(Volume volume, Stream stream, int bytesPerSector, int sectorsPerCluster,
        uint clusterCount, uint firstDataSector, uint fatStartSector, uint fatSectors,
        uint rootDirSectors, uint rootCluster, bool isFat32, uint[] fat)
    {
        _volume = volume;
        _stream = stream;
        _sector = new byte[bytesPerSector];
        _bytesPerSector = bytesPerSector;
        _sectorsPerCluster = sectorsPerCluster;
        _clusterCount = clusterCount;
        _firstDataSector = firstDataSector;
        _fatStartSector = fatStartSector;
        _fatSectors = fatSectors;
        _rootDirSectors = rootDirSectors;
        _rootCluster = rootCluster;
        _isFat32 = isFat32;
        _fat = fat;
    }

    /// <summary>Mount the FAT filesystem inside an open volume.</summary>
    public static FatVolume Mount(Volume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var stream = volume.OpenStream(writable: true);
        var boot = new byte[512];
        stream.ReadExactly(boot);

        // A FAT boot sector starts with a JMP or NOP instruction.
        if (boot[0] is not (0xEB or 0xE9 or 0x69))
            throw new FatFormatException("The volume does not contain a FAT filesystem (create it with VcFilesystem.Fat).");

        int bps = (int)ReadU16(boot, 11);
        int spc = boot[13];
        uint reserved = ReadU16(boot, 14);
        int fatCount = Math.Max(1, (int)boot[16]);
        uint rootEntries = ReadU16(boot, 17);
        uint total16 = ReadU16(boot, 19);
        uint fatSize16 = ReadU16(boot, 22);
        uint total32 = ReadU32(boot, 32);

        if (bps is not (512 or 1024 or 2048 or 4096) || spc == 0 || reserved == 0 || fatCount == 0)
            throw new FatFormatException("Invalid FAT boot sector.");

        uint totalSectors = total16 != 0 ? total16 : total32;
        if (totalSectors == 0)
            throw new FatFormatException("Invalid FAT total sector count.");

        uint rootDirSectors = (uint)((rootEntries * 32 + bps - 1) / bps);
        uint fatSize = fatSize16 != 0 ? fatSize16 : ReadU32(boot, 36);
        uint firstDataSector = reserved + (uint)fatCount * fatSize + rootDirSectors;
        if (firstDataSector >= totalSectors)
            throw new FatFormatException("Invalid FAT layout.");

        uint dataSectors = totalSectors - firstDataSector;
        uint clusterCount = dataSectors / (uint)spc;
        bool isFat32 = clusterCount >= 65525;

        uint fatStart = reserved;
        uint fatSectors = fatSize;

        // load FAT copy 0
        var fatBytes = new byte[fatSectors * bps];
        stream.Seek(fatStart * bps, SeekOrigin.Begin);
        stream.ReadExactly(fatBytes);

        uint[] fat;
        if (isFat32)
        {
            fat = new uint[fatBytes.Length / 4];
            for (int i = 0; i < fat.Length; i++)
                fat[i] = ReadU32(fatBytes, i * 4) & 0x0FFFFFFFu;
        }
        else
        {
            fat = new uint[fatBytes.Length * 2 / 3]; // FAT12 entries
            for (uint i = 0; i < fat.Length; i++)
                fat[i] = ReadFat12(fatBytes, i);
        }

        uint rootCluster = isFat32 ? (ReadU32(boot, 44) == 0 ? 2 : ReadU32(boot, 44)) : 0;

        return new FatVolume(volume, stream, bps, spc, clusterCount, firstDataSector,
            fatStart, fatSectors, rootDirSectors, rootCluster, isFat32, fat);
    }

    // ------------------------------------------------------------ public API

    /// <summary>List the entries of a directory ("\" or "/foo/bar" style paths).</summary>
    public IReadOnlyList<FatEntry> ListDirectory(string path)
    {
        var (dirClusters, isRoot) = ResolveDirectory(path);
        var entries = new List<FatEntry>();
        ReadDirEntries(dirClusters, isRoot, entries);
        return entries;
    }

    /// <summary>Read a whole file into memory.</summary>
    public byte[] ReadFile(string path)
    {
        var entry = FindEntry(path, requireFile: true) ?? throw new VcArgumentException($"'{path}' not found.");
        using var s = OpenFile(entry);
        var data = new byte[entry.SizeBytes];
        s.ReadExactly(data);
        return data;
    }

    /// <summary>Create or overwrite a file. Parent directories must exist.</summary>
    public void WriteFile(string path, ReadOnlySpan<byte> data)
    {
        var (parentClusters, parentIsRoot, fileName) = ResolveParent(path);

        // existing entry? free its chain first
        var existing = FindEntryIn(parentClusters, parentIsRoot, fileName);
        if (existing is { IsDirectory: false })
            FreeChain(existing.FirstCluster);

        uint first = data.Length == 0 ? 0 : AllocateChain((uint)((data.Length + ClusterBytes - 1) / ClusterBytes));

        // write data
        if (data.Length > 0)
        {
            long written = 0;
            uint cluster = first;
            int clusterBytes = ClusterBytes;
            var clusterBuf = new byte[clusterBytes];
            while (written < data.Length && cluster >= 2 && cluster < BadOrEof)
            {
                int take = (int)Math.Min(clusterBytes, data.Length - written);
                data.Slice((int)written, take).CopyTo(clusterBuf);
                if (take < clusterBytes)
                    Array.Clear(clusterBuf, take, clusterBytes - take);
                WriteCluster(cluster, clusterBuf);
                written += take;
                cluster = Next(cluster);
            }
        }

        WriteDirectoryEntry(parentClusters, parentIsRoot, fileName,
            firstCluster: first, size: (uint)data.Length, isDirectory: false, overwriteName: fileName);
        FlushFat();
    }

    /// <summary>Delete a file or an empty directory.</summary>
    public void Delete(string path)
    {
        var (parentClusters, parentIsRoot, fileName) = ResolveParent(path);
        var entry = FindEntryIn(parentClusters, parentIsRoot, fileName)
            ?? throw new VcArgumentException($"'{path}' not found.");

        if (entry.IsDirectory)
        {
            var children = new List<FatEntry>();
            ReadDirEntries(ClusterList(entry.FirstCluster), false, children);
            if (children.Any(c => c.Name is not ("." or "..")))
                throw new VcArgumentException($"Directory '{path}' is not empty.");
        }

        FreeChain(entry.FirstCluster);
        MarkEntriesDeleted(parentClusters, parentIsRoot, fileName);
        FlushFat();
    }

    /// <summary>Create a directory.</summary>
    public void CreateDirectory(string path)
    {
        var (parentClusters, parentIsRoot, fileName) = ResolveParent(path);
        if (FindEntryIn(parentClusters, parentIsRoot, fileName) is not null)
            throw new VcArgumentException($"'{path}' already exists.");

        uint cluster = AllocateChain(1);
        var zero = new byte[ClusterBytes];
        WriteCluster(cluster, zero);

        // "." and ".." entries (a full cluster, zero-filled)
        var dotEntries = new byte[ClusterBytes];
        WriteRawEntry(dotEntries, 0, ".          ", 0x10, cluster, 0);
        WriteRawEntry(dotEntries, 32, "..         ", 0x10, parentIsRoot ? 0 : FirstClusterOf(parentClusters), 0);
        WriteCluster(cluster, dotEntries);

        WriteDirectoryEntry(parentClusters, parentIsRoot, fileName,
            firstCluster: cluster, size: 0, isDirectory: true, overwriteName: fileName);
        FlushFat();
    }

    /// <summary>
    ///   Space usage computed live from the in-memory FAT: clusters 2..
    ///   <c>clusterCount+1</c> are the data clusters; an entry of 0 means free.
    /// </summary>
    public VolumeSpace GetSpace()
    {
        long clusterBytes = ClusterBytes;
        long clusters = _clusterCount;
        if (_fat.Length < clusters + 2)
            clusters = _fat.Length - 2;  // truncated FAT image (defensive)

        long free = 0;
        for (int i = 2; i < 2 + clusters; i++)
            if (_fat[i] == 0)
                free++;

        return new VolumeSpace
        {
            TotalBytes = clusters * clusterBytes,
            FreeBytes = free * clusterBytes,
            ClusterBytes = clusterBytes,
        };
    }

    public void Dispose() => _stream.Dispose();

    // -------------------------------------------------------------- internals

    private int ClusterBytes => _bytesPerSector * _sectorsPerCluster;

    private static uint ReadU16(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8));
    private static uint ReadU32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private static uint ReadFat12(byte[] b, uint index)
    {
        uint offset = (index * 3) / 2;
        if (offset + 1 >= (uint)b.Length)
            return 0xFFF;
        return (index & 1) == 0
            ? (uint)(b[offset] | ((b[offset + 1] & 0x0F) << 8))
            : (uint)((b[offset] >> 4) | (b[offset + 1] << 4));
    }

    private void WriteFat12(byte[] b, uint index, uint value)
    {
        uint offset = (index * 3) / 2;
        if (offset + 1 >= (uint)b.Length)
            return;
        if ((index & 1) == 0)
        {
            b[offset] = (byte)(value & 0xFF);
            b[offset + 1] = (byte)(((uint)b[offset + 1] & 0xF0u) | ((value >> 8) & 0x0Fu));
        }
        else
        {
            b[offset] = (byte)(((uint)b[offset] & 0x0Fu) | ((value << 4) & 0xF0u));
            b[offset + 1] = (byte)((value >> 4) & 0xFF);
        }
    }

    private uint EofMarker => _isFat32 ? 0x0FFFFFF8u : _clusterCount < 4085 ? 0xFF8u : 0xFFF8u;
    private uint BadOrEof => EofMarker;

    private uint Next(uint cluster) => _fat[cluster];

    private bool IsEof(uint v) => v >= EofMarker || v is 0;

    private uint ClusterToSector(uint cluster)
        => _firstDataSector + (uint)((cluster - 2) * _sectorsPerCluster);

    private long ClusterToOffset(uint cluster) => ClusterToSector(cluster) * _bytesPerSector;

    private byte[] ReadCluster(uint cluster)
    {
        var buf = new byte[ClusterBytes];
        _stream.Seek(ClusterToOffset(cluster), SeekOrigin.Begin);
        _stream.ReadExactly(buf);
        return buf;
    }

    private void WriteCluster(uint cluster, byte[] data)
    {
        _stream.Seek(ClusterToOffset(cluster), SeekOrigin.Begin);
        _stream.Write(data, 0, ClusterBytes);
    }

    private List<uint> ClusterList(uint first)
    {
        var list = new List<uint>();
        uint c = first;
        int guard = 0;
        while (c >= 2 && c < EofMarker && guard++ < (int)_clusterCount + 8)
        {
            list.Add(c);
            c = Next(c);
            if (IsEof(c))
                break;
        }
        return list;
    }

    private uint FirstClusterOf(List<uint> clusters) => clusters.Count > 0 ? clusters[0] : 0;

    // ----------------------------------------------------------- FAT writing

    private uint AllocateChain(uint count)
    {
        if (count == 0)
            return 0;

        var found = new List<uint>();
        for (uint c = 2; c < _clusterCount + 2 && found.Count < count; c++)
            if (c < _fat.Length && _fat[c] == 0)
                found.Add(c);

        if (found.Count < count)
            throw new IOException("Volume is full.");

        for (int i = 0; i < found.Count - 1; i++)
            _fat[found[i]] = found[i + 1];
        _fat[found[^1]] = EofMarker;
        return found[0];
    }

    private void FreeChain(uint first)
    {
        uint c = first;
        int guard = 0;
        while (c >= 2 && c < EofMarker && c < _fat.Length && guard++ < (int)_clusterCount + 8)
        {
            uint next = _fat[c];
            _fat[c] = 0;
            if (IsEof(next))
                break;
            c = next;
        }
    }

    private void FlushFat()
    {
        int fatBytes = (int)(_fatSectors * _bytesPerSector);
        var copy = new byte[fatBytes];
        if (_isFat32)
        {
            for (int i = 0; i < _fat.Length && i * 4 + 4 <= fatBytes; i++)
                WriteU32(copy, i * 4, _fat[i]);
        }
        else
        {
            for (uint i = 0; i < _fat.Length; i++)
                WriteFat12(copy, i, _fat[i]);
        }

        for (int f = 0; f < 2; f++) // both FAT copies
        {
            _stream.Seek((long)(_fatStartSector + f * _fatSectors) * _bytesPerSector, SeekOrigin.Begin);
            _stream.Write(copy, 0, fatBytes);
        }
    }

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    // ---------------------------------------------------------- directories

    private (List<uint> clusters, bool isRoot) ResolveDirectory(string path)
    {
        var parts = SplitPath(path);
        if (parts.Count == 0)
            return (RootClusters(), true);

        var clusters = RootClusters();
        bool atRoot = true;
        foreach (var part in parts)
        {
            var entry = FindEntryIn(clusters, isRoot: atRoot, part)
                ?? throw new VcArgumentException($"Directory '{part}' not found.");
            if (!entry.IsDirectory)
                throw new VcArgumentException($"'{part}' is not a directory.");
            clusters = ClusterList(entry.FirstCluster);
            atRoot = false;
        }
        return (clusters, parts.Count == 0);
    }

    private (List<uint> clusters, bool isRoot, string name) ResolveParent(string path)
    {
        var parts = SplitPath(path);
        if (parts.Count == 0)
            throw new VcArgumentException($"'{path}' is not a valid file path.");

        string name = parts[^1];
        var parentParts = parts.Take(parts.Count - 1).ToList();
        if (parentParts.Count == 0)
            return (RootClusters(), true, name);

        var (clusters, _) = ResolveDirectory(string.Join("\\", parentParts));
        return (clusters, false, name);
    }

    private static readonly char[] PathSeparators = { '\\', '/' };

    private static List<string> SplitPath(string path) =>
        path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private List<uint> RootClusters() => _isFat32 ? ClusterList(_rootCluster) : new List<uint>(); // FAT12/16: empty = root region

    private byte[] ReadDirRegion(List<uint> clusters, bool isRoot)
    {
        if (isRoot && !_isFat32)
        {
            long offset = (_fatStartSector + 2 * _fatSectors) * _bytesPerSector; // reserved + 2 FATs
            var buf = new byte[(int)(_rootDirSectors * _bytesPerSector)];
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.ReadExactly(buf);
            return buf;
        }

        int cb = ClusterBytes;
        var result = new byte[clusters.Count * cb];
        for (int i = 0; i < clusters.Count; i++)
            Array.Copy(ReadCluster(clusters[i]), 0, result, i * cb, cb);
        return result;
    }

    private void WriteDirRegion(List<uint> clusters, bool isRoot, byte[] data)
    {
        if (isRoot && !_isFat32)
        {
            long offset = (_fatStartSector + 2 * _fatSectors) * _bytesPerSector;
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.Write(data, 0, data.Length);
            return;
        }

        int cb = ClusterBytes;
        for (int i = 0; i < clusters.Count; i++)
        {
            var piece = new byte[cb];
            Array.Copy(data, i * cb, piece, 0, Math.Min(cb, data.Length - i * cb));
            WriteCluster(clusters[i], piece);
        }
    }

    private void ReadDirEntries(List<uint> clusters, bool isRoot, List<FatEntry> output)
    {
        var raw = ReadDirRegion(clusters, isRoot);
        CollectEntries(raw, output, includeDots: false);
    }

    private static void CollectEntries(byte[] raw, List<FatEntry> output, bool includeDots)
    {
        var lfnBuffer = new StringBuilder();
        byte lfnChecksum = 0;
        int lfnExpected = -1;

        for (int off = 0; off + 32 <= raw.Length; off += 32)
        {
            byte first = raw[off];
            if (first == 0x00)
                break; // end of directory
            if (first == 0xE5)
            {
                lfnBuffer.Clear();
                lfnExpected = -1;
                continue;
            }

            byte attr = raw[off + 11];

            if (attr == 0x0F) // LFN entry
            {
                int seq = first & 0x3F;
                bool isLast = (first & 0x40) != 0;
                byte checksum = raw[off + 13];

                if (isLast)
                {
                    lfnBuffer.Clear();
                    lfnExpected = seq;
                    lfnChecksum = checksum;
                }

                if (lfnExpected == seq && checksum == lfnChecksum)
                {
                    var piece = new char[13];
                    piece[0] = DecodeChar(raw, off + 1);
                    piece[1] = DecodeChar(raw, off + 3);
                    piece[2] = DecodeChar(raw, off + 5);
                    piece[3] = DecodeChar(raw, off + 7);
                    piece[4] = DecodeChar(raw, off + 9);
                    piece[5] = DecodeChar(raw, off + 14);
                    piece[6] = DecodeChar(raw, off + 16);
                    piece[7] = DecodeChar(raw, off + 18);
                    piece[8] = DecodeChar(raw, off + 20);
                    piece[9] = DecodeChar(raw, off + 22);
                    piece[10] = DecodeChar(raw, off + 24);
                    piece[11] = DecodeChar(raw, off + 28);
                    piece[12] = DecodeChar(raw, off + 30);
                    lfnBuffer.Insert(0, new string(piece).TrimEnd('\0', '\uFFFF'));
                    lfnExpected = seq - 1;
                }
                continue;
            }

            if ((attr & 0x08) != 0) // volume label
                continue;

            string shortName = DecodeShortName(raw, off);
            if (!includeDots && (shortName == "." || shortName == ".."))
                continue;

            // Windows fastfat semantics: honor the long name only when its
            // checksum matches the 11 on-disk bytes of the 8.3 entry; otherwise
            // the LFN entries are orphans and the 8.3 alias is displayed.
            string name = shortName;
            if (lfnBuffer.Length > 0 && FatShortName.Checksum(shortName) == lfnChecksum)
                name = lfnBuffer.ToString();
            lfnBuffer.Clear();
            lfnExpected = -1;

            uint cluster = ReadU16(raw, off + 26) | (ReadU16(raw, off + 20) << 16);


            output.Add(new FatEntry
            {
                Name = name,
                IsDirectory = (attr & 0x10) != 0,
                SizeBytes = ReadU32(raw, off + 28),
                ModifiedUtc = FatDateTime.Decode((ushort)ReadU16(raw, off + 24), (ushort)ReadU16(raw, off + 22)),
                FirstCluster = cluster,
            });
        }
    }

    private static char DecodeChar(byte[] raw, int offset)
    {
        ushort v = (ushort)(raw[offset] | (raw[offset + 1] << 8));
        return v == 0xFFFF ? '\0' : (char)v;
    }

    private static string DecodeShortName(byte[] raw, int off)
    {
        var name = Encoding.ASCII.GetString(raw, off, 8).TrimEnd(' ');
        var ext = Encoding.ASCII.GetString(raw, off + 8, 3).TrimEnd(' ');
        return ext.Length > 0 ? $"{name}.{ext}" : name;
    }

    private FatEntry? FindEntry(string path, bool requireFile)
    {
        var (parentClusters, parentIsRoot, name) = ResolveParent(path);
        var entry = FindEntryIn(parentClusters, parentIsRoot, name)
            ?? throw new VcArgumentException($"'{path}' not found.");
        if (requireFile && entry.IsDirectory)
            throw new VcArgumentException($"'{path}' is a directory.");
        return entry;
    }

    private FatEntry? FindEntryIn(List<uint> clusters, bool isRoot, string name)
    {
        var entries = new List<FatEntry>();
        ReadDirEntries(clusters, isRoot, entries);
        return entries.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private Stream OpenFile(FatEntry entry)
    {
        var clusters = ClusterList(entry.FirstCluster);
        long length = entry.SizeBytes;
        return new ClusterReadStream(this, clusters, length);
    }

    private sealed class ClusterReadStream : Stream
    {
        private readonly FatVolume _fs;
        private readonly List<uint> _clusters;
        private readonly long _length;
        private long _position;

        public ClusterReadStream(FatVolume fs, List<uint> clusters, long length)
        {
            _fs = fs; _clusters = clusters; _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length || count == 0)
                return 0;
            count = (int)Math.Min(count, _length - _position);

            int cb = _fs.ClusterBytes;
            int copied = 0;
            while (copied < count)
            {
                int clusterIndex = (int)(_position / cb);
                int within = (int)(_position % cb);
                int take = Math.Min(cb - within, count - copied);
                var data = _fs.ReadCluster(_clusters[clusterIndex]);
                Array.Copy(data, within, buffer, offset + copied, take);
                _position += take;
                copied += take;
            }
            return copied;
        }

        public override long Seek(long offset, SeekOrigin origin) => origin switch
        {
            SeekOrigin.Begin => _position = offset,
            SeekOrigin.Current => _position += offset,
            SeekOrigin.End => _position = _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // -------------------------------------------------------- dir entry write

    private void WriteDirectoryEntry(List<uint> clusters, bool isRoot, string name,
        uint firstCluster, uint size, bool isDirectory, string overwriteName)
    {
        var raw = ReadDirRegion(clusters, isRoot);
        var entries = new List<FatEntry>();
        CollectEntries(raw, entries, includeDots: true);

        bool needsLfn = NeedsLfn(name);
        string shortName;

        int targetOffset = -1;
        int lfnStart = -1;
        int lfnCount = 0;

        var target = FindEntryIn(clusters, isRoot, name);
        if (target is not null)
        {
            // find its raw offset: scan raw again matching short name after LFN name resolution
            (targetOffset, lfnStart, lfnCount) = LocateEntryRaw(raw, name);
            shortName = DecodeShortName(raw, targetOffset);
        }
        else
        {
            shortName = needsLfn
                ? FatShortName.Generate(name, 1, taken => EntryShortNames(raw).Contains(taken))
                : name.ToUpperInvariant();
        }

        int entryCount = 1 + (needsLfn ? LfnEntryCount(name) : 0);

        int writeOffset;
        if (targetOffset >= 0)
        {
            // reuse the existing slot area (LFN entries + 8.3 entry)
            writeOffset = lfnCount > 0 ? lfnStart : targetOffset;
        }
        else
        {
            writeOffset = FindFreeSlots(raw, entryCount);
            if (writeOffset < 0)
            {
                // grow the directory (root FAT12/16 cannot grow)
                if (isRoot && !_isFat32)
                    throw new IOException("Root directory is full.");
                var newCluster = AllocateChain(1);
                raw = GrowDirectory(clusters, raw, newCluster);
                clusters.Add(newCluster);
                writeOffset = raw.Length - ClusterBytes;
            }
        }

        WriteEntryAt(raw, writeOffset, name, shortName, firstCluster, size, isDirectory);
        WriteDirRegion(clusters, isRoot, raw);
    }

    private static HashSet<string> EntryShortNames(byte[] raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int off = 0; off + 32 <= raw.Length; off += 32)
        {
            byte first = raw[off];
            if (first == 0x00) break;
            if (first == 0xE5) continue;
            if (raw[off + 11] == 0x0F) continue;
            set.Add(DecodeShortName(raw, off));
        }
        return set;
    }

    private static (int entryOffset, int lfnStart, int lfnCount) LocateEntryRaw(byte[] raw, string name)
    {
        var lfnBuffer = new StringBuilder();
        int lfnStart = -1, lfnCount = 0;
        for (int off = 0; off + 32 <= raw.Length; off += 32)
        {
            byte first = raw[off];
            if (first == 0x00) break;
            if (first == 0xE5) { lfnBuffer.Clear(); lfnStart = -1; lfnCount = 0; continue; }
            byte attr = raw[off + 11];
            if (attr == 0x0F)
            {
                if (lfnStart < 0) lfnStart = off;
                lfnCount++;
                // (name reconstruction handled by the comparison at the 8.3 entry)
                lfnBuffer.Insert(0, DecodeLfnPiece(raw, off));
                continue;
            }
            string longName = lfnBuffer.Length > 0 ? lfnBuffer.ToString().TrimEnd('\0', '\uFFFF') : DecodeShortName(raw, off);
            if (string.Equals(longName, name, StringComparison.OrdinalIgnoreCase))
                return (off, lfnStart >= 0 ? lfnStart : off, lfnCount);
            lfnBuffer.Clear(); lfnStart = -1; lfnCount = 0;
        }
        return (-1, -1, 0);
    }

    private static string DecodeLfnPiece(byte[] raw, int off)
    {
        var piece = new char[13];
        piece[0] = DecodeChar(raw, off + 1);
        piece[1] = DecodeChar(raw, off + 3);
        piece[2] = DecodeChar(raw, off + 5);
        piece[3] = DecodeChar(raw, off + 7);
        piece[4] = DecodeChar(raw, off + 9);
        piece[5] = DecodeChar(raw, off + 14);
        piece[6] = DecodeChar(raw, off + 16);
        piece[7] = DecodeChar(raw, off + 18);
        piece[8] = DecodeChar(raw, off + 20);
        piece[9] = DecodeChar(raw, off + 22);
        piece[10] = DecodeChar(raw, off + 24);
        piece[11] = DecodeChar(raw, off + 28);
        piece[12] = DecodeChar(raw, off + 30);
        return new string(piece);
    }

    private void MarkEntriesDeleted(List<uint> clusters, bool isRoot, string name)
    {
        var raw = ReadDirRegion(clusters, isRoot);
        var (entryOffset, lfnStart, _) = LocateEntryRaw(raw, name);
        if (entryOffset < 0)
            throw new VcArgumentException($"'{name}' not found.");

        if (lfnStart >= 0 && lfnStart != entryOffset)
            for (int off = lfnStart; off < entryOffset; off += 32)
                raw[off] = 0xE5;
        raw[entryOffset] = 0xE5;

        WriteDirRegion(clusters, isRoot, raw);
    }

    private static int FindFreeSlots(byte[] raw, int count)
    {
        int run = 0;
        for (int off = 0; off + 32 <= raw.Length; off += 32)
        {
            byte first = raw[off];
            if (first == 0x00 || first == 0xE5)
            {
                if (++run == count)
                    return off - (count - 1) * 32;
            }
            else
            {
                run = 0;
            }
        }
        return -1;
    }

    private byte[] GrowDirectory(List<uint> clusters, byte[] raw, uint newCluster)
    {
        // link the new cluster into the FAT chain
        for (int i = 0; i < clusters.Count - 1; i++)
            _fat[clusters[i]] = clusters[i + 1];
        _fat[clusters[^1]] = newCluster;
        _fat[newCluster] = EofMarker;

        var grown = new byte[raw.Length + ClusterBytes];
        Array.Copy(raw, grown, raw.Length);
        WriteCluster(newCluster, new byte[ClusterBytes]);
        return grown;
    }

    private static bool NeedsLfn(string name)
    {
        if (name.Length > 12 || name.Contains('.'))
        {
            if (name.Length > 255) throw new VcArgumentException("Name too long.");
            int dot = name.LastIndexOf('.');
            string stem = dot > 0 ? name[..dot] : name;
            string ext = dot > 0 ? name[(dot + 1)..] : "";
            if (stem.Length <= 8 && ext.Length <= 3 && stem.Length > 0)
            {
                // could still need LFN if lowercase — treat anything not uppercase-8.3 as LFN
                return name != name.ToUpperInvariant() || !IsPlainAscii(name);
            }
            return true;
        }
        return name != name.ToUpperInvariant() || !IsPlainAscii(name);
    }

    private static bool IsPlainAscii(string s)
    {
        foreach (char c in s)
            if (c > 127 || c < 0x20 || " \"*+,/:;<=>?\\[|]".Contains(c))
                return false;
        return true;
    }

    private static int LfnEntryCount(string name)
        => (name.Length + 12) / 13;

    private static void WriteEntryAt(byte[] raw, int offset, string longName, string shortName,
        uint firstCluster, uint size, bool isDirectory)
    {
        bool lfn = NeedsLfn(longName) && longName != shortName;
        int lfnCount = lfn ? LfnEntryCount(longName) : 0;

        if (lfn)
        {
            byte checksum = FatShortName.Checksum(shortName);
            for (int i = 0; i < lfnCount; i++)
            {
                int logicalIndex = lfnCount - 1 - i; // physical order: last entry first
                int off = offset + i * 32;
                raw[off] = (byte)(logicalIndex + 1 | (i == 0 ? 0x40 : 0));
                raw[off + 11] = 0x0F;

                for (int k = 0; k < 13; k++)
                {
                    int charIndex = logicalIndex * 13 + k;
                    char c = charIndex < longName.Length ? longName[charIndex] : (charIndex == longName.Length ? '\0' : '\uFFFF');
                    (raw[off + LfnCharOffset(k)], raw[off + LfnCharOffset(k) + 1]) = ((byte)c, (byte)((ushort)c >> 8));
                }
                raw[off + 13] = checksum;
                raw[off + 12] = 0;
                raw[off + 26] = 0;
                raw[off + 27] = 0;
            }
        }

        int entryOff = offset + lfnCount * 32;
        WriteRawEntry(raw, entryOff, shortName, isDirectory ? (byte)0x10 : (byte)0x20, firstCluster, size);
    }

    private static int LfnCharOffset(int k) => k switch
    {
        < 5 => 1 + k * 2,
        < 11 => 14 + (k - 5) * 2,
        _ => 28 + (k - 11) * 2,
    };

    private static void WriteRawEntry(byte[] raw, int offset, string shortName, byte attr, uint cluster, uint size)
    {
        // share the exact on-disk decomposition with FatShortName.Checksum so the
        // LFN checksum always matches the serialized 8.3 bytes (incl. '.'/'..'
        // entries, which must keep both dots)
        var (stem, ext) = FatShortName.OnDiskParts(shortName);

        var bytes = Encoding.ASCII.GetBytes(stem);
        Array.Copy(bytes, 0, raw, offset, 8);
        bytes = Encoding.ASCII.GetBytes(ext);
        Array.Copy(bytes, 0, raw, offset + 8, 3);

        raw[offset + 11] = attr;
        raw[offset + 12] = 0; // reserved (NT flags)
        raw[offset + 13] = 0; // created tenths

        var (date, time) = FatDateTime.Encode(DateTime.UtcNow);
        WriteU16(raw, offset + 14, time); // created time
        WriteU16(raw, offset + 16, date); // created date
        WriteU16(raw, offset + 18, date); // last access date

        raw[offset + 20] = (byte)(cluster >> 16); // first cluster high (FAT32)
        raw[offset + 21] = (byte)(cluster >> 24);

        raw[offset + 22] = (byte)(time & 0xFF); raw[offset + 23] = (byte)(time >> 8);
        raw[offset + 24] = (byte)(date & 0xFF); raw[offset + 25] = (byte)(date >> 8);
        raw[offset + 26] = (byte)(cluster & 0xFF); raw[offset + 27] = (byte)((cluster >> 8) & 0xFF);

        raw[offset + 28] = (byte)size;
        raw[offset + 29] = (byte)(size >> 8);
        raw[offset + 30] = (byte)(size >> 16);
        raw[offset + 31] = (byte)(size >> 24);
    }

    private static void WriteU16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
    }
}
