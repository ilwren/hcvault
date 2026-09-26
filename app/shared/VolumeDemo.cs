using System.Security.Cryptography;
using HCVault.Core;
using HCVault.Core.Fat;

namespace HCVault.Demo;

public enum DemoLogKind
{
    Info,
    Ok,
    Fail,
}

public sealed record DemoLog(DemoLogKind Kind, string Message, DateTime TimeUtc);

/// <summary>
///   Platform-neutral driver for the volume demo: creates a VeraCrypt
///   container, opens it, mounts its filesystem (exFAT / FAT) and exercises
///   file operations inside it.
///   All VeraCrypt work lives here so the exact same code can be exercised
///   from a console on any OS and from the MAUI UI.
///   Not thread-safe: run from one worker thread at a time.
/// </summary>
public sealed class VolumeDemo : IDisposable
{
    private Volume? _volume;
    private IVolumeFileSystem? _fs;

    public string VolumePath { get; }
    public string Password { get; set; } = "demo-passw0rd!";
    public long SizeBytes { get; set; } = 16 * 1024 * 1024;
    public VcFilesystem Filesystem { get; set; } = VcFilesystem.ExFat;
    public VcCipher Cipher { get; set; } = VcCipher.Aes;
    public VcKdf Kdf { get; set; } = VcKdf.Argon2id;
    public bool Quick { get; set; } = true;

    /// <summary>Raised for every log line (from the calling worker thread).</summary>
    public event Action<DemoLog>? Logged;

    public VolumeDemo(string volumePath) => VolumePath = volumePath;

    // -------------------------------------------------------------- state

    public bool VolumeFileExists => File.Exists(VolumePath);

    public long VolumeFileSize => VolumeFileExists ? new FileInfo(VolumePath).Length : 0;

    public bool IsOpen => _volume is { IsOpen: true };

    public bool IsMounted => _fs is not null;

    public string? MountedFilesystem => _fs?.GetType().Name;

    public void Dispose() => Close();

    private void Log(DemoLogKind kind, string message)
    {
        Logged?.Invoke(new DemoLog(kind, message, DateTime.UtcNow));
    }

    private void Info(string message) => Log(DemoLogKind.Info, message);

    private void Ok(string message) => Log(DemoLogKind.Ok, message);

    private void Fail(string message) => Log(DemoLogKind.Fail, message);

    // ----------------------------------------------------------- scripted

    /// <summary>
    ///   Full end-to-end cycle: create → open (wrong password rejected) →
    ///   mount → write → read back → list → overwrite → delete → close →
    ///   reopen → verify persistence. Stops at the first failing step.
    ///   Returns true when every step passed.
    /// </summary>
    public bool RunFullTest()
    {
        int failures = 0;

        void Step(string name, Action body)
        {
            if (failures > 0)
            {
                Log(DemoLogKind.Info, $"skipped: {name}");
                return;
            }
            try
            {
                body();
                Ok(name);
            }
            catch (Exception ex)
            {
                failures++;
                Fail($"{name}: {ex.Message}");
            }
        }

        Close();

        if (VolumeFileExists)
        {
            File.Delete(VolumePath);
            Info("removed previous volume file");
        }

        Step("library self-test + algorithm tables", () => HCVaultLibrary.VerifyAlgorithmTables());

        Step($"create {Filesystem} volume ({SizeBytes / 1024 / 1024} MiB, {Cipher}/{Kdf})", () =>
        {
            CreateVolume();
        });

        Step("wrong password rejected", () =>
        {
            using var pw = SecurePassword.FromText(Password + "wrong");
            try
            {
                using var v = Volume.Open(new VolumeOpenOptions { Path = VolumePath, Password = pw });
                throw new InvalidOperationException("volume opened with a wrong password?!");
            }
            catch (VcWrongPasswordException)
            {
                // expected
            }
        });

        Step("open with the correct password", () =>
        {
            OpenVolume();
            Info($"cipher={_volume!.CipherName} kdf={_volume.KdfName} " +
                 $"sector={_volume.SectorSize} data={_volume.DataSize:N0} B");
        });

        Step("mount filesystem (auto-detect)", () =>
        {
            MountFilesystem();
            Info($"filesystem implementation: {MountedFilesystem}");
        });

        byte[] probe = RandomNumberGenerator.GetBytes(300 * 1024);

        Step("write 300 KiB file + directory + nested unicode file", () =>
        {
            _fs!.WriteFile("\\probe.bin", probe);
            _fs.CreateDirectory("\\Documents");
            _fs.WriteFile("\\Documents\\résumé.md", "nested unicode filename"u8.ToArray());
        });

        Step("read back and byte-compare", () =>
        {
            var back = _fs!.ReadFile("\\probe.bin");
            if (!back.AsSpan().SequenceEqual(probe))
                throw new InvalidOperationException("content mismatch after write");
        });

        Step("list root directory", () =>
        {
            var entries = _fs!.ListDirectory("\\");
            foreach (var e in entries)
                Info($"  {e.Name}  {(e.IsDirectory ? "<dir>" : e.SizeBytes.ToString("N0") + " B")}");
            if (!entries.Any(e => e.Name == "probe.bin"))
                throw new InvalidOperationException("probe.bin not listed");
            if (!entries.Any(e => e.Name == "Documents" && e.IsDirectory))
                throw new InvalidOperationException("Documents directory not listed");
        });

        Step("overwrite shrinks the file", () =>
        {
            _fs!.WriteFile("\\probe.bin", "tiny"u8.ToArray());
            if (_fs.ReadFile("\\probe.bin").Length != 4)
                throw new InvalidOperationException("overwrite did not shrink the file");
        });

        Step("delete nested file + directory", () =>
        {
            _fs!.Delete("\\Documents\\résumé.md");
            _fs.Delete("\\Documents");
        });

        Step("close, reopen from disk and verify persistence", () =>
        {
            Close();
            OpenVolume();
            MountFilesystem();
            var back = _fs!.ReadFile("\\probe.bin");
            if (!back.AsSpan().SequenceEqual("tiny"u8))
                throw new InvalidOperationException("persisted content mismatch after reopen");
            Info("re-opened volume, content intact");
        });

        Step("delete probe file", () => _fs!.Delete("\\probe.bin"));

        Close();

        if (failures == 0)
            Ok("FULL TEST PASSED ✔");
        else
            Fail($"full test FAILED ({failures} step(s))");

        return failures == 0;
    }

