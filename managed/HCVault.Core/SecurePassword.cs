using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HCVault.Core;

/// <summary>
///   A password held as pinned, wipe-on-dispose bytes. The CLR string type is
///   deliberately avoided: string contents cannot be erased deterministically.
///   Construct from text with <see cref="FromText" /> or from raw bytes.
/// </summary>
/// <remarks>
///   Dispose deterministically wipes and unpins. A finalizer backs that up:
///   without it, an instance that was never disposed leaked its
///   <see cref="GCHandle" /> for the life of the process, and because a pinned
///   handle is a GC root the password array could never be collected either.
/// </remarks>
public sealed class SecurePassword : IDisposable
{
    private byte[]? _bytes;
    private GCHandle _pin;
    private bool _disposed;

    private SecurePassword(byte[] bytes)
    {
        _bytes = bytes;
        _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
    }

    /// <summary>Best-effort cleanup for callers that forgot <see cref="Dispose" />.</summary>
    ~SecurePassword() => ReleaseResources();

    public static SecurePassword FromText(string text)
        => new(Encoding.UTF8.GetBytes(text ?? string.Empty));

    public static SecurePassword FromBytes(ReadOnlySpan<byte> bytes)
    {
        var copy = new byte[bytes.Length];
        bytes.CopyTo(copy);
        return new SecurePassword(copy);
    }

    /// <summary>The pinned password bytes; throws after <see cref="Dispose" />.</summary>
    internal ReadOnlySpan<byte> Span =>
        _bytes ?? throw new ObjectDisposedException(nameof(SecurePassword));

    internal nuint Length => _bytes is null
        ? throw new ObjectDisposedException(nameof(SecurePassword))
        : (nuint)_bytes.Length;

    /// <summary>Overwrite the password bytes and unpin them.</summary>
    public void Dispose()
    {
        ReleaseResources();
        GC.SuppressFinalize(this);
    }

    private void ReleaseResources()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_bytes is not null)
        {
            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = null;
        }

        if (_pin.IsAllocated)
            _pin.Free();
    }
}
