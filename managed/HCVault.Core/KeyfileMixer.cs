namespace HCVault.Core;

/// <summary>
///   Managed port of VeraCrypt's keyfile mixing (Volume/Keyfile.cpp), used
///   when keyfile contents are supplied directly as streams instead of file
///   paths. The result is byte-identical to what the native VeraCrypt core
///   computes for the same keyfiles - the test suite verifies this by
///   creating volumes with keyfile paths and opening them with equivalent
///   stream sources, and vice versa.
///
///   Algorithm (per keyfile source): every byte advances a running CRC-32
///   register; the four bytes of the register are ADDED (mod 256) into a
///   password-sized pool at four consecutive positions, wrapping around.
///   Only the first 1 MiB of each source is processed; at least one byte is
///   required. The pool starts as the password zero-padded to 64 bytes
///   (128 when the password itself is longer than 64) and becomes the
///   effective password.
/// </summary>
internal static class KeyfileMixer
{
    private const int MaxLegacyPasswordSize = 64;   // VolumePassword::MaxLegacySize
    private const int MaxPasswordSize = 128;        // VolumePassword::MaxSize
    private const int MaxProcessedLength = 1024 * 1024;
    private const int MinProcessedLength = 1;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>
    ///   Mix the password with keyfile contents: the paths are read as
    ///   regular files first (in list order), then the streams (in list
    ///   order). Returns the effective password; the caller owns and
    ///   disposes it. Each stream is read but not disposed.
    /// </summary>
    public static SecurePassword Apply(
        SecurePassword password,
        IReadOnlyList<string> keyFilePaths,
        IReadOnlyList<Stream> keyFileStreams)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(keyFilePaths);
        ArgumentNullException.ThrowIfNull(keyFileStreams);

        int poolSize = password.Length <= MaxLegacyPasswordSize
            ? MaxLegacyPasswordSize
            : MaxPasswordSize;
        var pool = new byte[poolSize];
        try
        {
            password.Span.CopyTo(pool);

            foreach (string path in keyFilePaths)
            {
                if (Directory.Exists(path))
                    throw new VcArgumentException(
                        $"'{path}' is a directory. Directory expansion is only supported in the " +
                        "KeyFiles (path-only) mode; with KeyFileStreams every path must be a regular file.");
                using FileStream file = File.OpenRead(path);
                ApplyStream(file, pool);
            }

            foreach (Stream stream in keyFileStreams)
            {
                ArgumentNullException.ThrowIfNull(stream, nameof(keyFileStreams));
                ApplyStream(stream, pool);
            }

            var mixed = SecurePassword.FromBytes(pool);
            return mixed;
        }
        finally
        {
            Array.Clear(pool);
        }
    }

    private static void ApplyStream(Stream stream, byte[] pool)
    {
        if (!stream.CanRead)
            throw new VcArgumentException("A keyfile stream is not readable.");

        long total = 0;
        uint crc = 0xFFFF_FFFF;      // Crc32 register, never final-XORed
        int poolPos = 0;
        var buffer = new byte[64 * 1024];
        try
        {
            while (total < MaxProcessedLength)
            {
                int toRead = (int)Math.Min(buffer.Length, MaxProcessedLength - total);
                int read = stream.Read(buffer, 0, toRead);
                if (read <= 0)
                    break;

                for (int i = 0; i < read; i++)
                {
                    crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
                    pool[poolPos++] += (byte)(crc >> 24);
                    pool[poolPos++] += (byte)(crc >> 16);
                    pool[poolPos++] += (byte)(crc >> 8);
                    pool[poolPos++] += (byte)crc;
                    if (poolPos >= pool.Length)
                        poolPos = 0;
                }

                total += read;
            }

            if (total < MinProcessedLength)
                throw new VcArgumentException(
                    "A keyfile source contains no data - at least one byte is required.");
        }
        finally
        {
            Array.Clear(buffer);
        }
    }
}
