namespace HCVault.Core;

/// <summary>
///   Computes the container size needed to store a payload of a given size.
///
///   The math mirrors the two formatters the native core uses for normal
///   (non-hidden) file containers: VeraCrypt's own FatFormatter for FAT
///   (native/src/veracrypt/Core/FatFormatter.cpp) and FatFs R0.15's f_mkfs
///   for exFAT, as invoked by the bridge (FM_EXFAT | FM_SFD, one FAT,
///   automatic cluster size). The fixed container overhead - the 128 KiB
///   header region plus the 128 KiB backup-header region of the V2 layout -
///   is subtracted first.
///
///   The results are exact for the bundled formatter versions: a freshly
///   created volume of the returned size can hold a file of the requested
///   size, and 512 bytes less cannot. If a FatFs or FatFormatter update ever
///   changes the geometry, this class must be updated with it (the test
///   suite cross-checks the prediction against real volumes).
/// </summary>
public static class ContainerSize
{
    private const int SectorSize = 512;

    /// <summary>128 KiB header region + 128 KiB backup header region (V2 file containers).</summary>
    private const long HeaderOverhead = 256 * 1024;

    /// <summary>Size of the compressed up-case table f_mkfs writes into every exFAT volume.</summary>
    private const long ExfatUpcaseTableBytes = 5836;

    /// <summary>f_mkfs refuses exFAT volumes below 4096 sectors (2 MiB data area).</summary>
    private const long ExfatMinDataSectors = 0x1000;

    /// <summary>f_mkfs refuses exFAT volumes with fewer than 16 clusters.</summary>
    private const long ExfatMinClusters = 16;

    /// <summary>
    ///   Smallest FAT data area the native formatter accepts:
    ///   TC_MIN_FAT_FS_SIZE = 36,864 bytes = 72 sectors of 512.
    /// </summary>
    private const long FatMinDataSectors = 72;

    /// <summary>
    ///   The largest file (in bytes) that fits into a freshly formatted volume
    ///   of the given container size. For <see cref="VcFilesystem.None"/> this
    ///   is simply the data-area size.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">negative size or unknown filesystem</exception>
    public static long GetFileCapacity(long containerSizeBytes, VcFilesystem filesystem)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(containerSizeBytes);

        long dataArea = containerSizeBytes - HeaderOverhead;
        if (dataArea < SectorSize)
            return 0;
        long dataSectors = dataArea / SectorSize;

