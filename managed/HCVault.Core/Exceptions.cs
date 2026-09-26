using HCVault.Core.Interop;

namespace HCVault.Core;

/// <summary>Base exception for all HCVault.Core errors.</summary>
public class VcException : Exception
{
    internal VcException(string message) : base(message) { }
}

/// <summary>The password / keyfile / PIM combination was rejected by the volume header.</summary>
public class VcWrongPasswordException : VcException
{
    internal VcWrongPasswordException(string message) : base(message) { }
}

/// <summary>An argument or the volume state was invalid.</summary>
public class VcArgumentException : VcException
{
    internal VcArgumentException(string message) : base(message) { }
}

/// <summary>A volume feature is not supported by this build/platform.</summary>
public class VcUnsupportedException : VcException
{
    internal VcUnsupportedException(string message) : base(message) { }
}

internal static class Status
{
    internal const int Ok = 0;
    internal const int Generic = 1;
    internal const int WrongPassword = 2;
    internal const int Arg = 3;
    internal const int VolumeNotFound = 4;
    internal const int Unsupported = 5;

    internal static Exception ToException(int status)
    {
        string message = NativeMethods.vc_last_error() != IntPtr.Zero
            ? NativeLoader.StringFromIntPtr(NativeMethods.vc_last_error())
            : $"vcapi error {status}";

        return status switch
        {
            WrongPassword => new VcWrongPasswordException(message),
            Arg => new VcArgumentException(message),
            Unsupported => new VcUnsupportedException(message),
            _ => new VcException(message),
        };
    }

    internal static void ThrowIfFailed(int status)
    {
        if (status != Ok)
            throw ToException(status);
    }
}
