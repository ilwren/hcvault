using System.Runtime.InteropServices;
using System.Text;

namespace HCVault.Core.Interop;

/// <summary>
///   P/Invoke surface of the native hcvault-core library (vcapi.h, API version 4).
///   The library name is resolved once by <see cref="NativeLoader" />.
/// </summary>
internal static partial class NativeMethods
{
    public const int ApiVersion = 4;

    // ------------------------------------------------------------------ status

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_init();

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial void vc_shutdown();

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_api_version();

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial IntPtr vc_last_error();

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_last_status();

    // ------------------------------------------------------------ enumeration

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_get_cipher_count();

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial IntPtr vc_get_cipher_name(int index);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_get_kdf_count();

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial IntPtr vc_get_kdf_name(int index);

    // --------------------------------------------------------------- creation

    internal delegate void VcProgressCallback(ulong done, ulong total, int stage, IntPtr user);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_create_volume(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ulong sizeBytes,
        ReadOnlySpan<byte> password, nuint passwordLen,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string[] keyfilePaths,
        nuint keyfileCount,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string cipher,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string kdf,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filesystem,
        int pim, int quick, int hidden,
        VcProgressCallback? progress, IntPtr progressUser);

    // ------------------------------------------------------------------ access

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial IntPtr vc_open_volume(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ReadOnlySpan<byte> password, nuint passwordLen,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string[] keyfilePaths,
        nuint keyfileCount,
        int pim, int readOnly, int useBackupHeader);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial long vc_read_sectors(IntPtr volume, byte[] buffer, ulong offset, nuint length);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial long vc_write_sectors(IntPtr volume, byte[] buffer, ulong offset, nuint length);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial ulong vc_get_data_size(IntPtr volume);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial uint vc_get_sector_size(IntPtr volume);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_get_pim(IntPtr volume);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial IntPtr vc_get_cipher_used(IntPtr volume);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial IntPtr vc_get_kdf_used(IntPtr volume);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_is_hidden(IntPtr volume);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial void vc_close_volume(IntPtr volume);

    // --------------------------------------------------------- password change

