using HCVault.Core;
using System.Text;
using HCVault.Core.Fat;

// ---------------------------------------------------------------------------
// End-to-end smoke test for HCVault.Core (managed) + hcvault-core (native).
// Run:  dotnet run -c Release --project tests/HCVault.Core.Tests
// The native library is found via VCNATIVE_HOME or the repo layout.
// ---------------------------------------------------------------------------

var failures = new List<string>();
int section = 0;

void Check(string what, Action body)
{
    section++;
    try
    {
        body();
        Console.WriteLine($"[{section,2}] OK   {what}");
    }
    catch (Exception ex)
    {
        failures.Add(what);
        Console.WriteLine($"[{section,2}] FAIL {what}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
}

// Fail fast with actionable guidance when the native library is missing —
// every other test would cascade-fail otherwise.
try
{
    HCVaultLibrary.Initialize();
}
catch (DllNotFoundException ex)
{
    Console.WriteLine("FATAL: " + ex.Message);
    Console.WriteLine();
    Console.WriteLine("No tests were run.");
    return 2;
}

string dir = Path.Combine(Path.GetTempPath(), "vcnet-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
string vol = Path.Combine(dir, "test-volume.hc");
string keyfile = Path.Combine(dir, "test.key");
File.WriteAllBytes(keyfile, RandomBytes(64_000));

Console.WriteLine($"work dir: {dir}");
Console.WriteLine($"cipher count: {HCVaultLibrary.SupportedCiphers.Count}, " +
                  $"kdf count: {HCVaultLibrary.SupportedKdfs.Count}");

// ------------------------------------------------------------------ enumerate
Check("algorithm tables match native core", HCVaultLibrary.VerifyAlgorithmTables);

// -------------------------------------------------------------------- create
Check("create volume (AES + Argon2id + keyfile + PIM 10 + FAT, quick)", () =>
{
    using var pw = SecurePassword.FromText("managed-test-passw0rd!");
    Volume.Create(new VolumeCreationOptions
    {
        Path = vol,
        SizeBytes = 5 * 1024 * 1024,
        Password = pw,
        Pim = 10,
        KeyFiles = [keyfile],
        Cipher = VcCipher.Aes,
        Kdf = VcKdf.Argon2id,
        Filesystem = VcFilesystem.Fat,
        Quick = true,
        Progress = p =>
        {
            if (p.Stage == VcCreationStage.Finished)
                Console.WriteLine($"       create progress: {p.Fraction:P0}");
        },
    });
    Console.WriteLine($"       file size: {new FileInfo(vol).Length} bytes");
});

// ---------------------------------------------------------------------- open
Check("open without keyfile fails", () =>
{
    using var pw = SecurePassword.FromText("managed-test-passw0rd!");
    try
    {
        Volume.Open(new VolumeOpenOptions { Path = vol, Password = pw, Pim = 10 });
        throw new Exception("opened without keyfile?!");
    }
    catch (VcWrongPasswordException) { /* expected */ }
});

Volume? volume = null;
Check("open with keyfile + PIM", () =>
{
    using var pw = SecurePassword.FromText("managed-test-passw0rd!");
    volume = Volume.Open(new VolumeOpenOptions { Path = vol, Password = pw, Pim = 10, KeyFiles = [keyfile] });
    Console.WriteLine($"       cipher={volume.CipherName} kdf={volume.KdfName} " +
                      $"sector={volume.SectorSize} data={volume.DataSize} hidden={volume.IsHidden}");
});

Check("sector roundtrip via stream", () =>
{
    using var s = volume!.OpenStream();
    var probe = new byte[16];
    s.Position = 0;
    s.ReadExactly(probe);
    Console.WriteLine($"       first bytes: {Convert.ToHexString(probe)}");

    s.Position = 0x1000;
    s.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    s.Flush();

    var back = new byte[8];
    s.Position = 0x1000;
    s.ReadExactly(back);
    if (!back.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }))
        throw new Exception("read-after-write mismatch");
});

// ----------------------------------------------------------------------- FAT
FatVolume? fat = null;
Check("mount FAT filesystem", () =>
{
    fat = FatVolume.Mount(volume!);
});

Check("write file (long name, 100 KiB)", () =>
{
    using var f = fat!;
    var data = RandomBytes(100 * 1024);
    f.WriteFile("\\hello-world-with-a-long-name.txt", data);

    var entries = f.ListDirectory("\\");
    foreach (var e in entries)
        Console.WriteLine($"       {e.Name}  dir={e.IsDirectory}  {e.SizeBytes} B  {e.ModifiedUtc:u}");
    if (!entries.Any(e => e.Name == "hello-world-with-a-long-name.txt"))
        throw new Exception("file not listed after write");
});

Check("read file back and compare", () =>
{
    using var f = fat!;
    var data = f.ReadFile("\\hello-world-with-a-long-name.txt");
    if (data.Length != 100 * 1024)
        throw new Exception($"size mismatch: {data.Length}");
});

Check("create directory + nested file", () =>
{
    using var f = fat!;
    f.CreateDirectory("\\Documents");
    f.WriteFile("\\Documents\\notes.md", "encrypted inside a container"u8.ToArray());
    var sub = f.ListDirectory("\\Documents");
    if (sub.Count != 1 || sub[0].Name != "notes.md")
        throw new Exception("nested file not found");
});

Check("overwrite file (shrink + regrow)", () =>
{
    using var f = fat!;
    f.WriteFile("\\Documents\\notes.md", "tiny"u8.ToArray());
    var read = f.ReadFile("\\Documents\\notes.md");
    if (read.Length != 4)
        throw new Exception("overwrite did not shrink the file");

    var big = RandomBytes(300 * 1024); // larger than before → new chain
    f.WriteFile("\\Documents\\notes.md", big);
    var verify = f.ReadFile("\\Documents\\notes.md");
    if (!verify.AsSpan().SequenceEqual(big))
        throw new Exception("regrow content mismatch");
});

Check("delete file", () =>
{
    using var f = fat!;
    f.Delete("\\Documents\\notes.md");
    try
    {
        f.ReadFile("\\Documents\\notes.md");
        throw new Exception("file still readable after delete");
    }
    catch (VcArgumentException) { /* expected */ }
});

Check("FAT space: total/free/used track allocation exactly", () =>
{
    using var f = fat!;
    var s0 = f.GetSpace();
    Console.WriteLine($"       space: {s0} (cluster {s0.ClusterBytes} B)");
    if (s0.TotalBytes <= 0 || s0.FreeBytes <= 0 || s0.FreeBytes >= s0.TotalBytes)
        throw new Exception($"bad space values: {s0}");
    if (s0.ClusterBytes < 512 || s0.TotalBytes % s0.ClusterBytes != 0)
        throw new Exception($"bad cluster size: {s0.ClusterBytes}");
    if (s0.TotalBytes > volume!.DataSize)
        throw new Exception($"total {s0.TotalBytes} exceeds the data area {volume.DataSize}");

    f.WriteFile("\\space-probe.bin", new byte[1024 * 1024]);
    var s1 = f.GetSpace();
    if (s0.FreeBytes - s1.FreeBytes != 1024 * 1024)
        throw new Exception($"1 MiB file consumed {s0.FreeBytes - s1.FreeBytes} bytes (cluster {s1.ClusterBytes})");
    if (s1.UsedBytes != s0.UsedBytes + 1024 * 1024)
        throw new Exception("used bytes did not grow by the file size");

    f.Delete("\\space-probe.bin");
    var s2 = f.GetSpace();
    if (s2.FreeBytes != s0.FreeBytes)
        throw new Exception($"free space not restored: {s2.FreeBytes} != {s0.FreeBytes}");
});

fat?.Dispose();
// the volume handle from [4] must be released before the password change below —
// change-password opens the file exclusively (ShareNone), which fails with a
// sharing violation on Windows while this handle is still open
volume?.Dispose();

// ------------------------------------------------------------ change password
Check("change password + KDF, drop keyfile", () =>
{
    using var old = SecurePassword.FromText("managed-test-passw0rd!");
    using var neu = SecurePassword.FromText("rotated!");
    Volume.ChangePassword(new ChangePasswordOptions
    {
        Path = vol,
        OldPassword = old,
        OldPim = 10,
        OldKeyFiles = [keyfile],
        NewPassword = neu,
        NewKdf = VcKdf.HmacSha512,
    });
});

Check("old credentials rejected, new accepted", () =>
{
    using var old = SecurePassword.FromText("managed-test-passw0rd!");
    using var neu = SecurePassword.FromText("rotated!");
    try
    {
        using var v = Volume.Open(new VolumeOpenOptions { Path = vol, Password = old, Pim = 10, KeyFiles = [keyfile] });
        throw new Exception("old credentials still work?!");
    }
    catch (VcWrongPasswordException) { /* expected */ }

    using var v2 = Volume.Open(new VolumeOpenOptions { Path = vol, Password = neu });
    Console.WriteLine($"       kdf after rotation: {v2.KdfName}");
});

// -------------------------------------------------------------------- hidden
Check("hidden volume: create outer (NONE) + inner (FAT)", () =>
{
    string outer = Path.Combine(dir, "hidden.hc");
    using var opw = SecurePassword.FromText("outer-pw");
    Volume.Create(new VolumeCreationOptions
    {
        Path = outer, SizeBytes = 10 * 1024 * 1024,
        Password = opw, Filesystem = VcFilesystem.None, Quick = true,
    });
    using var ipw = SecurePassword.FromText("inner-pw");
    Volume.Create(new VolumeCreationOptions
    {
        Path = outer, SizeBytes = 3 * 1024 * 1024,
        Password = ipw, Filesystem = VcFilesystem.Fat, Quick = true, Hidden = true,
    });

    using var opw2 = SecurePassword.FromText("outer-pw");
    using var o = Volume.Open(new VolumeOpenOptions { Path = outer, Password = opw2 });
    using var ipw2 = SecurePassword.FromText("inner-pw");
    using var i = Volume.Open(new VolumeOpenOptions { Path = outer, Password = ipw2 });

    using var fi = FatVolume.Mount(i);   // outer is Filesystem.None — raw sectors only
    fi.WriteFile("\\secret.txt", "hidden data"u8.ToArray());
    var secret = fi.ReadFile("\\secret.txt");
    Console.WriteLine($"       outer hidden={o.IsHidden}, inner hidden={i.IsHidden}, " +
                      $"secret in inner: {Encoding.UTF8.GetString(secret)}");
});

// -------------------------------------------------------------------- exFAT
IVolumeFileSystem? xfs = null;
Volume? xvol = null;
Check("create exFAT volume (AES + HMAC-SHA-512)", () =>
{
    string p = Path.Combine(dir, "exfat.hc");
    using var pw = SecurePassword.FromText("exfat-passw0rd!");
    Volume.Create(new VolumeCreationOptions
    {
        Path = p, SizeBytes = 8 * 1024 * 1024,
        Password = pw, Filesystem = VcFilesystem.ExFat, Quick = true,
    });
});

Check("mount exFAT via auto-detect factory", () =>
{
    string p = Path.Combine(dir, "exfat.hc");
    using var pw = SecurePassword.FromText("exfat-passw0rd!");
    xvol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw });
    xfs = VolumeFilesystem.Mount(xvol);   // sniffs the boot sector
    Console.WriteLine($"       filesystem: {xfs.GetType().Name}");
});