    // ------------------------------------------------------------- manual

    public void CreateVolume()
    {
        using var pw = SecurePassword.FromText(Password);
        Volume.Create(new VolumeCreationOptions
        {
            Path = VolumePath,
            SizeBytes = SizeBytes,
            Password = pw,
            Cipher = Cipher,
            Kdf = Kdf,
            Filesystem = Filesystem,
            Quick = Quick,
            Progress = p =>
            {
                if (p.Stage == VcCreationStage.Finished)
                    Info($"creation finished ({p.BytesDone:N0} B written)");
            },
        });
        Info($"volume file on disk: {VolumeFileSize:N0} B");
    }

    public void OpenVolume()
    {
        if (IsOpen)
            throw new InvalidOperationException("a volume is already open — close it first");
        using var pw = SecurePassword.FromText(Password);
        _volume = Volume.Open(new VolumeOpenOptions { Path = VolumePath, Password = pw });
        Info($"open: cipher={_volume.CipherName} kdf={_volume.KdfName} " +
             $"sector={_volume.SectorSize} data={_volume.DataSize:N0} B hidden={_volume.IsHidden}");
    }

    public void MountFilesystem()
    {
        var volume = _volume ?? throw new InvalidOperationException("open the volume first");
        _fs = VolumeFilesystem.Mount(volume);
        Info($"filesystem mounted: {_fs.GetType().Name}");
    }

    public IReadOnlyList<FatEntry> ListDirectory(string path = "\\")
    {
        var fs = _fs ?? throw new InvalidOperationException("mount the filesystem first");
        return fs.ListDirectory(path);
    }

    public void WriteFile(string name, int sizeBytes)
    {
        var fs = _fs ?? throw new InvalidOperationException("mount the filesystem first");
        if (sizeBytes is < 0 or > 512 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        var data = RandomNumberGenerator.GetBytes(sizeBytes);
        fs.WriteFile("\\" + name, data);
        Ok($"wrote \\{name} ({sizeBytes:N0} B, sha256 {Convert.ToHexString(SHA256.HashData(data))[..12].ToLowerInvariant()}…)");
    }

    public byte[] ReadFile(string path)
    {
        var fs = _fs ?? throw new InvalidOperationException("mount the filesystem first");
        var data = fs.ReadFile(path);
        Ok($"read {path} ({data.Length:N0} B, sha256 {Convert.ToHexString(SHA256.HashData(data))[..12].ToLowerInvariant()}…)");
        return data;
    }

    public void DeleteEntry(string path)
    {
        var fs = _fs ?? throw new InvalidOperationException("mount the filesystem first");
        fs.Delete(path);
        Ok($"deleted {path}");
    }

    public void Close()
    {
        bool hadSomething = _fs is not null || _volume is not null;
        _fs?.Dispose();
        _fs = null;
        _volume?.Dispose();
        _volume = null;
        if (hadSomething)
            Info("volume closed");
    }
}