    // -------------------------------------------------- exFAT (FatFs bridge)

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_exfat_format(IntPtr volume);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial IntPtr vc_exfat_mount(IntPtr volume);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial void vc_exfat_unmount(IntPtr fs);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_exfat_mkdir(IntPtr fs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_exfat_delete(IntPtr fs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial IntPtr vc_exfat_opendir(IntPtr fs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_exfat_readdir(IntPtr dir, out VcExFatEntry entry);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial void vc_exfat_closedir(IntPtr dir);

    // mode: 0 = read existing, 1 = create/truncate write, 2 = open/append write
    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial IntPtr vc_exfat_open(IntPtr fs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial long vc_exfat_read(IntPtr file, byte[] buffer, nuint len);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial long vc_exfat_write(IntPtr file, ReadOnlySpan<byte> buffer, nuint len);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_exfat_seek(IntPtr file, ulong position);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_exfat_close(IntPtr file);

    /// <summary>Mirrors <c>vc_exfat_entry</c> from vcapi.h (blittable).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct VcExFatEntry
    {
        internal fixed byte Name[256];
        internal byte IsDirectory;
        private fixed byte Reserved0[7];
        internal ulong Size;
        internal ushort ModifiedDate;
        internal ushort ModifiedTime;
        private uint Reserved1;
    }

    /// <summary>Mirrors <c>vc_exfat_space</c> from vcapi.h (blittable).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VcExFatSpace
    {
        internal ulong TotalBytes;
        internal ulong FreeBytes;
        internal ulong ClusterBytes;
    }

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_exfat_get_space(IntPtr fs, out VcExFatSpace space);

    [LibraryImport(NativeLoader.LibraryName)]
    internal static partial int vc_change_password(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ReadOnlySpan<byte> oldPassword, nuint oldPasswordLen,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string[] oldKeyfilePaths,
        nuint oldKeyfileCount,
        int oldPim,
        ReadOnlySpan<byte> newPassword, nuint newPasswordLen,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string[] newKeyfilePaths,
        nuint newKeyfileCount,
        int newPim,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? newKdf,
        int wipeCount);
}

/// <summary>
///   Loads <c>hcvault-core</c> (hcvault-core.dll / libhcvault-core.so).
/// </summary>
internal static class NativeLoader
{
    internal const string LibraryName = "hcvault-core";

    private const int StateLoaded = 1;
    private static int _state;
    private static readonly object LoadGate = new();

    internal static string? LastLoadError { get; private set; }

    /// <summary>
    ///   Ensure the native library is loaded into the process; throws with build
    ///   guidance otherwise. A failed attempt is NOT cached as success — every
    ///   retry throws the helpful message again (so building the library and
    ///   simply retrying works without an app restart).
    /// </summary>
    internal static void EnsureLoaded()
    {
        if (Volatile.Read(ref _state) == StateLoaded)
            return;

        lock (LoadGate)
        {
            if (Volatile.Read(ref _state) == StateLoaded)
                return;

            string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

            // Candidates that exist on disk but were rejected by the OS loader
            // (wrong architecture, missing dependency, …) — keep the real error
            // so the hint can explain what actually went wrong.
            var rejected = new List<(string Path, string Error)>();

            foreach (var candidate in BuildCandidateList())
            {
                if (!File.Exists(candidate))
                    continue;
                try
                {
                    NativeLibrary.Load(candidate);
                    Volatile.Write(ref _state, StateLoaded);
                    return;
                }
                catch (Exception ex)
                {
                    rejected.Add((candidate, ex.Message));
                }
            }

            // Last resort: plain name via host default probing (also covers
            // native assets provided by the HCVault.Native NuGet package).
            try
            {
                NativeLibrary.Load(LibraryName);
                Volatile.Write(ref _state, StateLoaded);
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
            {
                LastLoadError = ex.Message;
                throw new DllNotFoundException(BuildHint(rid, rejected), ex);
            }
        }
    }

    private static List<string> BuildCandidateList()
    {
        // 1. explicit override: VCNATIVE_HOME = directory or full library path
        // 2. the application directory and its runtimes/{rid}/native
        // 3. walk up looking for native/runtimes/{rid}/native (repo layout)
        var candidates = new List<string>();

        var env = Environment.GetEnvironmentVariable("VCNATIVE_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            candidates.Add(Path.IsPathRooted(env) && Path.GetExtension(env).Length > 0
                ? env
                : Path.Combine(env, LibraryFile));
        }

        string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        string baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, LibraryFile));
        candidates.Add(Path.Combine(baseDir, "runtimes", rid, "native", LibraryFile));

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "native", "runtimes", rid, "native", LibraryFile));
            candidates.Add(Path.Combine(dir.FullName, "runtimes", rid, "native", LibraryFile));
        }