Check("exFAT write + list + read (long + unicode names)", () =>
{
    var data = RandomBytes(300 * 1024);
    xfs!.WriteFile("\\hello-exfat-with-a-long-name.txt", data);
    xfs.CreateDirectory("\\Documents");
    xfs.WriteFile("\\Documents\\r" + char.ConvertFromUtf32(0x00E9) + "sum" + char.ConvertFromUtf32(0x00E9) + "-exfat.md", "exfat nested"u8.ToArray());

    foreach (var e in xfs.ListDirectory("\\"))
        Console.WriteLine($"       {e.Name}  dir={e.IsDirectory}  {e.SizeBytes} B");

    var back = xfs.ReadFile("\\hello-exfat-with-a-long-name.txt");
    if (!back.AsSpan().SequenceEqual(data))
        throw new Exception("exFAT content mismatch");
    if (!xfs.ListDirectory("\\").Any(e => e.Name == "Documents" && e.IsDirectory))
        throw new Exception("exFAT directory not listed");
});

Check("exFAT overwrite + delete nested", () =>
{
    xfs!.WriteFile("\\hello-exfat-with-a-long-name.txt", "tiny"u8.ToArray());
    if (xfs.ReadFile("\\hello-exfat-with-a-long-name.txt").Length != 4)
        throw new Exception("exFAT overwrite did not shrink");
    xfs.Delete("\\Documents\\r\u00E9sum\u00E9-exfat.md");
    xfs.Delete("\\Documents");
});

