using HCVault.Core;

// Native-AOT smoke test for HCVault.Core (see the .csproj for the rationale).
// Mirrors the managed suite's coverage with an emphasis on the interop paths
// that behave differently under AOT: LibraryImport marshaling, the managed
// delegate -> native progress callback, and NativeLibrary.Load-based probing.

var dir = Path.Combine(Path.GetTempPath(), "hcvault-aot-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
string volPath = Path.Combine(dir, "aot.hc");

try
{
    HCVaultLibrary.Initialize();
    HCVaultLibrary.VerifyAlgorithmTables();
    Console.WriteLine($"ciphers: {HCVaultLibrary.SupportedCiphers.Count}, " +
                      $"kdfs: {HCVaultLibrary.SupportedKdfs.Count}");

    string keyfile = Path.Combine(dir, "test.key");
    File.WriteAllBytes(keyfile, RandomBytes(64_000));

    int progressCalls = 0;
    using (var pw = SecurePassword.FromText("aot-smoke-pw!"))
    {
        Volume.Create(new VolumeCreationOptions
        {
            Path = volPath,
            SizeBytes = 8 * 1024 * 1024,
            Password = pw,
            Pim = 10,
            KeyFiles = [keyfile],
            Cipher = VcCipher.Aes,
            Kdf = VcKdf.Argon2id,
            Filesystem = VcFilesystem.ExFat,
            Quick = true,
            Progress = p =>
            {
                progressCalls++;
                if (p.Stage == VcCreationStage.Finished)
                    Console.WriteLine($"  progress: {p.Fraction:P0}");
            },
        });
    }
    Console.WriteLine($"progress callback invocations: {progressCalls}");

    using (var pw = SecurePassword.FromText("aot-smoke-pw!"))
    using (var volume = Volume.Open(new VolumeOpenOptions
    {
        Path = volPath,
        Password = pw,
        Pim = 10,
        KeyFiles = [keyfile],
    }))
    {
        Console.WriteLine($"cipher={volume.CipherName} kdf={volume.KdfName} " +
                          $"sector={volume.SectorSize} data={volume.DataSize}");

        using var stream = volume.OpenStream();
        var probe = new byte[16];
        stream.ReadExactly(probe);
        Console.WriteLine($"first bytes: {Convert.ToHexString(probe)}");

        using var fs = VolumeFilesystem.Mount(volume);   // boot-sector sniff -> exFAT
        fs.WriteFile("\\long-file-name-résumé.txt", "aot smoke"u8.ToArray());
        var back = fs.ReadFile("\\long-file-name-résumé.txt");
        if (back.Length != 9)
            throw new Exception("content mismatch");

        var space = fs.GetSpace();
        Console.WriteLine($"space: {space.TotalBytes} total, {space.FreeBytes} free, " +
                          $"cluster {space.ClusterBytes}");
        foreach (var e in fs.ListDirectory("\\"))
            Console.WriteLine($"  {e.Name}  dir={e.IsDirectory}  {e.SizeBytes} B");
    }
}
finally
{
    try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
}

Console.WriteLine("AOT SMOKE PASSED");
return 0;

static byte[] RandomBytes(int n)
{
    var b = new byte[n];
    Random.Shared.NextBytes(b);
    return b;
}
