using System.Runtime.InteropServices;

namespace Tasktor.Widget;

internal static class Program
{
    // This CLSID must match the separately packaged provider registration.
    internal static readonly Guid ProviderClassId = new("EB1FA3BD-4044-4D61-BA86-D13AFC71E2C4");

    private const uint LocalServer = 4, MultipleUse = 1, Suspended = 4; // CLSCTX_LOCAL_SERVER, REGCLS_*

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)] Guid clsid,
        [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoResumeClassObjects();

    [DllImport("ole32.dll")]
    private static extern uint CoAddRefServerProcess();

    [DllImport("ole32.dll")]
    private static extern uint CoReleaseServerProcess();

    [MTAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Runs until the last COM reference is released (ServerLifetime), not until widgets go idle.
        var lifetime = new ServerLifetime(CoAddRefServerProcess, CoReleaseServerProcess);
        var factory = new WidgetProviderFactory(lifetime);
        Marshal.ThrowExceptionForHR(CoRegisterClassObject(ProviderClassId, factory, LocalServer, MultipleUse | Suspended, out var cookie));
        try
        {
            Marshal.ThrowExceptionForHR(CoResumeClassObjects());
            lifetime.Stopped.WaitOne();
        }
        finally
        {
            CoRevokeClassObject(cookie);
            GC.KeepAlive(factory);
        }

        return 0;
    }
}
