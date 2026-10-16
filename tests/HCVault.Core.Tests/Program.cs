using HCVault.Core;
using HCVault.Core.Fat;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

// ---------------------------------------------------------------------------
// End-to-end smoke test for HCVault.Core (managed) + hcvault-core (native).
// Run:  dotnet run -c Release --project tests/HCVault.Core.Tests
// The native library is found via VCNATIVE_HOME or the repo layout.
//
// Independent scenarios (each owning its own volumes) run in parallel.
// Set HCV_TEST_SERIAL=1 to run them one by one, e.g. when debugging a failure.
// Check ids are stable and referenced from release notes: keep them unique
// and sequential when adding tests. Opens pass an explicit KDF hint except
// where the full KDF/layout search itself is what the check exercises.
// ---------------------------------------------------------------------------

var failures = new ConcurrentBag<(int Id, string What)>();
var usedIds = new HashSet<int>();
var consoleLock = new object();

void Log(string line)
{
    lock (consoleLock)
        Console.WriteLine(line);
}

void Check(int id, string what, Action body)
{
    lock (usedIds)
    {
        if (!usedIds.Add(id))
            throw new InvalidOperationException($"duplicate check id {id}");
    }
    var sw = Stopwatch.StartNew();
    try
    {
        body();
        Log($"[{id,2}] OK   {what}  ({sw.Elapsed.TotalSeconds:0.0}s)");
    }
    catch (Exception ex)
    {
        failures.Add((id, what));
        Log($"[{id,2}] FAIL {what}: {ex.Message}  ({sw.Elapsed.TotalSeconds:0.0}s)");
        Log(ex.StackTrace ?? "");
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

string root = Path.Combine(Path.GetTempPath(), "vcnet-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);

Log($"work dir: {root}");
Log($"cipher count: {HCVaultLibrary.SupportedCiphers.Count}, " +
    $"kdf count: {HCVaultLibrary.SupportedKdfs.Count}");

// ------------------------------------------------------------------ enumerate
Check(1, "algorithm tables match native core", HCVaultLibrary.VerifyAlgorithmTables);

// Scenarios are declared in check-id order but are independent: each one
// creates (and cleans up after itself in) its own subdirectory.
var scenarios = new List<(string Name, Action Run)>();
void Scenario(string name, Action run) => scenarios.Add((name, run));

// --------------------------------------------------------------- Argon2 + FAT
Scenario("Argon2 + FAT workflow", () =>
{
    string g = Path.Combine(root, "fat");
    Directory.CreateDirectory(g);
    string vol = Path.Combine(g, "test-volume.hc");
    string keyfile = Path.Combine(g, "test.key");
    File.WriteAllBytes(keyfile, RandomBytes(64_000));

    Check(2, "create volume (AES + Argon2id + keyfile + PIM 10 + FAT, quick)", () =>
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
                    Log($"       create progress: {p.Fraction:P0}");
            },
        });
        Log($"       file size: {new FileInfo(vol).Length} bytes");
    });

    Check(3, "open without keyfile fails", () =>
    {
        // no KDF hint on purpose: wrong credentials must fall back to the
        // full KDF x layout search and still end in a clean rejection
        using var pw = SecurePassword.FromText("managed-test-passw0rd!");
        try
        {
            Volume.Open(new VolumeOpenOptions { Path = vol, Password = pw, Pim = 10 });
            throw new Exception("opened without keyfile?!");
        }
        catch (VcWrongPasswordException) { /* expected */ }
    });

    Volume? volume = null;
    Check(4, "open with keyfile + PIM (Argon2id hint: one derivation)", () =>
    {
        using var pw = SecurePassword.FromText("managed-test-passw0rd!");
        volume = Volume.Open(new VolumeOpenOptions
        {
            Path = vol, Password = pw, Pim = 10, KeyFiles = [keyfile], Kdf = VcKdf.Argon2id,
        });
        Log($"       cipher={volume.CipherName} kdf={volume.KdfName} " +
            $"sector={volume.SectorSize} data={volume.DataSize} hidden={volume.IsHidden}");
    });

    Check(5, "sector roundtrip via stream", () =>
    {
        using var s = volume!.OpenStream();
        var probe = new byte[16];
        s.Position = 0;
        s.ReadExactly(probe);
        Log($"       first bytes: {Convert.ToHexString(probe)}");

        s.Position = 0x1000;
        s.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        s.Flush();

        var back = new byte[8];
        s.Position = 0x1000;
        s.ReadExactly(back);
        if (!back.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }))
            throw new Exception("read-after-write mismatch");
    });

    FatVolume? fat = null;
    Check(6, "mount FAT filesystem", () =>
    {
        fat = FatVolume.Mount(volume!);
    });

    Check(7, "write file (long name, 100 KiB)", () =>
    {
        using var f = fat!;
        var data = RandomBytes(100 * 1024);
        f.WriteFile("\\hello-world-with-a-long-name.txt", data);

        var entries = f.ListDirectory("\\");
        foreach (var e in entries)
            Log($"       {e.Name}  dir={e.IsDirectory}  {e.SizeBytes} B  {e.ModifiedUtc:u}");
        if (!entries.Any(e => e.Name == "hello-world-with-a-long-name.txt"))
            throw new Exception("file not listed after write");
    });

    Check(8, "read file back and compare", () =>
    {
        using var f = fat!;
        var data = f.ReadFile("\\hello-world-with-a-long-name.txt");
        if (data.Length != 100 * 1024)
            throw new Exception($"size mismatch: {data.Length}");
    });

    Check(9, "create directory + nested file", () =>
    {
        using var f = fat!;
        f.CreateDirectory("\\Documents");
        f.WriteFile("\\Documents\\notes.md", "encrypted inside a container"u8.ToArray());
        var sub = f.ListDirectory("\\Documents");
        if (sub.Count != 1 || sub[0].Name != "notes.md")
            throw new Exception("nested file not found");
    });

    Check(10, "overwrite file (shrink + regrow)", () =>
    {
        using var f = fat!;
        f.WriteFile("\\Documents\\notes.md", "tiny"u8.ToArray());
        var read = f.ReadFile("\\Documents\\notes.md");
        if (read.Length != 4)
            throw new Exception("overwrite did not shrink the file");

        var big = RandomBytes(300 * 1024); // larger than before -> new chain
        f.WriteFile("\\Documents\\notes.md", big);
        var verify = f.ReadFile("\\Documents\\notes.md");
        if (!verify.AsSpan().SequenceEqual(big))
            throw new Exception("regrow content mismatch");
    });

    Check(11, "delete file", () =>
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

    Check(12, "FAT space: total/free/used track allocation exactly", () =>
    {
        using var f = fat!;
        var s0 = f.GetSpace();
        Log($"       space: {s0} (cluster {s0.ClusterBytes} B)");
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
    volume?.Dispose();
});

// -------------------------------------------------------- password rotation
Scenario("password rotation", () =>
{
    string g = Path.Combine(root, "rotation");
    Directory.CreateDirectory(g);
    string vol = Path.Combine(g, "test-volume.hc");
    string keyfile = Path.Combine(g, "test.key");
    File.WriteAllBytes(keyfile, RandomBytes(64_000));

    // same creation path as check [2]: Argon2id + keyfile + PIM 10. No other
    // handle is open on the file, so the exclusive open below is safe on
    // Windows as well.
    using (var pw = SecurePassword.FromText("managed-test-passw0rd!"))
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
        });

    Check(13, "change password + KDF, drop keyfile", () =>
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

    Check(14, "old credentials rejected, new accepted", () =>
    {
        using var old = SecurePassword.FromText("managed-test-passw0rd!");
        using var neu = SecurePassword.FromText("rotated!");
        try
        {
            // no hint on purpose: stale credentials run the full search
            using var v = Volume.Open(new VolumeOpenOptions { Path = vol, Password = old, Pim = 10, KeyFiles = [keyfile] });
            throw new Exception("old credentials still work?!");
        }
        catch (VcWrongPasswordException) { /* expected */ }

        using var v2 = Volume.Open(new VolumeOpenOptions
            { Path = vol, Password = neu, Kdf = VcKdf.HmacSha512 });
        Log($"       kdf after rotation: {v2.KdfName}");
    });
});

