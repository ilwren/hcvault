using HCVault.Core.Interop;

namespace HCVault.Core;

/// <summary>Old and new credentials for re-encrypting a volume header.</summary>
public sealed class ChangePasswordOptions
{
    public required string Path { get; init; }

    public required SecurePassword OldPassword { get; init; }
    public int OldPim { get; init; }
    public IReadOnlyList<string> OldKeyFiles { get; init; } = Array.Empty<string>();

    public required SecurePassword NewPassword { get; init; }
    public int NewPim { get; init; }
    public IReadOnlyList<string> NewKeyFiles { get; init; } = Array.Empty<string>();

    /// <summary>Switch the header KDF; null keeps the volume's current KDF.</summary>
    public VcKdf? NewKdf { get; init; }

    /// <summary>Header wipe passes (1 = default; 0 is treated as 1).</summary>
    public int WipeCount { get; init; } = 1;
}

public sealed partial class Volume
{
    /// <summary>
    ///   Re-encrypt the volume header with new credentials (password / PIM /
    ///   keyfiles / KDF). Works for normal and hidden volumes: the first header
    ///   that opens with the old credentials is the one re-encrypted.
    /// </summary>
    public static void ChangePassword(ChangePasswordOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Path);
        ArgumentNullException.ThrowIfNull(options.OldPassword);
        ArgumentNullException.ThrowIfNull(options.NewPassword);

        HCVaultLibrary.Initialize();

        var oldKeys = options.OldKeyFiles.ToArray();
        var newKeys = options.NewKeyFiles.ToArray();

        int status = NativeMethods.vc_change_password(
            options.Path,
            options.OldPassword.Span, options.OldPassword.Length,
            oldKeys, (nuint)oldKeys.Length,
            options.OldPim,
            options.NewPassword.Span, options.NewPassword.Length,
            newKeys, (nuint)newKeys.Length,
            options.NewPim,
            options.NewKdf?.ToNative(),
            options.WipeCount);

        Status.ThrowIfFailed(status);
    }
}
