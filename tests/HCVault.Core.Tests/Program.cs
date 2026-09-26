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