Check("exFAT space: total/free/used track allocation exactly", () =>
{
    var s0 = xfs!.GetSpace();
    Console.WriteLine($"       space: {s0} (cluster {s0.ClusterBytes} B)");
    if (s0.TotalBytes <= 0 || s0.FreeBytes <= 0 || s0.FreeBytes >= s0.TotalBytes)
        throw new Exception($"bad space values: {s0}");
    if (s0.ClusterBytes < 512 || s0.TotalBytes % s0.ClusterBytes != 0)
        throw new Exception($"bad cluster size: {s0.ClusterBytes}");
    if (s0.TotalBytes > xvol!.DataSize)
        throw new Exception($"total {s0.TotalBytes} exceeds the data area {xvol.DataSize}");

    xfs.WriteFile("\\space-probe.bin", new byte[1024 * 1024]);
    var s1 = xfs.GetSpace();
    if (s0.FreeBytes - s1.FreeBytes != 1024 * 1024)
        throw new Exception($"1 MiB file consumed {s0.FreeBytes - s1.FreeBytes} bytes (cluster {s1.ClusterBytes})");

    xfs.Delete("\\space-probe.bin");
    var s2 = xfs.GetSpace();
    if (s2.FreeBytes != s0.FreeBytes)
        throw new Exception($"free space not restored: {s2.FreeBytes} != {s0.FreeBytes}");
});

