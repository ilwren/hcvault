using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HCVault.Core;

/// <summary>
///   A password held as pinned, wipe-on-dispose bytes. The CLR string type is
///   deliberately avoided: string contents cannot be erased deterministically.
///   Construct from text with <see cref="FromText" /> or from raw bytes.
/// </summary>
public sealed class SecurePassword : IDisposable
{
    private byte[] _bytes;
    private GCHandle _pin;
    private bool _disposed;

    private SecurePassword(byte[] bytes)
    {
        _bytes = bytes;
        _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
    }

    public static SecurePassword FromText(string text)
        => new(Encoding.UTF8.GetBytes(text ?? string.Empty));

    public static SecurePassword FromBytes(ReadOnlySpan<byte> bytes)
    {
        var copy = new byte[bytes.Length];
        bytes.CopyTo(copy);
        return new SecurePassword(copy);
    }

    internal ReadOnlySpan<byte> Span => _bytes;

    internal nuint Length => (nuint)_bytes.Length;

    /// <summary>Overwrite the password bytes and unpin them.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(_bytes);
        _pin.Free();
        _bytes = null!;
        _disposed = true;
    }
}
