namespace HCVault.Core;

/// <summary>
///   Space usage of a mounted filesystem (see <see cref="IVolumeFileSystem.GetSpace" />).
///   <see cref="TotalBytes" /> counts only the clusters available to files — the
///   volume header areas and the filesystem bookkeeping sectors (FAT, bitmap,
///   root directory) are not part of it, so <c>TotalBytes &lt;= Volume.DataSize</c>.
/// </summary>
public readonly record struct VolumeSpace
{
    /// <summary>Total capacity available to files (all clusters).</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Unallocated space, in bytes.</summary>
    public required long FreeBytes { get; init; }

    /// <summary>Allocation granularity in bytes (cluster size).</summary>
    public required long ClusterBytes { get; init; }

    /// <summary>Allocated space (files, directories; <c>TotalBytes − FreeBytes</c>).</summary>
    public long UsedBytes => TotalBytes - FreeBytes;

    public override string ToString() => $"{FreeBytes:N0} free of {TotalBytes:N0} bytes";
}