xfs?.Dispose();
xvol?.Dispose();

// ------------------------------------------------- container size + async
Check("container size: exact fit + predicted fresh free space", () =>
{
    foreach (var (fs, need) in new[] {
                 (VcFilesystem.Fat, 12_345_678L),
                 (VcFilesystem.ExFat, 12_345_678L),
                 (VcFilesystem.Fat, 5L * 1024 * 1024 + 123),
                 (VcFilesystem.ExFat, 5L * 1024 * 1024 + 123) })
    {
        long size = ContainerSize.GetRequiredContainerSize(need, fs);
        Console.WriteLine($"       need {need} B ({fs}) -> container {size} B");
        if (ContainerSize.GetFileCapacity(size - 512, fs) >= need)
            throw new Exception("a 512-byte smaller container claims to fit as well");
        if (ContainerSize.GetFileCapacity(size, fs) < need)
            throw new Exception("computed container cannot fit the file");

        string p = Path.Combine(dir, $"fit-{fs}-{need}.hc");
        using (var pw = SecurePassword.FromText("fit-pw"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = size, Password = pw, Filesystem = fs, Quick = true });

        using (var pw = SecurePassword.FromText("fit-pw"))
        using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw }))
        using (var f = VolumeFilesystem.Mount(vol))
        {
            var space = f.GetSpace();
            long predicted = ContainerSize.GetFileCapacity(size, fs);
            Console.WriteLine($"       fresh free {space.FreeBytes}, predicted {predicted} (cluster {space.ClusterBytes} B)");
            if ((long)space.FreeBytes != predicted)
                throw new Exception($"geometry mismatch ({fs}): fresh free {space.FreeBytes} != predicted {predicted}");

            f.WriteFile("\\fit.bin", RandomBytes((int)need));
            if (f.ReadFile("\\fit.bin").Length != need)
                throw new Exception("readback mismatch");
        }
    }
});

