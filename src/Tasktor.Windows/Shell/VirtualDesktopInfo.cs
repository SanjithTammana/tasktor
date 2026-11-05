using System.Runtime.InteropServices;

namespace Tasktor.Windows.Shell;

/// <summary>Public IVirtualDesktopManager only; no desktop enumeration or switching.</summary>
public static unsafe partial class VirtualDesktopInfo
{
    private static readonly Guid ClassId = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    private static readonly Guid InterfaceId = new("a5cd92ff-29be-454c-8d04-d82879fb3f1b");

    public static bool? IsOnCurrent(long hwnd)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!TryCreate(out var instance, out var initialized)) return null;
        try
        {
            var table = *(nint**)instance;
            int result;
            return ((delegate* unmanaged[Stdcall]<nint, nint, int*, int>)table[3])(instance, (nint)hwnd, &result) >= 0 ? result != 0 : null;
        }
        finally { Finish(instance, initialized); }
    }

    public static bool TryBringHere(long hwnd, long ownWindow)
    {
        if (!OperatingSystem.IsWindows() || ownWindow == 0) return false;
        if (!TryCreate(out var instance, out var initialized)) return false;
        try
        {
            var table = *(nint**)instance;
            Guid desktop;
            if (((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int>)table[4])(instance, (nint)ownWindow, &desktop) < 0) return false;
            return ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int>)table[5])(instance, (nint)hwnd, &desktop) >= 0;
        }
        finally { Finish(instance, initialized); }
    }

    private static bool TryCreate(out nint instance, out bool initialized)
    {
        instance = 0;
        var hr = CoInitializeEx(0, 2);
        initialized = hr >= 0;
        if (hr < 0 && hr != unchecked((int)0x80010106)) return false;
        var clsid = ClassId;
        var iid = InterfaceId;
        if (CoCreateInstance(&clsid, 0, 1, &iid, out instance) >= 0 && instance != 0) return true;
        if (initialized) CoUninitialize();
        initialized = false;
        return false;
    }

    private static void Finish(nint instance, bool initialized)
    {
        var table = *(nint**)instance;
        ((delegate* unmanaged[Stdcall]<nint, uint>)table[2])(instance);
        if (initialized) CoUninitialize();
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);
}
