using System.Linq;
using System.Text;

namespace HCVault.Core.Fat;

/// <summary>An entry of a FAT directory (file, subdirectory or volume label).</summary>
public sealed class FatEntry
{
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required uint FirstCluster { get; init; }
}

/// <summary>Thrown when the volume's data area does not contain a FAT filesystem.</summary>
public class FatFormatException : VcException
{
    internal FatFormatException(string message) : base(message) { }
}

internal readonly struct FatDateTime
{
    public static DateTime Decode(ushort date, ushort time)
    {
        int year = 1980 + ((date >> 9) & 0x7F);
        int month = Math.Clamp((date >> 5) & 0x0F, 1, 12);
        int day = Math.Clamp(date & 0x1F, 1, 31);
        int hour = Math.Clamp((time >> 11) & 0x1F, 0, 23);
        int minute = Math.Clamp((time >> 5) & 0x3F, 0, 59);
        int second = Math.Clamp((time & 0x1F) * 2, 0, 58);
        try { return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc); }
        catch { return new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc); }
    }

    public static (ushort date, ushort time) Encode(DateTime utc)
    {
        var t = utc < new DateTime(1980, 1, 1) ? new DateTime(1980, 1, 1) : utc;
        ushort date = (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
        ushort time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
        return (date, time);
    }
}

internal static class FatShortName
{
    private const string Invalid = "\"*+,/:;<=>?\\[|]";

    /// <summary>
    ///   Split a "STEM.EXT" short name into the two on-disk 8.3 fields: stem
    ///   padded to 8 chars, extension padded to 3 chars, no separating dot.
    ///   Non-ASCII characters fold to '?' exactly like <see cref="Encoding.ASCII" />
    ///   does when the entry is serialized, so consumers can checksum the very
    ///   bytes that end up on disk.
    /// </summary>
    public static (string stem, string ext) OnDiskParts(string shortName)
    {
        string sn = shortName.TrimEnd();
        string stem, ext;
        if (sn == "." || sn == "..")
        {
            // dot entries are stored verbatim in the name field
            stem = sn;
            ext = "";
        }
        else
        {
            int dot = sn.LastIndexOf('.');
            stem = dot > 0 ? sn[..dot] : sn;
            ext = dot > 0 ? sn[(dot + 1)..] : "";
        }

        stem = stem.Length > 8 ? stem[..8] : stem.PadRight(8);
        ext = ext.Length > 3 ? ext[..3] : ext.PadRight(3);
        return (AsciiSafe(stem), AsciiSafe(ext));
    }

    /// <summary>
    ///   LFN checksum over the 11 on-disk DIR_Name bytes (EFI FAT32 spec §7.2).
    ///   Must match the serialized 8.3 entry byte-for-byte or Windows/fastfat
    ///   silently discards the long name and displays the 8.3 alias instead.
    /// </summary>
    public static byte Checksum(string shortName)
    {
        var (stem, ext) = OnDiskParts(shortName);
        byte sum = 0;
        foreach (char c in stem + ext)
            sum = (byte)(((sum & 1) << 7) + (sum >> 1) + (byte)c);
        return sum;
    }

    private static string AsciiSafe(string s)
    {
        if (s.All(c => c <= 0x7F))
            return s;
        return string.Concat(s.Select(c => c <= 0x7F ? c : '?'));
    }

    /// <summary>Generate a Windows-style 8.3 short name (NAME~N.EXT) for a long name.</summary>
    public static string Generate(string longName, int attempt, Func<string, bool> isTaken)
    {
        string name = longName.ToUpperInvariant();
        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;
        string ext = dot > 0 ? name[(dot + 1)..] : "";

        stem = Sanitize(stem);
        ext = Sanitize(ext);

        // when the uppercased name already fits 8.3, prefer it over a ~N alias
        // (Windows-style: the LFN then only preserves character case)
        if (stem.Length is > 0 and <= 8 && ext.Length <= 3)
        {
            string plain = ext.Length > 0 ? $"{stem}.{ext}" : stem;
            if (!isTaken(plain))
                return plain;
        }

        string suffix = attempt <= 1 ? "~1" : $"~{Math.Min(attempt, 999999)}";
        string stemBase = stem.Length + suffix.Length > 8 ? stem[..(8 - suffix.Length)] : stem;

        for (int n = attempt; n < 1000000; n++)
        {
            string sfx = n == 1 && attempt <= 1 ? "~1" : $"~{n}";
            string candidate = sfx.Length > 4 ? "~~~~" : sfx;
            string shortName = (stemBase + candidate)[..Math.Min(8, stemBase.Length + candidate.Length)];
            string full = ext.Length > 0 ? $"{shortName}.{ext[..Math.Min(3, ext.Length)]}" : shortName;
            if (!isTaken(full))
                return full;
            // vary the stem for further collisions
            int numeric = n + 1;
            sfx = $"~{numeric}";
            if (stem.Length + sfx.Length > 8 && stem.Length > 0)
                stemBase = stem[..Math.Max(1, 8 - sfx.Length)];
        }
        return "AAAAAAAA";
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == ' ' || char.IsControl(c) || Invalid.Contains(c))
                continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