Check("container size: geometry across cluster-size boundaries (FAT32 / exFAT 32K)", () =>
{
    // 100 MiB FAT -> FAT32 (1 KiB clusters); 140 MiB exFAT -> 32 KiB clusters
    foreach (var (fs, size) in new[] {
                 (VcFilesystem.Fat, 100L * 1024 * 1024),
                 (VcFilesystem.ExFat, 140L * 1024 * 1024) })
    {
        string p = Path.Combine(dir, $"geo-{fs}.hc");
        using (var pw = SecurePassword.FromText("geo-pw"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = size, Password = pw, Filesystem = fs, Quick = true });

        using (var pw = SecurePassword.FromText("geo-pw"))
        using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw }))
        using (var f = VolumeFilesystem.Mount(vol))
        {
            var space = f.GetSpace();
            long predicted = ContainerSize.GetFileCapacity(size, fs);
            Console.WriteLine($"       {fs}, {size >> 20} MiB: fresh free {space.FreeBytes}, " +
                              $"predicted {predicted} (cluster {space.ClusterBytes} B)");
            if ((long)space.FreeBytes != predicted)
                throw new Exception($"geometry mismatch ({fs}): {space.FreeBytes} != {predicted}");
        }
    }
});

Check("FAT16: on-disk FAT uses 16-bit entries (VeraCrypt-readable layout)", () =>
{
    // 6 MiB -> FAT16, 1 KiB clusters, 2 FATs, 23-sector FAT, 512 root entries
    string p = Path.Combine(dir, "fat16-raw.hc");
    using (var pw = SecurePassword.FromText("raw-pw"))
        Volume.Create(new VolumeCreationOptions
            { Path = p, SizeBytes = 6L * 1024 * 1024, Password = pw, Filesystem = VcFilesystem.Fat, Quick = true });

    using (var pw = SecurePassword.FromText("raw-pw"))
    using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw }))
    {
        long predicted = ContainerSize.GetFileCapacity(6L * 1024 * 1024, VcFilesystem.Fat);
        using (var f = VolumeFilesystem.Mount(vol))
        {
            var space = f.GetSpace();
            Console.WriteLine($"       fresh free {space.FreeBytes}, predicted {predicted}");
            if ((long)space.FreeBytes != predicted)
                throw new Exception($"fresh free {space.FreeBytes} != predicted {predicted}");
            f.WriteFile("\\one-cluster.txt", new byte[1000]);
        }

        // raw boot sector + first FAT copy, as any external FAT driver would see them
        using var raw = vol.OpenStream(writable: false);
        var boot = new byte[512];
        raw.ReadExactly(boot);
        uint bps = (uint)(boot[11] | (boot[12] << 8));
        uint spc = boot[13];
        uint reserved = (uint)(boot[14] | (boot[15] << 8));
        uint fats = boot[16];
        uint rootEntries = (uint)(boot[17] | (boot[18] << 8));
        uint fatLength = (uint)(boot[22] | (boot[23] << 8));
        Console.WriteLine($"       bps={bps} spc={spc} reserved={reserved} fats={fats} " +
                          $"rootEntries={rootEntries} fatLength={fatLength}");
        if (bps != 512 || spc != 2 || reserved != 2 || fats != 2 || rootEntries != 512 || fatLength != 23)
            throw new Exception("unexpected FAT16 boot-sector geometry");

        var fat = new byte[8];
        raw.Seek(reserved * bps, SeekOrigin.Begin);
        raw.ReadExactly(fat);
        // entry 0/1: media descriptor + EOF; entry 2: the file's single cluster -> EOF (0xFFF8)
        uint e0 = (uint)(fat[0] | (fat[1] << 8));
        uint e1 = (uint)(fat[2] | (fat[3] << 8));
        uint e2 = (uint)(fat[4] | (fat[5] << 8));
        Console.WriteLine($"       raw FAT16 entries: [{e0:X4}] [{e1:X4}] [{e2:X4}]");
        if (e0 != 0xFFF8 || e1 != 0xFFFF || e2 != 0xFFF8)
            throw new Exception($"FAT entries are not 16-bit EOF markers: {e0:X4} {e1:X4} {e2:X4}");

        using (var f = VolumeFilesystem.Mount(vol))
        {
            if (f.ReadFile("\\one-cluster.txt").Length != 1000)
                throw new Exception("readback mismatch after raw inspection");
            var space = f.GetSpace();
            if ((long)space.FreeBytes != predicted - 1024)
                throw new Exception($"free did not drop by exactly one cluster: {space.FreeBytes}");
        }
    }
});