// -------------------------------------------------------------------- hidden
Scenario("hidden volume", () =>
{
    string g = Path.Combine(root, "hidden");
    Directory.CreateDirectory(g);

    Check(15, "hidden volume: create outer (NONE) + inner (FAT)", () =>
    {
        string outer = Path.Combine(g, "hidden.hc");
        using var opw = SecurePassword.FromText("outer-pw");
        Volume.Create(new VolumeCreationOptions
        {
            Path = outer, SizeBytes = 10 * 1024 * 1024,
            Password = opw, Filesystem = VcFilesystem.None, Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true,
        });
        using var ipw = SecurePassword.FromText("inner-pw");
        Volume.Create(new VolumeCreationOptions
        {
            Path = outer, SizeBytes = 3 * 1024 * 1024,
            Password = ipw, Filesystem = VcFilesystem.Fat, Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true, Hidden = true,
        });

        using var opw2 = SecurePassword.FromText("outer-pw");
        using var o = Volume.Open(new VolumeOpenOptions
            { Path = outer, Password = opw2, Kdf = VcKdf.HmacSha512, Pim = 1 });
        using var ipw2 = SecurePassword.FromText("inner-pw");
        using var i = Volume.Open(new VolumeOpenOptions
            { Path = outer, Password = ipw2, Kdf = VcKdf.HmacSha512, Pim = 1 });

        using var fi = FatVolume.Mount(i);   // outer is Filesystem.None — raw sectors only
        fi.WriteFile("\\secret.txt", "hidden data"u8.ToArray());
        var secret = fi.ReadFile("\\secret.txt");
        Log($"       outer hidden={o.IsHidden}, inner hidden={i.IsHidden}, " +
            $"secret in inner: {Encoding.UTF8.GetString(secret)}");
    });
});

