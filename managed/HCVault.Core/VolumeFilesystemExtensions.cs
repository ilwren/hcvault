using System.Buffers;
using HCVault.Core.Fat;

namespace HCVault.Core;

/// <summary>
///   High-level file transfer between the host filesystem and a mounted
///   volume: single files and whole directory trees, in both directions.
///   Transfers are streamed through a small pooled buffer, so peak memory
///   stays flat no matter how large the file is. Whole-file semantics remain
///   available through <see cref="IVolumeFileSystem.ReadFile" /> /
///   <see cref="IVolumeFileSystem.WriteFile" />.
/// </summary>
public static class VolumeFilesystemExtensions
{
    /// <summary>
    ///   Copy a local file into the volume. <paramref name="volumePath" />
    ///   defaults to the file name in the volume root; parent directories
    ///   must already exist (use <see cref="ImportDirectory" /> for trees).
    /// </summary>
    public static void ImportFile(this IVolumeFileSystem fs, string localPath, string? volumePath = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(localPath);

        volumePath ??= Path.GetFileName(localPath);

        using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        using Stream destination = fs.OpenFile(volumePath, VcFileMode.Create);
        Copy(source, destination);
    }

    /// <summary>Copy a file out of the volume (parent directories are created).</summary>
    public static void ExportFile(this IVolumeFileSystem fs, string volumePath, string localPath)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(volumePath);
        ArgumentNullException.ThrowIfNull(localPath);