Check("async: create / open / change password as tasks", () =>
{
    string p = Path.Combine(dir, "async.hc");
    using (var pw = SecurePassword.FromText("async-pw"))
        Volume.CreateAsync(new VolumeCreationOptions
        {
            Path = p, SizeBytes = 2L * 1024 * 1024,
            Password = pw, Filesystem = VcFilesystem.Fat, Quick = true,
        }).GetAwaiter().GetResult();

    var vol = Volume.OpenAsync(new VolumeOpenOptions { Path = p, Password = SecurePassword.FromText("async-pw") })
        .GetAwaiter().GetResult();
    Console.WriteLine($"       opened via task: {vol.CipherName}, {vol.DataSize} B data area");
    vol.Dispose();

    using (var pw = SecurePassword.FromText("async-pw"))
        Volume.ChangePasswordAsync(new ChangePasswordOptions
        {
            Path = p, OldPassword = pw, NewPassword = SecurePassword.FromText("rotated-async"),
        }).GetAwaiter().GetResult();

    using var v2 = Volume.OpenAsync(new VolumeOpenOptions { Path = p, Password = SecurePassword.FromText("rotated-async") })
        .GetAwaiter().GetResult();
    Console.WriteLine("       password rotated via task; reopened with new credentials");
});

// ------------------------------------------------------- keyfile streams
Check("keyfiles: stream sources mix exactly like native keyfile paths", () =>
{
    string kfDir = Path.Combine(dir, "kfs");
    Directory.CreateDirectory(kfDir);
    // 1 byte / 100 KB / >1 MiB (exercises the 1 MiB cap), all random
    string kf1 = Path.Combine(kfDir, "kf1.bin"); File.WriteAllBytes(kf1, new byte[] { 0x5A });
    string kf2 = Path.Combine(kfDir, "kf2.bin"); File.WriteAllBytes(kf2, RandomBytes(100_000));
    string kf3 = Path.Combine(kfDir, "kf3.bin"); File.WriteAllBytes(kf3, RandomBytes(1024 * 1024 + 4096));
    string[] paths = { kf1, kf2, kf3 };
    Func<List<Stream>> sources = () => new List<Stream>
    {
        new MemoryStream(File.ReadAllBytes(kf1)),
        new MemoryStream(File.ReadAllBytes(kf2)),
        new MemoryStream(File.ReadAllBytes(kf3)),
    };
    const VcKdf fastKdf = VcKdf.HmacSha512;

    // create with paths -> open with streams
    string p = Path.Combine(dir, "kfs.hc");
    using (var pw = SecurePassword.FromText("hunter2"))
        Volume.Create(new VolumeCreationOptions
            { Path = p, SizeBytes = 2L * 1024 * 1024, Password = pw,
              KeyFiles = paths, Kdf = fastKdf, Quick = true });
    using (var pw = SecurePassword.FromText("hunter2"))
    using (var vol = Volume.Open(new VolumeOpenOptions
            { Path = p, Password = pw, KeyFileStreams = sources() }))
        Console.WriteLine($"       opened a path-created volume with stream keyfiles ({vol.DataSize} B)");

    // create with streams -> open with paths (the reverse differential)
    using (var pw = SecurePassword.FromText("hunter2"))
        Volume.Create(new VolumeCreationOptions
            { Path = p, SizeBytes = 2L * 1024 * 1024, Password = pw,
              KeyFileStreams = sources(), Kdf = fastKdf, Quick = true });
    using (var pw = SecurePassword.FromText("hunter2"))
    using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw, KeyFiles = paths }))
        Console.WriteLine("       opened a stream-created volume with keyfile paths");

    // combined: one path + two streams, mixed in this library
    using (var pw = SecurePassword.FromText("hunter2"))
        Volume.Create(new VolumeCreationOptions
            { Path = p, SizeBytes = 2L * 1024 * 1024, Password = pw,
              KeyFiles = new[] { kf1 }, KeyFileStreams = sources().Skip(1).ToList(),
              Kdf = fastKdf, Quick = true });
    using (var pw = SecurePassword.FromText("hunter2"))
    using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw, KeyFiles = paths }))
        Console.WriteLine("       combined path+stream mixing matches the pure-path order");

    // wrong keyfile set -> rejected
    using (var pw = SecurePassword.FromText("hunter2"))
    {
        var partialSources = new List<Stream> { new MemoryStream(File.ReadAllBytes(kf1)) };
        try
        {
            using var vol = Volume.Open(new VolumeOpenOptions
                { Path = p, Password = pw, KeyFileStreams = partialSources });
            throw new Exception("a wrong keyfile set was accepted");
        }
        catch (VcWrongPasswordException) { Console.WriteLine("       wrong keyfile set rejected"); }
    }

    // empty stream -> argument error
    using (var pw = SecurePassword.FromText("hunter2"))
    {
        try
        {
            using var vol = Volume.Open(new VolumeOpenOptions
                { Path = p, Password = pw,
                  KeyFileStreams = new List<Stream> { new MemoryStream() } });
            throw new Exception("an empty keyfile stream was accepted");
        }
        catch (VcArgumentException) { Console.WriteLine("       empty keyfile stream rejected"); }
    }

    // rotate credentials with stream keyfiles on the old side
    using (var pw = SecurePassword.FromText("hunter2"))
        Volume.ChangePassword(new ChangePasswordOptions
        {
            Path = p, OldPassword = pw, OldKeyFileStreams = sources(),
            NewPassword = SecurePassword.FromText("plain-new"),
        });
    using (var pw = SecurePassword.FromText("plain-new"))
    using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw }))
        Console.WriteLine("       rotated from stream keyfiles to a plain password");
});