// -------------------------------------------------------------------- exFAT
Scenario("exFAT volume", () =>
{
    string g = Path.Combine(root, "exfat");
    Directory.CreateDirectory(g);

    IVolumeFileSystem? xfs = null;
    Volume? xvol = null;
    Check(16, "create exFAT volume (AES + HMAC-SHA-512)", () =>
    {
        string p = Path.Combine(g, "exfat.hc");
        using var pw = SecurePassword.FromText("exfat-passw0rd!");
        Volume.Create(new VolumeCreationOptions
        {
            Path = p, SizeBytes = 8 * 1024 * 1024,
            Password = pw, Filesystem = VcFilesystem.ExFat, Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true,
        });
    });

    Check(17, "mount exFAT via auto-detect factory", () =>
    {
        string p = Path.Combine(g, "exfat.hc");
        using var pw = SecurePassword.FromText("exfat-passw0rd!");
        xvol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 });
        xfs = VolumeFilesystem.Mount(xvol);   // sniffs the boot sector
        Log($"       filesystem: {xfs.GetType().Name}");
    });

    Check(18, "exFAT write + list + read (long + unicode names)", () =>
    {
        var data = RandomBytes(300 * 1024);
        xfs!.WriteFile("\\hello-exfat-with-a-long-name.txt", data);
        xfs.CreateDirectory("\\Documents");
        xfs.WriteFile("\\Documents\\r" + char.ConvertFromUtf32(0x00E9) + "sum" + char.ConvertFromUtf32(0x00E9) + "-exfat.md", "exfat nested"u8.ToArray());

        foreach (var e in xfs.ListDirectory("\\"))
            Log($"       {e.Name}  dir={e.IsDirectory}  {e.SizeBytes} B");

        var back = xfs.ReadFile("\\hello-exfat-with-a-long-name.txt");
        if (!back.AsSpan().SequenceEqual(data))
            throw new Exception("exFAT content mismatch");
        if (!xfs.ListDirectory("\\").Any(e => e.Name == "Documents" && e.IsDirectory))
            throw new Exception("exFAT directory not listed");
    });

    Check(19, "exFAT overwrite + delete nested", () =>
    {
        xfs!.WriteFile("\\hello-exfat-with-a-long-name.txt", "tiny"u8.ToArray());
        if (xfs.ReadFile("\\hello-exfat-with-a-long-name.txt").Length != 4)
            throw new Exception("exFAT overwrite did not shrink");
        xfs.Delete("\\Documents\\r\u00E9sum\u00E9-exfat.md");
        xfs.Delete("\\Documents");
    });

    Check(20, "exFAT space: total/free/used track allocation exactly", () =>
    {
        var s0 = xfs!.GetSpace();
        Log($"       space: {s0} (cluster {s0.ClusterBytes} B)");
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
});