        string? parent = Path.GetDirectoryName(Path.GetFullPath(localPath));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        using Stream source = fs.OpenFile(volumePath, VcFileMode.Read);
        using var destination = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 1);
        Copy(source, destination);
    }

    /// <summary>
    ///   Recursively copy a local directory into the volume. The target
    ///   directory (and missing parents) are created; files keep their
    ///   names. Deterministic order: subdirectories first, then files,
    ///   both sorted by name.
    /// </summary>
    public static void ImportDirectory(this IVolumeFileSystem fs, string localDir, string volumePath = "\\")
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(localDir);
        ArgumentNullException.ThrowIfNull(volumePath);

        if (!Directory.Exists(localDir))
            throw new DirectoryNotFoundException($"'{localDir}' does not exist.");

        ImportDirectoryRecursive(fs, localDir, volumePath);
    }

    /// <summary>
    ///   Recursively copy a directory out of the volume into
    ///   <paramref name="localDir" /> (created if missing, including parents).
    /// </summary>
    public static void ExportDirectory(this IVolumeFileSystem fs, string volumePath, string localDir)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(volumePath);
        ArgumentNullException.ThrowIfNull(localDir);

        ExportDirectoryRecursive(fs, volumePath, localDir);
    }

    // ------------------------------------------------------------------ async

    // The same pattern as Volume.CreateAsync / OpenAsync: the work runs on a
    // thread-pool thread so UI callers stay responsive. The native exFAT
    // calls and the sector I/O are not cancellable mid-flight - the tokens
    // only prevent an operation that has not started yet.

    /// <summary><see cref="IVolumeFileSystem.ReadFile"/> as a thread-pool task.</summary>
    public static Task<byte[]> ReadFileAsync(this IVolumeFileSystem fs, string path, CancellationToken cancellationToken = default)
        => Task.Run(() => fs.ReadFile(path), cancellationToken);

    /// <summary><see cref="IVolumeFileSystem.WriteFile"/> as a thread-pool task (byte array form; spans cannot cross task boundaries).</summary>
    public static Task WriteFileAsync(this IVolumeFileSystem fs, string path, byte[] data, CancellationToken cancellationToken = default)
        => Task.Run(() => fs.WriteFile(path, data), cancellationToken);

    /// <summary><see cref="IVolumeFileSystem.ListDirectory"/> as a thread-pool task.</summary>
    public static Task<IReadOnlyList<FatEntry>> ListDirectoryAsync(this IVolumeFileSystem fs, string path, CancellationToken cancellationToken = default)
        => Task.Run(() => fs.ListDirectory(path), cancellationToken);

    /// <summary><see cref="ImportFile"/> as a thread-pool task.</summary>
    public static Task ImportFileAsync(this IVolumeFileSystem fs, string localPath, string? volumePath = null, CancellationToken cancellationToken = default)
        => Task.Run(() => fs.ImportFile(localPath, volumePath), cancellationToken);

    /// <summary><see cref="ExportFile"/> as a thread-pool task.</summary>
    public static Task ExportFileAsync(this IVolumeFileSystem fs, string volumePath, string localPath, CancellationToken cancellationToken = default)
        => Task.Run(() => fs.ExportFile(volumePath, localPath), cancellationToken);

    /// <summary><see cref="ImportDirectory"/> as a thread-pool task.</summary>
    public static Task ImportDirectoryAsync(this IVolumeFileSystem fs, string localDir, string volumePath = "\\", CancellationToken cancellationToken = default)
        => Task.Run(() => fs.ImportDirectory(localDir, volumePath), cancellationToken);

    /// <summary><see cref="ExportDirectory"/> as a thread-pool task.</summary>
    public static Task ExportDirectoryAsync(this IVolumeFileSystem fs, string volumePath, string localDir, CancellationToken cancellationToken = default)
        => Task.Run(() => fs.ExportDirectory(volumePath, localDir), cancellationToken);

    // ------------------------------------------------------------------ core

    /// <summary>
    ///   Chunk size for streamed transfers. One MiB keeps the syscall and
    ///   sector-I/O overhead low while staying far below the 85 KB large-object
    ///   heap threshold concern - the buffer is pooled, so it is allocated once
    ///   per process rather than once per file.
    /// </summary>
    private const int TransferBufferBytes = 1 << 20;

    /// <summary>
    ///   Copy one stream into another through a pooled buffer. The buffer is
    ///   wiped before it goes back to the pool: it held plaintext.
    /// </summary>
    private static void Copy(Stream source, Stream destination)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferBytes);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                destination.Write(buffer, 0, read);
        }
        finally
        {
            Array.Clear(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ImportDirectoryRecursive(IVolumeFileSystem fs, string localDir, string volumePath)
    {
        EnsureVolumeDirectory(fs, volumePath);

        foreach (string dir in Directory.EnumerateDirectories(localDir).OrderBy(p => p, StringComparer.Ordinal))
            ImportDirectoryRecursive(fs, dir, Join(volumePath, Path.GetFileName(dir)));

        foreach (string file in Directory.EnumerateFiles(localDir).OrderBy(p => p, StringComparer.Ordinal))
            fs.ImportFile(file, Join(volumePath, Path.GetFileName(file)));
    }

    private static void ExportDirectoryRecursive(IVolumeFileSystem fs, string volumePath, string localDir)
    {
        Directory.CreateDirectory(localDir);

        foreach (FatEntry entry in fs.ListDirectory(volumePath))
        {
            if (entry.IsDirectory)
                ExportDirectoryRecursive(fs, Join(volumePath, entry.Name), Path.Combine(localDir, entry.Name));
            else
                fs.ExportFile(Join(volumePath, entry.Name), Path.Combine(localDir, entry.Name));
        }
    }

    /// <summary>Join a volume directory path and an entry name ('\'-separated, rooted).</summary>
    internal static string Join(string directory, string name)
    {
        string trimmed = directory.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? "\\" + name : trimmed + "\\" + name;
    }

    private static void EnsureVolumeDirectory(IVolumeFileSystem fs, string path)
    {
        string[] parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return; // the root always exists

        string built = "";
        foreach (string part in parts)
        {
            string parent = built;
            built = built.Length == 0 ? "\\" + part : built + "\\" + part;
            if (!VolumeDirectoryExists(fs, parent, part))
                fs.CreateDirectory(built);
        }
    }

    private static bool VolumeDirectoryExists(IVolumeFileSystem fs, string parent, string name)
    {
        foreach (FatEntry entry in fs.ListDirectory(parent.Length == 0 ? "\\" : parent))
            if (entry.IsDirectory && string.Equals(entry.Name, name, StringComparison.Ordinal))
                return true;
        return false;
    }
}