// ---------------------------------------------------------- path handling
Check("paths: relative, forward slashes, optional leading separator (FAT + exFAT)", () =>
{
    foreach (var fs in new[] { VcFilesystem.Fat, VcFilesystem.ExFat })
    {
        string p = Path.Combine(dir, $"paths-{fs}.hc");
        using (var pw = SecurePassword.FromText("path-pw"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = 4L * 1024 * 1024, Password = pw,
                  Filesystem = fs, Kdf = VcKdf.HmacSha512, Quick = true });

        using (var pw = SecurePassword.FromText("path-pw"))
        using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw }))
        using (var f = VolumeFilesystem.Mount(vol))
        {
            f.WriteFile("hello.txt", "rel"u8.ToArray());                    // relative, no separator
            f.CreateDirectory("docs");
            f.WriteFile("docs/note.txt", "fwd"u8.ToArray());                // forward slashes
            f.WriteFile("\\docs\\other.txt", "back"u8.ToArray());       // classic rooted form

            if (Encoding.UTF8.GetString(f.ReadFile("hello.txt")) != "rel") throw new Exception("relative read failed");
            if (Encoding.UTF8.GetString(f.ReadFile("/docs//note.txt")) != "fwd") throw new Exception("normalized read failed");
            if (Encoding.UTF8.GetString(f.ReadFile("docs/other.txt")) != "back") throw new Exception("cross-form read failed");

            var rootNames = f.ListDirectory("").Select(e => e.Name).OrderBy(n => n).ToList();
            if (!rootNames.SequenceEqual(new[] { "docs", "hello.txt" })) throw new Exception($"root listing wrong: {string.Join(",", rootNames)}");
            var docs = f.ListDirectory("/docs").Select(e => e.Name).OrderBy(n => n).ToList();
            if (!docs.SequenceEqual(new[] { "note.txt", "other.txt" })) throw new Exception($"docs listing wrong: {string.Join(",", docs)}");
        }
        Console.WriteLine($"       {fs}: relative / forward-slash / no-root forms all work");
    }
});

