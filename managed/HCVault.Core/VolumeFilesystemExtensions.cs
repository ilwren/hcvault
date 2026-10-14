using HCVault.Core.Fat;

namespace HCVault.Core;

/// <summary>
///   High-level file transfer between the host filesystem and a mounted
///   volume: single files and whole directory trees, in both directions.
///   Files are transferred whole (the current filesystem API reads and
///   writes complete files).
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
        fs.WriteFile(volumePath, File.ReadAllBytes(localPath));
    }

    /// <summary>Copy a file out of the volume (parent directories are created).</summary>
    public static void ExportFile(this IVolumeFileSystem fs, string volumePath, string localPath)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(volumePath);
        ArgumentNullException.ThrowIfNull(localPath);

        byte[] data = fs.ReadFile(volumePath);
        try
        {
            string? parent = Path.GetDirectoryName(Path.GetFullPath(localPath));
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllBytes(localPath, data);
        }
        finally
        {
            Array.Clear(data);
        }
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

    // ------------------------------------------------------------------ core

    private static void ImportDirectoryRecursive(IVolumeFileSystem fs, string localDir, string volumePath)
    {
        EnsureVolumeDirectory(fs, volumePath);

        foreach (string dir in Directory.EnumerateDirectories(localDir).OrderBy(p => p, StringComparer.Ordinal))
            ImportDirectoryRecursive(fs, dir, Join(volumePath, Path.GetFileName(dir)));

        foreach (string file in Directory.EnumerateFiles(localDir).OrderBy(p => p, StringComparer.Ordinal))
            fs.WriteFile(Join(volumePath, Path.GetFileName(file)), File.ReadAllBytes(file));
    }

    private static void ExportDirectoryRecursive(IVolumeFileSystem fs, string volumePath, string localDir)
    {
        Directory.CreateDirectory(localDir);

        foreach (FatEntry entry in fs.ListDirectory(volumePath))
        {
            if (entry.IsDirectory)
                ExportDirectoryRecursive(fs, Join(volumePath, entry.Name), Path.Combine(localDir, entry.Name));
            else
                File.WriteAllBytes(Path.Combine(localDir, entry.Name), fs.ReadFile(Join(volumePath, entry.Name)));
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