        return filesystem switch
        {
            VcFilesystem.None => dataSectors * SectorSize,
            VcFilesystem.Fat => FatCapacity(dataSectors),
            VcFilesystem.ExFat => ExFatCapacity(dataSectors),
            _ => throw new ArgumentOutOfRangeException(nameof(filesystem), filesystem, null),
        };
    }

    /// <summary>
    ///   The smallest container size (multiple of 512) whose freshly formatted
    ///   volume can hold a single file of <paramref name="fileSizeBytes" />.
    ///   The returned size always passes <c>Volume.Create</c>'s validation:
    ///   never below the native formatter minimums (FAT 299,008 bytes,
    ///   exFAT 2,359,296, None 262,145).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">negative size or unknown filesystem</exception>
    public static long GetRequiredContainerSize(long fileSizeBytes, VcFilesystem filesystem)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSizeBytes);

        if (filesystem != VcFilesystem.None && filesystem != VcFilesystem.Fat && filesystem != VcFilesystem.ExFat)
            throw new ArgumentOutOfRangeException(nameof(filesystem), filesystem, null);

        if (filesystem == VcFilesystem.None)
            return Math.Max(HeaderOverhead + 1,   // Volume.Create floor: 1 byte of data area
                RoundUpToSector(fileSizeBytes + HeaderOverhead));

        // the smallest volume the formatter accepts at all
        long minData = filesystem == VcFilesystem.ExFat ? ExfatMinDataSectors : FatMinDataSectors;
        // with the exact per-filesystem minimums this is always above
        // Volume.Create's floor for that filesystem
        long lo = RoundUpToSector(HeaderOverhead + minData * SectorSize);

        if (GetFileCapacity(lo, filesystem) >= fileSizeBytes)
            return lo;

        // upper bound: the per-filesystem overhead is bounded well below 64 MiB
        long hi = RoundUpToSector(fileSizeBytes + HeaderOverhead + 64 * 1024 * 1024);
        while (GetFileCapacity(hi, filesystem) < fileSizeBytes)
            hi = checked(hi * 2);

        // binary search: smallest container size with capacity >= fileSizeBytes
        while (lo < hi)
        {
            long mid = lo + (hi - lo) / 2;
            if (GetFileCapacity(mid, filesystem) >= fileSizeBytes)
                hi = mid;
            else
                lo = mid + 1;
        }

        // capacity() is not strictly monotonic where the automatic cluster
        // size jumps; step back while a smaller volume still fits
        long floor = RoundUpToSector(HeaderOverhead + minData * SectorSize);
        long best = lo;
        for (long probe = RoundUpToSector(lo - SectorSize);
             probe >= floor;
             probe = RoundUpToSector(probe - SectorSize))
        {
            if (GetFileCapacity(probe, filesystem) >= fileSizeBytes)
                best = probe;
            else
                break;
        }

        return best;
    }

    // ------------------------------------------------------------------ exFAT

    // Mirrors the exFAT branch of f_mkfs (FatFs R0.15) with the bridge's
    // parameters: FM_EXFAT | FM_SFD, n_fat = 1, automatic cluster size, no
    // erase-block alignment (the bridge reports no block size).
    private static long ExFatCapacity(long dataSectors)
    {
        if (dataSectors < ExfatMinDataSectors)
            return 0;

        long au = 8;                              // sectors per cluster (auto-selection)
        if (dataSectors >= 0x80000) au = 64;      // data area >= 256 MiB
        if (dataSectors >= 0x4000000) au = 256;   // data area >= 32 GiB
        long clusterBytes = au * SectorSize;

        long fatSectors = ((dataSectors / au + 2) * 4 + SectorSize - 1) / SectorSize;
        long dataStart = 32 + fatSectors;         // boot area (padded to sector 32) + FAT
        long clusters = (dataSectors - dataStart) / au;
        if (clusters < ExfatMinClusters)
            return 0;

        // system clusters: allocation bitmap, up-case table, root directory
        long bitmapClusters = ((clusters + 7) / 8 + clusterBytes - 1) / clusterBytes;
        long upcaseClusters = (ExfatUpcaseTableBytes + clusterBytes - 1) / clusterBytes;
        long freeClusters = clusters - bitmapClusters - upcaseClusters - 1;

        return freeClusters > 0 ? freeClusters * clusterBytes : 0;
    }

    // -------------------------------------------------------------------- FAT

    // Mirrors GetFatParams() in VeraCrypt's FatFormatter.cpp: default cluster
    // size table, FAT12/16/32 selection at 4085 / 65525 clusters, two FATs,
    // 512 root-dir entries (32 sectors), and the FAT32 data-area alignment
    // loop (4096-byte granularity).
    private static long FatCapacity(long dataSectors)
    {
        const long FatRootSectors = 32;           // 512 entries x 32 bytes
        const int FatCount = 2;

        if (dataSectors < FatMinDataSectors)
            return 0;

        long volumeBytes = dataSectors * SectorSize;

        // FatFormatter's default cluster size (bytes)
        long clusterBytes =
            volumeBytes >= 2L * 1024 * 1024 * 1024 * 1024 ? 256 * 1024 :
            volumeBytes >= 512L * 1024 * 1024 * 1024 ? 128 * 1024 :
            volumeBytes >= 128L * 1024 * 1024 * 1024 ? 64 * 1024 :
            volumeBytes >= 64L * 1024 * 1024 * 1024 ? 32 * 1024 :
            volumeBytes >= 32L * 1024 * 1024 * 1024 ? 16 * 1024 :
            volumeBytes >= 16L * 1024 * 1024 * 1024 ? 8 * 1024 :
            volumeBytes >= 512L * 1024 * 1024 ? 4 * 1024 :
            volumeBytes >= 256L * 1024 * 1024 ? 2 * 1024 :
            volumeBytes >= 1L * 1024 * 1024 ? 1 * 1024 : SectorSize;

        long spc = clusterBytes / SectorSize;
        if (spc > 128)
            spc = 128;                            // FatFormatter caps at 128 sectors
        if (volumeBytes <= 256 * 1024 * 4)
            spc = 1;                              // <= 1 MiB data area: 512-byte clusters
        clusterBytes = spc * SectorSize;

        long reserved = 2;

        // FAT12 pass (always evaluated, then upgraded)
        long clusterCount = (dataSectors - FatRootSectors - reserved) / spc;
        long fatLength = ((clusterCount * 3 + 1) / 2 + SectorSize - 1) / SectorSize;

        if (clusterCount >= 4085)
        {
            // FAT16
            clusterCount = (dataSectors - FatRootSectors - reserved) / spc;
            fatLength = (clusterCount * 2 + SectorSize - 1) / SectorSize;
        }

        bool fat32 = clusterCount >= 65525;
        if (fat32)
        {
            // FAT32: reserved grows until (reserved + FATs) is 4096-byte aligned;
            // the root directory moves into a cluster chain
            reserved = 31;
            long guard = 0;
            do
            {
                reserved++;
                clusterCount = (dataSectors - reserved) / spc;
                fatLength = (clusterCount * 4 + SectorSize - 1) / SectorSize;
            }
            while ((reserved * SectorSize + fatLength * FatCount * SectorSize) % 4096 != 0
                   && ++guard < 1_000_000);
            if (guard >= 1_000_000)
                return 0;
        }

        // Usable clusters exactly as the mounting driver counts them:
        // data sectors minus reserved sectors, FATs and (FAT12/16) the fixed
        // root-directory area, divided in ONE step. The formatter subtracts
        // in two steps and can land one cluster lower; the mounter's view is
        // what governs how much a file can occupy.
        long usable = fat32
            ? (dataSectors - reserved - fatLength * FatCount) / spc - 1   // root chain
            : (dataSectors - reserved - fatLength * FatCount - FatRootSectors) / spc;

        return usable > 0 ? usable * clusterBytes : 0;
    }

    // ------------------------------------------------------------------ misc

    private static long RoundUpToSector(long value)
        => (value + SectorSize - 1) / SectorSize * SectorSize;
}
