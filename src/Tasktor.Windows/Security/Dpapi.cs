using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Tasktor.Core.Storage;

namespace Tasktor.Windows.Security;

/// <summary>DPAPI, current-user scope (U1 D-H8: the install key). P/Invoke source generation; no reflection.</summary>
public static partial class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static KeyProtection Protection { get; } = new(Protect, Unprotect, "dpapi-user");

    public static byte[] Protect(byte[] data) => Run(data, protect: true);

    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static unsafe byte[] Run(byte[] data, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is Windows-only");
        }

        fixed (byte* p = data)
        {
            var input = new DataBlob { Size = (uint)data.Length, Data = (nint)p };
            var output = default(DataBlob);
            var ok = protect
                ? CryptProtectData(ref input, null, 0, 0, 0, CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, CryptProtectUiForbidden, ref output);
            if (!ok)
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, (int)output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, string? description, nint entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, nint description, nint entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);
}
