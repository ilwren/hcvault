using HCVault.Core.Interop;

namespace HCVault.Core;

/// <summary>Old and new credentials for re-encrypting a volume header.</summary>
public sealed class ChangePasswordOptions
{
    public required string Path { get; init; }

    public required SecurePassword OldPassword { get; init; }
    public int OldPim { get; init; }
    public IReadOnlyList<string> OldKeyFiles { get; init; } = Array.Empty<string>();

    /// <summary>Keyfile contents for the OLD credentials (see VolumeCreationOptions.KeyFileStreams).</summary>
    public IReadOnlyList<Stream>? OldKeyFileStreams { get; init; }

    public required SecurePassword NewPassword { get; init; }
    public int NewPim { get; init; }
    public IReadOnlyList<string> NewKeyFiles { get; init; } = Array.Empty<string>();

    /// <summary>Keyfile contents for the NEW credentials (see VolumeCreationOptions.KeyFileStreams).</summary>
    public IReadOnlyList<Stream>? NewKeyFileStreams { get; init; }

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

        SecurePassword? mixedOld = null;
        if (options.OldKeyFileStreams is { Count: > 0 })
        {
            mixedOld = KeyfileMixer.Apply(options.OldPassword, options.OldKeyFiles, options.OldKeyFileStreams);
            oldKeys = Array.Empty<string>();
        }
        SecurePassword? mixedNew = null;
        if (options.NewKeyFileStreams is { Count: > 0 })
        {
            mixedNew = KeyfileMixer.Apply(options.NewPassword, options.NewKeyFiles, options.NewKeyFileStreams);
            newKeys = Array.Empty<string>();
        }

        try
        {
            SecurePassword oldPassword = mixedOld ?? options.OldPassword;
            SecurePassword newPassword = mixedNew ?? options.NewPassword;
            int status = NativeMethods.vc_change_password(
                options.Path,
                oldPassword.Span, oldPassword.Length,
                oldKeys, (nuint)oldKeys.Length,
                options.OldPim,
                newPassword.Span, newPassword.Length,
                newKeys, (nuint)newKeys.Length,
                options.NewPim,
                options.NewKdf?.ToNative(),
                options.WipeCount);

            Status.ThrowIfFailed(status);
        }
        finally
        {
            mixedOld?.Dispose();
            mixedNew?.Dispose();
        }
    }

    /// <summary>
    ///   <see cref="ChangePassword"/> as a thread-pool task (re-derives the
    ///   header key). The native call is not cancellable - the token only
    ///   prevents a run that has not started yet. Close open handles on the
    ///   volume first; the operation opens the file exclusively.
    /// </summary>
    public static Task ChangePasswordAsync(ChangePasswordOptions options, CancellationToken cancellationToken = default)
        => Task.Run(() => ChangePassword(options), cancellationToken);
}