        return candidates.Distinct().ToList();
    }

    private static string BuildHint(string rid, List<(string Path, string Error)> rejected)
    {
        var sb = new StringBuilder();
        sb.Append($"Could not load '{LibraryFile}' (process architecture: {rid}).\n");

        if (rejected.Count > 0)
        {
            sb.Append("The library was FOUND but the operating system refused to load it:\n");
            foreach (var (path, error) in rejected)
            {
                sb.Append($"    {path}\n");
                sb.Append($"      {error.Trim()}\n");
                sb.Append($"      (the file is {DescribeFile(path)})\n");
            }
            sb.Append("That usually means an architecture mismatch or a missing dependency.\n");
            if (OperatingSystem.IsWindows())
                sb.Append("A library built from this repository uses the static CRT (/MT), so a\n" +
                          "missing Visual C++ Redistributable is normally NOT the cause — check\n" +
                          "the architecture (e.g. an x64 DLL cannot load into an ARM64 process).\n");
        }
        else
        {
            sb.Append(BuildFromSourceHint(rid));
        }

        var otherRids = FindOtherBuiltRids(rid);
        if (otherRids.Count > 0)
        {
            sb.Append($"\nNote: a library was built for {string.Join(", ", otherRids)} but this\n" +
                      $"process runs as {rid}. Rebuild it for the matching architecture, e.g.:\n");
            if (OperatingSystem.IsWindows())
                sb.Append($"    cmake --preset windows-{RidArch(rid)}-release\n");
            else
                sb.Append("    cmake --preset linux-release   (cross builds: CMakePresets.json)\n");
        }

        sb.Append("\nAlternatively: place the library next to the application, reference the\n" +
                  "HCVault.Native NuGet package (prebuilt libraries for Windows, Linux and\n" +
                  "Android), or point the VCNATIVE_HOME environment variable at it.");
        return sb.ToString();
    }

    private static string BuildFromSourceHint(string rid)
    {
        if (OperatingSystem.IsWindows())
            return "No hcvault-core library was found. Build it from the source distribution\n" +
                   "first (or run build.ps1 from the repository root, which does this):\n" +
                   "    cd native\n" +
                   $"    cmake --preset windows-{RidArch(rid)}-release\n" +
                   $"    cmake --build --preset windows-{RidArch(rid)}-release\n" +
                   "  (output native\\runtimes\\" + rid + "\\native\\" + LibraryFile +
                   " is discovered automatically).";
        if (OperatingSystem.IsAndroid())
            return "On Android the library must be packaged into the app as an ABI-specific .so:\n" +
                   "  build it with the NDK (e.g. cmake --preset android-arm64-release and\n" +
                   "  cmake --build --preset android-arm64-release -> runtimes/android-arm64/native/),\n" +
                   "  reference the HCVault.Native package and build with the matching android-*\n" +
                   "  RuntimeIdentifier so the .so is packaged into the APK, or point\n" +
                   "  VCNATIVE_HOME at it.";
        return "No hcvault-core library was found. Build it from the source distribution\n" +
               "first (or run ./build.sh from the repository root, which does this):\n" +
               "    cd native && cmake --preset linux-release && cmake --build --preset linux-release\n" +
               "  (output native/runtimes/linux-x64/native/libhcvault-core.so is discovered automatically).";
    }

    /// <summary>
    ///   Architectures other than the current one that have a built library in
    ///   a repo-layout native/runtimes/ tree above the application.
    /// </summary>
    private static List<string> FindOtherBuiltRids(string currentRid)
    {
        var rids = new List<string>();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            string runtimesDir = Path.Combine(dir.FullName, "native", "runtimes");
            if (!Directory.Exists(runtimesDir))
                continue;
            foreach (string ridDir in Directory.EnumerateDirectories(runtimesDir))
            {
                string name = Path.GetFileName(ridDir);
                if (name != currentRid
                    && File.Exists(Path.Combine(ridDir, "native", LibraryFile))
                    && !rids.Contains(name))
                {
                    rids.Add(name);
                }
            }
        }
        return rids;
    }

    private static string RidArch(string rid) => rid.StartsWith("win-") ? rid[4..] : rid;

    /// <summary>
    ///   Reads a file's PE or ELF header and describes its actual architecture,
    ///   so load failures can distinguish "wrong architecture" from anything else.
    /// </summary>
    private static string DescribeFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buf = new byte[4096];
            int len = fs.Read(buf, 0, buf.Length);

            if (len >= 64 && buf[0] == 0x4D && buf[1] == 0x5A) // 'MZ'
            {
                int peOff = BitConverter.ToInt32(buf, 0x3C);
                if (peOff > 0 && peOff + 6 <= len && buf[peOff] == 0x50 && buf[peOff + 1] == 0x45)
                {
                    ushort machine = BitConverter.ToUInt16(buf, peOff + 4);
                    string arch = machine switch
                    {
                        0x014C => "x86 (32-bit)",
                        0x8664 => "x64",
                        0xAA64 => "ARM64",
                        0x01C0 => "ARM (32-bit)",
                        0x0200 => "Itanium",
                        _ => $"unknown machine 0x{machine:X4}",
                    };
                    return $"a Windows {arch} executable";
                }
                return "a corrupt PE file (bad header)";
            }

            if (len >= 20 && buf[0] == 0x7F && buf[1] == 0x45 && buf[2] == 0x4C && buf[3] == 0x46) // ELF
            {
                int bits = buf[4] == 2 ? 64 : buf[4] == 1 ? 32 : 0;
                ushort machine = BitConverter.ToUInt16(buf, 18);
                string arch = machine switch
                {
                    3 => "x86 (i386)",
                    40 => "ARM (32-bit)",
                    62 => "x86-64",
                    183 => "AArch64",
                    243 => "RISC-V",
                    _ => $"unknown machine {machine}",
                };
                return $"an ELF {bits}-bit {arch} executable";
            }

            return "not a PE or ELF executable (corrupt or truncated?)";
        }
        catch
        {
            return "unreadable";
        }
    }

    internal static string LibraryFile =>
        OperatingSystem.IsWindows() ? "hcvault-core.dll" : "libhcvault-core.so";

    internal static string StringFromIntPtr(IntPtr ptr, bool free = true)
    {
        if (ptr == IntPtr.Zero)
            return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        }
        finally
        {
            // vcapi returns pointers to thread-local / cached std::string buffers
            // that stay valid until the next call on that thread — nothing to free.
        }
    }
}