// ---------------------------------------------------------- import/export
Check("import/export: directory round-trip (FAT + exFAT)", () =>
{
    string src = Path.Combine(dir, "tree");
    Directory.CreateDirectory(Path.Combine(src, "data", "sub"));
    byte[] big = RandomBytes(64 * 1024);
    File.WriteAllBytes(Path.Combine(src, "readme.md"), "readme"u8.ToArray());
    File.WriteAllBytes(Path.Combine(src, "data", "sub", "a.bin"), big);
    File.WriteAllBytes(Path.Combine(src, "data", "sub", "\u6d4b\u8bd5\u6587\u4ef6.txt"), "unicode"u8.ToArray());
    Directory.CreateDirectory(Path.Combine(src, "empty-dir"));

    foreach (var fs in new[] { VcFilesystem.Fat, VcFilesystem.ExFat })
    {
        string p = Path.Combine(dir, $"tree-{fs}.hc");
        using (var pw = SecurePassword.FromText("tree-pw"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = 8L * 1024 * 1024, Password = pw,
                  Filesystem = fs, Kdf = VcKdf.HmacSha512, Quick = true });

        using (var pw = SecurePassword.FromText("tree-pw"))
        using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw }))
        using (var f = VolumeFilesystem.Mount(vol))
        {
            // single file, default target (volume root)
            f.ImportFile(Path.Combine(src, "readme.md"));
            if (Encoding.UTF8.GetString(f.ReadFile("\\readme.md")) != "readme") throw new Exception("ImportFile default path failed");

            // whole tree
            f.ImportDirectory(src, "\\backup");
            f.ExportFile("backup/data/sub/a.bin", Path.Combine(dir, "exported-single.bin"));
            if (!File.ReadAllBytes(Path.Combine(dir, "exported-single.bin")).SequenceEqual(big))
                throw new Exception("ExportFile content mismatch");

            string outDir = Path.Combine(dir, $"out-{fs}");
            f.ExportDirectory("\\backup", outDir);
            if (!File.ReadAllBytes(Path.Combine(outDir, "readme.md")).SequenceEqual("readme"u8.ToArray())) throw new Exception("export readme mismatch");
            if (!File.ReadAllBytes(Path.Combine(outDir, "data", "sub", "a.bin")).SequenceEqual(big)) throw new Exception("export a.bin mismatch");
            if (Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(outDir, "data", "sub", "\u6d4b\u8bd5\u6587\u4ef6.txt"))) != "unicode") throw new Exception("export unicode mismatch");
            if (!Directory.Exists(Path.Combine(outDir, "empty-dir"))) throw new Exception("export kept the empty directory");

            var listed = f.ListDirectory("\\backup\\data\\sub").Select(e => e.Name).OrderBy(n => n).ToList();
            if (!listed.Contains("a.bin") || !listed.Contains("\u6d4b\u8bd5\u6587\u4ef6.txt")) throw new Exception("volume listing after import incomplete");
        }
        Console.WriteLine($"       {fs}: import/export round-trip byte-identical (incl. unicode, empty dir)");
    }
});

Check("async: filesystem operations as tasks", () =>
{
    string local = Path.Combine(dir, "asyncfile.bin");
    File.WriteAllBytes(local, RandomBytes(64 * 1024));

    string p = Path.Combine(dir, "asyncfs.hc");
    using (var pw = SecurePassword.FromText("async-fs"))
        Volume.Create(new VolumeCreationOptions
            { Path = p, SizeBytes = 2L * 1024 * 1024, Password = pw,
              Kdf = VcKdf.HmacSha512, Quick = true });

    using (var pw = SecurePassword.FromText("async-fs"))
    using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw }))
    using (var f = VolumeFilesystem.Mount(vol))
    {
        f.WriteFileAsync("blob.bin", File.ReadAllBytes(local)).GetAwaiter().GetResult();
        byte[] back = f.ReadFileAsync("blob.bin").GetAwaiter().GetResult();
        if (back.Length != 64 * 1024) throw new Exception("async write/read size mismatch");

        f.ImportFileAsync(local, "again.bin").GetAwaiter().GetResult();
        var names = f.ListDirectoryAsync("").GetAwaiter().GetResult().Select(e => e.Name).OrderBy(n => n).ToList();
        if (!names.SequenceEqual(new[] { "again.bin", "blob.bin" })) throw new Exception($"async listing wrong: {string.Join(",", names)}");

        f.ExportFileAsync("again.bin", Path.Combine(dir, "async-out.bin")).GetAwaiter().GetResult();
        if (!File.ReadAllBytes(Path.Combine(dir, "async-out.bin")).SequenceEqual(File.ReadAllBytes(local)))
            throw new Exception("async export content mismatch");
    }
    Console.WriteLine("       async write/read/list/import/export round-trip OK");
});

// ------------------------------------------------------------------- summary
Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL MANAGED TESTS PASSED");
    return 0;
}
Console.WriteLine($"{failures.Count} FAILURE(S):");
foreach (var f in failures)
    Console.WriteLine($"  - {f}");
return 1;

static byte[] RandomBytes(int n)
{
    var b = new byte[n];
    Random.Shared.NextBytes(b);
    return b;
}