// --------------------------------------------------- container size + async
Scenario("container fit", () =>
{
    string g = Path.Combine(root, "fit");
    Directory.CreateDirectory(g);

    Check(21, "container size: exact fit + predicted fresh free space", () =>
    {
        foreach (var (fs, need) in new[] {
                     (VcFilesystem.Fat, 12_345_678L),
                     (VcFilesystem.ExFat, 12_345_678L),
                     (VcFilesystem.Fat, 5L * 1024 * 1024 + 123),
                     (VcFilesystem.ExFat, 5L * 1024 * 1024 + 123) })
        {
            long size = ContainerSize.GetRequiredContainerSize(need, fs);
            Log($"       need {need} B ({fs}) -> container {size} B");
            if (ContainerSize.GetFileCapacity(size - 512, fs) >= need)
                throw new Exception("a 512-byte smaller container claims to fit as well");
            if (ContainerSize.GetFileCapacity(size, fs) < need)
                throw new Exception("computed container cannot fit the file");

            string p = Path.Combine(g, $"fit-{fs}-{need}.hc");
            using (var pw = SecurePassword.FromText("fit-pw"))
                Volume.Create(new VolumeCreationOptions
                    { Path = p, SizeBytes = size, Password = pw, Filesystem = fs,
                      Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });

            using (var pw = SecurePassword.FromText("fit-pw"))
            using (var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
            using (var f = VolumeFilesystem.Mount(vol))
            {
                var space = f.GetSpace();
                long predicted = ContainerSize.GetFileCapacity(size, fs);
                Log($"       fresh free {space.FreeBytes}, predicted {predicted} (cluster {space.ClusterBytes} B)");
                if ((long)space.FreeBytes != predicted)
                    throw new Exception($"geometry mismatch ({fs}): fresh free {space.FreeBytes} != predicted {predicted}");

                f.WriteFile("\\fit.bin", RandomBytes((int)need));
                if (f.ReadFile("\\fit.bin").Length != need)
                    throw new Exception("readback mismatch");
            }
        }
    });
});

Scenario("cluster geometry", () =>
{
    string g = Path.Combine(root, "geo");
    Directory.CreateDirectory(g);

    Check(22, "container size: geometry across cluster-size boundaries (FAT32 / exFAT 32K)", () =>
    {
        // 100 MiB FAT -> FAT32 (1 KiB clusters); 140 MiB exFAT -> 32 KiB clusters
        foreach (var (fs, size) in new[] {
                     (VcFilesystem.Fat, 100L * 1024 * 1024),
                     (VcFilesystem.ExFat, 140L * 1024 * 1024) })
        {
            string p = Path.Combine(g, $"geo-{fs}.hc");
            using (var pw = SecurePassword.FromText("geo-pw"))
                Volume.Create(new VolumeCreationOptions
                    { Path = p, SizeBytes = size, Password = pw, Filesystem = fs,
                      Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });

            using (var pw = SecurePassword.FromText("geo-pw"))
            using (var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
            using (var f = VolumeFilesystem.Mount(vol))
            {
                var space = f.GetSpace();
                long predicted = ContainerSize.GetFileCapacity(size, fs);
                Log($"       {fs}, {size >> 20} MiB: fresh free {space.FreeBytes}, " +
                    $"predicted {predicted} (cluster {space.ClusterBytes} B)");
                if ((long)space.FreeBytes != predicted)
                    throw new Exception($"geometry mismatch ({fs}): {space.FreeBytes} != {predicted}");
            }
        }
    });
});

Scenario("FAT16 layout", () =>
{
    string g = Path.Combine(root, "fat16");
    Directory.CreateDirectory(g);

    Check(23, "FAT16: on-disk FAT uses 16-bit entries (VeraCrypt-readable layout)", () =>
    {
        // 6 MiB -> FAT16, 1 KiB clusters, 2 FATs, 23-sector FAT, 512 root entries
        string p = Path.Combine(g, "fat16-raw.hc");
        using (var pw = SecurePassword.FromText("raw-pw"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = 6L * 1024 * 1024, Password = pw, Filesystem = VcFilesystem.Fat,
                  Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });

        using (var pw = SecurePassword.FromText("raw-pw"))
        using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
        {
            long predicted = ContainerSize.GetFileCapacity(6L * 1024 * 1024, VcFilesystem.Fat);
            using (var f = VolumeFilesystem.Mount(vol))
            {
                var space = f.GetSpace();
                Log($"       fresh free {space.FreeBytes}, predicted {predicted}");
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
            Log($"       bps={bps} spc={spc} reserved={reserved} fats={fats} " +
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
            Log($"       raw FAT16 entries: [{e0:X4}] [{e1:X4}] [{e2:X4}]");
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
});

Scenario("async volume operations", () =>
{
    string g = Path.Combine(root, "async");
    Directory.CreateDirectory(g);

    Check(24, "async: create / open / change password as tasks", () =>
    {
        string p = Path.Combine(g, "async.hc");
        using (var pw = SecurePassword.FromText("async-pw"))
            Volume.CreateAsync(new VolumeCreationOptions
            {
                Path = p, SizeBytes = 2L * 1024 * 1024,
                Password = pw, Filesystem = VcFilesystem.Fat, Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true,
            }).GetAwaiter().GetResult();

        var vol = Volume.OpenAsync(new VolumeOpenOptions
            { Path = p, Password = SecurePassword.FromText("async-pw"), Kdf = VcKdf.HmacSha512, Pim = 1 })
            .GetAwaiter().GetResult();
        Log($"       opened via task: {vol.CipherName}, {vol.DataSize} B data area");
        vol.Dispose();

        using (var pw = SecurePassword.FromText("async-pw"))
            Volume.ChangePasswordAsync(new ChangePasswordOptions
            {
                Path = p, OldPassword = pw, OldPim = 1, NewPim = 1,
                NewPassword = SecurePassword.FromText("rotated-async"),
            }).GetAwaiter().GetResult();

        using var v2 = Volume.OpenAsync(new VolumeOpenOptions
            { Path = p, Password = SecurePassword.FromText("rotated-async"), Kdf = VcKdf.HmacSha512, Pim = 1 })
            .GetAwaiter().GetResult();
        Log("       password rotated via task; reopened with new credentials");
    });
});

Scenario("size limits", () =>
{
    string g = Path.Combine(root, "limits");
    Directory.CreateDirectory(g);

    Check(25, "volume size limits: exact per-filesystem minimums, clear errors", () =>
    {
        void ExpectArgError(VcFilesystem fs, long size)
        {
            string p = Path.Combine(g, $"toosmall-{fs}-{size}.hc");
            try
            {
                using var pw = SecurePassword.FromText("x");
                Volume.Create(new VolumeCreationOptions
                    { Path = p, SizeBytes = size, Password = pw, Filesystem = fs,
                      Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });
                throw new Exception($"{fs} volume of {size} B was accepted");
            }
            catch (VcArgumentException ex) when (!ex.Message.Contains("VolumeCreator"))
            {
                Log($"       {fs} {size} B rejected: {ex.Message.Split('.')[0]}.");
            }
        }
        void ExpectOk(VcFilesystem fs, long size)
        {
            string p = Path.Combine(g, $"minok-{fs}-{size}.hc");
            using (var pw = SecurePassword.FromText("x"))
                Volume.Create(new VolumeCreationOptions
                    { Path = p, SizeBytes = size, Password = pw, Filesystem = fs,
                      Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });
            using (var pw = SecurePassword.FromText("x"))
            using (var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
                Log($"       {fs} {size} B OK (data area {vol.DataSize} B)");
            File.Delete(p);
        }

        ExpectArgError(VcFilesystem.None, 262_144);
        ExpectOk(VcFilesystem.None, 262_145);
        ExpectArgError(VcFilesystem.Fat, 299_007);      // used to die in the native core (CreationThread:74)
        ExpectOk(VcFilesystem.Fat, 299_008);
        ExpectArgError(VcFilesystem.ExFat, 2L * 1024 * 1024 + 256 * 1024 - 1);
        ExpectOk(VcFilesystem.ExFat, 2L * 1024 * 1024 + 256 * 1024);
    });
});

Scenario("container minimums", () =>
{
    string g = Path.Combine(root, "minimums");
    Directory.CreateDirectory(g);

    Check(26, "container size: returned minimums are exactly creatable", () =>
    {
        // regression: GetRequiredContainerSize used to answer 294,912 for small
        // FAT files (64-sector floor) - below the formatter's real 72-sector
        // minimum, so Volume.Create rejected its own answer
        if (ContainerSize.GetRequiredContainerSize(1, VcFilesystem.Fat) != 299_008L)
            throw new Exception("FAT minimum container is not 299008");
        if (ContainerSize.GetRequiredContainerSize(1, VcFilesystem.ExFat) != 2_359_296L)
            throw new Exception("exFAT minimum container is not 2359296");
        if (ContainerSize.GetRequiredContainerSize(0, VcFilesystem.None) < 262_145L)
            throw new Exception("None minimum container below 262145");

        foreach (var (fs, file) in new[] {
                     (VcFilesystem.Fat, 1L), (VcFilesystem.Fat, 18_432L), (VcFilesystem.Fat, 18_433L),
                     (VcFilesystem.Fat, 100_000L), (VcFilesystem.ExFat, 1L), (VcFilesystem.None, 0L) })
        {
            long container = ContainerSize.GetRequiredContainerSize(file, fs);
            string p = Path.Combine(g, $"req-{fs}-{file}.hc");
            using (var pw = SecurePassword.FromText("x"))
                Volume.Create(new VolumeCreationOptions
                    { Path = p, SizeBytes = container, Password = pw, Filesystem = fs,
                      Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });
            using (var pw = SecurePassword.FromText("x"))
            using (var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
            {
                if (fs != VcFilesystem.None)
                {
                    using var f = VolumeFilesystem.Mount(vol);
                    f.WriteFile("payload.bin", new byte[file]);
                }
                Log($"       {fs} file {file} B -> container {container} B, created and filled");
            }
            File.Delete(p);
        }
    });
});

Scenario("KDF hint", () =>
{
    string g = Path.Combine(root, "kdfhint");
    Directory.CreateDirectory(g);

    Check(27, "open with an explicit KDF: fast path and fast failure", () =>
    {
        string p = Path.Combine(g, "kdfhint.hc");
        using (var pw = SecurePassword.FromText("hint-pw"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = 2L * 1024 * 1024, Password = pw,
                  Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });

        // correct hint: opens as before
        using (var pw = SecurePassword.FromText("hint-pw"))
        using (var vol = Volume.Open(new VolumeOpenOptions
                { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
            if (vol.KdfName != "HMAC-SHA-512")
                throw new Exception($"unexpected KDF: {vol.KdfName}");

        // no hint: auto-detection still works (PIM 1 as created)
        using (var pw = SecurePassword.FromText("hint-pw"))
        using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw, Pim = 1 }))
            Log($"       auto-detect and explicit hint agree: {vol.KdfName}");

        // wrong password WITH the correct hint: one derivation, then rejection
        using (var pw = SecurePassword.FromText("nope"))
        {
            try
            {
                using var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 });
                throw new Exception("wrong password accepted");
            }
            catch (VcWrongPasswordException) { Log("       wrong password rejected (single derivation)"); }
        }

        // wrong hint (volume is HMAC-SHA-512, hint claims Argon2): rejected as well
        using (var pw = SecurePassword.FromText("hint-pw"))
        {
            try
            {
                using var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, Kdf = VcKdf.Argon2id, Pim = 1 });
                throw new Exception("wrong KDF hint accepted");
            }
            catch (VcWrongPasswordException) { Log("       wrong KDF hint rejected"); }
        }
    });
});

// ------------------------------------------------------- keyfile streams
Scenario("keyfile streams", () =>
{
    string g = Path.Combine(root, "keyfiles");
    Directory.CreateDirectory(g);

    Check(28, "keyfiles: stream sources mix exactly like native keyfile paths", () =>
    {
        string kfDir = Path.Combine(g, "kfs");
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
        const int fastPim = 1;

        // create with paths -> open with streams
        string p = Path.Combine(g, "kfs.hc");
        using (var pw = SecurePassword.FromText("hunter2"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = 2L * 1024 * 1024, Password = pw,
                  KeyFiles = paths, Kdf = fastKdf, Pim = fastPim, Quick = true });
        using (var pw = SecurePassword.FromText("hunter2"))
        using (var vol = Volume.Open(new VolumeOpenOptions
            { Path = p, Password = pw, KeyFileStreams = sources(), Kdf = fastKdf, Pim = fastPim }))
            Log($"       opened a path-created volume with stream keyfiles ({vol.DataSize} B)");

        // create with streams -> open with paths (the reverse differential)
        using (var pw = SecurePassword.FromText("hunter2"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = 2L * 1024 * 1024, Password = pw,
                  KeyFileStreams = sources(), Kdf = fastKdf, Pim = fastPim, Quick = true });
        using (var pw = SecurePassword.FromText("hunter2"))
        using (var vol = Volume.Open(new VolumeOpenOptions
            { Path = p, Password = pw, KeyFiles = paths, Kdf = fastKdf, Pim = fastPim }))
            Log("       opened a stream-created volume with keyfile paths");

        // combined: one path + two streams, mixed in this library
        using (var pw = SecurePassword.FromText("hunter2"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = 2L * 1024 * 1024, Password = pw,
                  KeyFiles = new[] { kf1 }, KeyFileStreams = sources().Skip(1).ToList(),
                  Kdf = fastKdf, Pim = fastPim, Quick = true });
        using (var pw = SecurePassword.FromText("hunter2"))
        using (var vol = Volume.Open(new VolumeOpenOptions
            { Path = p, Password = pw, KeyFiles = paths, Kdf = fastKdf, Pim = fastPim }))
            Log("       combined path+stream mixing matches the pure-path order");

        // wrong keyfile set -> rejected
        using (var pw = SecurePassword.FromText("hunter2"))
        {
            var partialSources = new List<Stream> { new MemoryStream(File.ReadAllBytes(kf1)) };
            try
            {
                using var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, KeyFileStreams = partialSources, Kdf = fastKdf, Pim = fastPim });
                throw new Exception("a wrong keyfile set was accepted");
            }
            catch (VcWrongPasswordException) { Log("       wrong keyfile set rejected"); }
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
            catch (VcArgumentException) { Log("       empty keyfile stream rejected"); }
        }

        // rotate credentials with stream keyfiles on the old side
        using (var pw = SecurePassword.FromText("hunter2"))
            Volume.ChangePassword(new ChangePasswordOptions
            {
                Path = p, OldPassword = pw, OldPim = fastPim, OldKeyFileStreams = sources(),
                NewPassword = SecurePassword.FromText("plain-new"), NewPim = fastPim,
            });
        using (var pw = SecurePassword.FromText("plain-new"))
        using (var vol = Volume.Open(new VolumeOpenOptions
            { Path = p, Password = pw, Kdf = fastKdf, Pim = fastPim }))
            Log("       rotated from stream keyfiles to a plain password");

        // the same stream instances back a second operation (seekable streams
        // are rewound to the start automatically)
        var shared = sources();
        using (var pw = SecurePassword.FromText("hunter2"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = 2L * 1024 * 1024, Password = pw,
                  KeyFileStreams = shared, Kdf = fastKdf, Pim = fastPim, Quick = true });
        using (var pw = SecurePassword.FromText("hunter2"))
        using (var vol = Volume.Open(new VolumeOpenOptions
            { Path = p, Password = pw, KeyFileStreams = shared, Kdf = fastKdf, Pim = fastPim }))
            Log("       reused the same stream instances for create and open");

        // a disposed stream fails with an actionable message
        using (var pw = SecurePassword.FromText("hunter2"))
        {
            var dead = new MemoryStream(File.ReadAllBytes(kf1));
            dead.Dispose();
            try
            {
                using var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, KeyFileStreams = new List<Stream> { dead } });
                throw new Exception("a disposed keyfile stream was accepted");
            }
            catch (VcArgumentException) { Log("       disposed keyfile stream rejected"); }
        }
    });
});

// ---------------------------------------------------------- path handling
Scenario("path handling", () =>
{
    string g = Path.Combine(root, "paths");
    Directory.CreateDirectory(g);

    Check(29, "paths: relative, forward slashes, optional leading separator (FAT + exFAT)", () =>
    {
        foreach (var fs in new[] { VcFilesystem.Fat, VcFilesystem.ExFat })
        {
            string p = Path.Combine(g, $"paths-{fs}.hc");
            using (var pw = SecurePassword.FromText("path-pw"))
                Volume.Create(new VolumeCreationOptions
                    { Path = p, SizeBytes = 4L * 1024 * 1024, Password = pw,
                      Filesystem = fs, Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });

            using (var pw = SecurePassword.FromText("path-pw"))
            using (var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
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
            Log($"       {fs}: relative / forward-slash / no-root forms all work");
        }
    });
});

// ---------------------------------------------------------- import/export
Scenario("import/export", () =>
{
    string g = Path.Combine(root, "tree");
    Directory.CreateDirectory(g);

    Check(30, "import/export: directory round-trip (FAT + exFAT)", () =>
    {
        string src = Path.Combine(g, "tree");
        Directory.CreateDirectory(Path.Combine(src, "data", "sub"));
        byte[] big = RandomBytes(64 * 1024);
        File.WriteAllBytes(Path.Combine(src, "readme.md"), "readme"u8.ToArray());
        File.WriteAllBytes(Path.Combine(src, "data", "sub", "a.bin"), big);
        File.WriteAllBytes(Path.Combine(src, "data", "sub", "\u6d4b\u8bd5\u6587\u4ef6.txt"), "unicode"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(src, "empty-dir"));

        foreach (var fs in new[] { VcFilesystem.Fat, VcFilesystem.ExFat })
        {
            string p = Path.Combine(g, $"tree-{fs}.hc");
            using (var pw = SecurePassword.FromText("tree-pw"))
                Volume.Create(new VolumeCreationOptions
                    { Path = p, SizeBytes = 8L * 1024 * 1024, Password = pw,
                      Filesystem = fs, Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });

            using (var pw = SecurePassword.FromText("tree-pw"))
            using (var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
            using (var f = VolumeFilesystem.Mount(vol))
            {
                // single file, default target (volume root)
                f.ImportFile(Path.Combine(src, "readme.md"));
                if (Encoding.UTF8.GetString(f.ReadFile("\\readme.md")) != "readme") throw new Exception("ImportFile default path failed");

                // whole tree
                f.ImportDirectory(src, "\\backup");
                f.ExportFile("backup/data/sub/a.bin", Path.Combine(g, "exported-single.bin"));
                if (!File.ReadAllBytes(Path.Combine(g, "exported-single.bin")).SequenceEqual(big))
                    throw new Exception("ExportFile content mismatch");

                string outDir = Path.Combine(g, $"out-{fs}");
                f.ExportDirectory("\\backup", outDir);
                if (!File.ReadAllBytes(Path.Combine(outDir, "readme.md")).SequenceEqual("readme"u8.ToArray())) throw new Exception("export readme mismatch");
                if (!File.ReadAllBytes(Path.Combine(outDir, "data", "sub", "a.bin")).SequenceEqual(big)) throw new Exception("export a.bin mismatch");
                if (Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(outDir, "data", "sub", "\u6d4b\u8bd5\u6587\u4ef6.txt"))) != "unicode") throw new Exception("export unicode mismatch");
                if (!Directory.Exists(Path.Combine(outDir, "empty-dir"))) throw new Exception("export kept the empty directory");

                var listed = f.ListDirectory("\\backup\\data\\sub").Select(e => e.Name).OrderBy(n => n).ToList();
                if (!listed.Contains("a.bin") || !listed.Contains("\u6d4b\u8bd5\u6587\u4ef6.txt")) throw new Exception("volume listing after import incomplete");
            }
            Log($"       {fs}: import/export round-trip byte-identical (incl. unicode, empty dir)");
        }
    });
});

Scenario("async filesystem operations", () =>
{
    string g = Path.Combine(root, "asyncfs");
    Directory.CreateDirectory(g);

    Check(31, "async: filesystem operations as tasks", () =>
    {
        string local = Path.Combine(g, "asyncfile.bin");
        File.WriteAllBytes(local, RandomBytes(64 * 1024));

        string p = Path.Combine(g, "asyncfs.hc");
        using (var pw = SecurePassword.FromText("async-fs"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = 2L * 1024 * 1024, Password = pw,
                  Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });

        using (var pw = SecurePassword.FromText("async-fs"))
        using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
        using (var f = VolumeFilesystem.Mount(vol))
        {
            f.WriteFileAsync("blob.bin", File.ReadAllBytes(local)).GetAwaiter().GetResult();
            byte[] back = f.ReadFileAsync("blob.bin").GetAwaiter().GetResult();
            if (back.Length != 64 * 1024) throw new Exception("async write/read size mismatch");

            f.ImportFileAsync(local, "again.bin").GetAwaiter().GetResult();
            var names = f.ListDirectoryAsync("").GetAwaiter().GetResult().Select(e => e.Name).OrderBy(n => n).ToList();
            if (!names.SequenceEqual(new[] { "again.bin", "blob.bin" })) throw new Exception($"async listing wrong: {string.Join(",", names)}");

            f.ExportFileAsync("again.bin", Path.Combine(g, "async-out.bin")).GetAwaiter().GetResult();
            if (!File.ReadAllBytes(Path.Combine(g, "async-out.bin")).SequenceEqual(File.ReadAllBytes(local)))
                throw new Exception("async export content mismatch");
        }
        Log("       async write/read/list/import/export round-trip OK");
    });
});

Scenario("streaming IO", () =>
{
    string g = Path.Combine(root, "streaming");
    Directory.CreateDirectory(g);

    Check(32, "streaming: random access, holes, append, chunked (FAT + exFAT)", () =>
    {
        foreach (var fs in new[] { VcFilesystem.Fat, VcFilesystem.ExFat })
        {
            string p = Path.Combine(g, $"stream-{fs}.hc");
            using (var pw = SecurePassword.FromText("stream-pw"))
                Volume.Create(new VolumeCreationOptions
                    { Path = p, SizeBytes = 4L * 1024 * 1024, Password = pw,
                      Filesystem = fs, Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });

            using (var pw = SecurePassword.FromText("stream-pw"))
            using (var vol = Volume.Open(new VolumeOpenOptions
                    { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
            using (var f = VolumeFilesystem.Mount(vol))
            {
                byte[] pattern = RandomBytes(200_000);
                f.WriteFile("data.bin", pattern);

                using (var s = f.OpenFile("data.bin", VcFileMode.Read))
                {
                    if (s.CanWrite) throw new Exception("read stream claims CanWrite");
                    if (s.Length != 200_000) throw new Exception($"unexpected length {s.Length}");
                    var probe = new byte[64];
                    s.Seek(100_003, SeekOrigin.Begin);
                    if (s.Read(probe, 0, 64) != 64) throw new Exception("short read");
                    if (!probe.SequenceEqual(pattern.AsSpan(100_003, 64).ToArray())) throw new Exception("content mismatch after seek");
                    s.Seek(-64, SeekOrigin.End);
                    if (s.Read(probe, 0, 64) != 64) throw new Exception("short read at end");
                    if (!probe.SequenceEqual(pattern[^64..])) throw new Exception("content mismatch at end");
                    if (s.Read(probe, 0, 64) != 0) throw new Exception("read past EOF returned data");
                }

                using (var s = f.OpenFile("patch.bin", VcFileMode.Create))
                {
                    s.Write("hello"u8.ToArray(), 0, 5);
                    s.Seek(10, SeekOrigin.Begin);
                    s.Write("WORLD"u8.ToArray(), 0, 5);
                    if (s.Length != 15) throw new Exception($"hole write length {s.Length}");
                    var tail = new byte[9];                    // reads at a buffer offset, too
                    s.Seek(4, SeekOrigin.Begin);
                    if (s.Read(tail, 2, 7) != 7) throw new Exception("offset read failed");
                }

                var expect = new byte[15];
                "hello"u8.CopyTo(expect);
                "WORLD"u8.CopyTo(expect.AsSpan(10));
                if (!f.ReadFile("patch.bin").SequenceEqual(expect)) throw new Exception("hole content mismatch");

                using (var s = f.OpenFile("patch.bin", VcFileMode.Append))
                {
                    if (s.Position != 15) throw new Exception($"append starts at {s.Position}");
                    s.Write("!"u8.ToArray(), 0, 1);
                }
                if (!f.ReadFile("patch.bin").SequenceEqual(expect.Append((byte)'!').ToArray()))
                    throw new Exception("append mismatch");

                byte[] big = RandomBytes(300_000);
                using (var s = f.OpenFile("big.bin", VcFileMode.Create))
                    for (int off = 0; off < big.Length; off += 7_000)
                        s.Write(big, off, Math.Min(7_000, big.Length - off));
                using (var s = f.OpenFile("big.bin", VcFileMode.Read))
                {
                    var probe = new byte[9_999];
                    s.Seek(123_457, SeekOrigin.Begin);
                    if (s.Read(probe, 0, 9_999) != 9_999
                        || !probe.SequenceEqual(big.AsSpan(123_457, 9_999).ToArray()))
                        throw new Exception("chunked content mismatch");
                }
            }
            Log($"       {fs}: seek / holes / append / chunked all correct");
        }
    });
});

Scenario("in-place format", () =>
{
    string g = Path.Combine(root, "format");
    Directory.CreateDirectory(g);

    Check(33, "exFAT format: reformat an open volume in place", () =>
    {
        string p = Path.Combine(g, "refmt.hc");
        using (var pw = SecurePassword.FromText("refmt"))
            Volume.Create(new VolumeCreationOptions
                { Path = p, SizeBytes = 4L * 1024 * 1024, Password = pw,
                  Filesystem = VcFilesystem.Fat, Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });
        using (var pw = SecurePassword.FromText("refmt"))
        using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
        {
            using (var f = VolumeFilesystem.Mount(vol))
                f.WriteFile("old.txt", "gone"u8.ToArray());
            VolumeFilesystem.FormatExFat(vol);
            using (var f = VolumeFilesystem.Mount(vol))
            {
                if (f.ListDirectory("").Count != 0) throw new Exception("reformatted volume not empty");
                f.WriteFile("new.txt", "exfat now"u8.ToArray());
            }
        }
        using (var pw = SecurePassword.FromText("refmt"))
        using (var vol = Volume.Open(new VolumeOpenOptions { Path = p, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
        using (var f = VolumeFilesystem.Mount(vol))
            if (Encoding.UTF8.GetString(f.ReadFile("new.txt")) != "exfat now")
                throw new Exception("content lost across reopen");

        string q = Path.Combine(g, "none2exfat.hc");
        using (var pw = SecurePassword.FromText("refmt"))
            Volume.Create(new VolumeCreationOptions
                { Path = q, SizeBytes = 3L * 1024 * 1024, Password = pw,
                  Filesystem = VcFilesystem.None, Kdf = VcKdf.HmacSha512, Pim = 1, Quick = true });
        using (var pw = SecurePassword.FromText("refmt"))
        using (var vol = Volume.Open(new VolumeOpenOptions { Path = q, Password = pw, Kdf = VcKdf.HmacSha512, Pim = 1 }))
        {
            VolumeFilesystem.FormatExFat(vol);
            using var f = VolumeFilesystem.Mount(vol);
            f.WriteFile("fresh.txt", "ok"u8.ToArray());
        }
        Log("       FAT->exFAT and None->exFAT in-place formats work");
    });
});

// ------------------------------------------------------------------- run all
bool serial = Environment.GetEnvironmentVariable("HCV_TEST_SERIAL") == "1";
int dop = Math.Clamp(Environment.ProcessorCount, 1, scenarios.Count);

var total = Stopwatch.StartNew();
if (serial || dop == 1)
{
    foreach (var (name, run) in scenarios)
    {
        Log($"--- {name}");
        run();
    }
}
else
{
    Log($"running {scenarios.Count} scenarios on {dop} workers");
    Parallel.ForEach(
        scenarios,
        new ParallelOptions { MaxDegreeOfParallelism = dop },
        s => s.Run());
}
total.Stop();

// Keep the work directory only when something failed, so the volumes can be
// inspected; successful runs clean up after themselves (the largest scenario
// alone creates 240 MB of containers, and parallel runs multiply that).
if (failures.IsEmpty)
{
    try { Directory.Delete(root, recursive: true); }
    catch { /* best effort */ }
}
else
{
    Console.WriteLine($"work dir kept for inspection: {root}");
}

// ------------------------------------------------------------------- summary
Console.WriteLine();
if (failures.IsEmpty)
{
    Console.WriteLine($"ALL MANAGED TESTS PASSED  " +
        $"({usedIds.Count} checks, {total.Elapsed.TotalSeconds:0.0}s, " +
        $"{(serial || dop == 1 ? "serial" : $"parallel x{dop}")})");
    return 0;
}
Console.WriteLine($"{failures.Count} FAILURE(S):");
foreach (var (id, what) in failures.OrderBy(f => f.Id))
    Console.WriteLine($"  [{id,2}] {what}");
return 1;

static byte[] RandomBytes(int n)
{
    var b = new byte[n];
    Random.Shared.NextBytes(b);
    return b;
}
