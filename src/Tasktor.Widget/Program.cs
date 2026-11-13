using System.Runtime.InteropServices;

namespace Tasktor.Widget;

internal static class Program
{
    // This CLSID must match the separately packaged provider registration.
    internal static readonly Guid ProviderClassId = new("EB1FA3BD-4044-4D61-BA86-D13AFC71E2C4");

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)] Guid clsid,
        [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);

    [MTAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var factory = new WidgetProviderFactory();
        Marshal.ThrowExceptionForHR(CoRegisterClassObject(ProviderClassId, factory, 4, 1, out var cookie));
        try
        {
            // Keep COM objects valid until the host terminates the server. A safe idle exit
            // requires tracking outstanding COM references, not just active widgets.
            using var shutdown = new ManualResetEvent(false);
            shutdown.WaitOne();
        }
        finally
        {
            CoRevokeClassObject(cookie);
            GC.KeepAlive(factory);
        }
        return 0;
    }
}
