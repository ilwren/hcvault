using System.Text;
using HCVault.Core.ExFat;
using HCVault.Core.Fat;

namespace HCVault.Core;

/// <summary>
///   Filesystem access to a volume's data area (FAT via the built-in managed
///   implementation, exFAT via the FatFs-backed native bridge). Obtain an
///   instance with <see cref="VolumeFilesystem.Mount(Volume)" />, which detects
///   the filesystem from the boot sector.
/// </summary>
public interface IVolumeFileSystem : IDisposable
{
    /// <summary>Entries of a directory (paths rooted with '\' or '/').</summary>
    IReadOnlyList<FatEntry> ListDirectory(string path);

    /// <summary>Read a whole file into memory.</summary>
    byte[] ReadFile(string path);

    /// <summary>Create or overwrite a file with the given contents.</summary>
    void WriteFile(string path, ReadOnlySpan<byte> data);

    /// <summary>Create a directory (parent must exist).</summary>
    void CreateDirectory(string path);

    /// <summary>Delete a file or an empty directory.</summary>
    void Delete(string path);

    /// <summary>Total, free and used space of the mounted filesystem.</summary>
    VolumeSpace GetSpace();
}

public static class VolumeFilesystem
{
    /// <summary>
    ///   Mount the filesystem found in an open volume. The boot sector is
    ///   sniffed to pick the FAT or exFAT implementation automatically.
    /// </summary>
    public static IVolumeFileSystem Mount(Volume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var boot = new byte[512];
        using (var s = volume.OpenStream(writable: false))
        {
            s.Position = 0;
            s.ReadExactly(boot);
        }

        // exFAT: "EXFAT   " at offset 3 of the boot sector
        if (boot.AsSpan(3, 5).SequenceEqual("EXFAT"u8))
            return ExFatVolume.Mount(volume);

        // FAT: 0x55AA signature plus a "FATx" label at 0x36 (FAT12/16) or 0x52 (FAT32)
        if (boot[510] == 0x55 && boot[511] == 0xAA
            && (Encoding.ASCII.GetString(boot, 0x36, 3) == "FAT"
                || Encoding.ASCII.GetString(boot, 0x52, 3) == "FAT"))
        {
            return FatVolume.Mount(volume);
        }

        throw new VcException(
            "The volume does not contain a recognized filesystem (FAT or exFAT). " +
            "Volumes created with Filesystem.None hold raw sectors only.");
    }
}
