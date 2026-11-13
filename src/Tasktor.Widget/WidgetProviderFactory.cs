using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace Tasktor.Widget;

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000001-0000-0000-C000-000000000046")]
internal interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result);
    [PreserveSig] int LockServer(bool locked);
}

/// <summary>Creates providers, each holding a creation pin on the server until COM connects to it (<see cref="ServerLifetime"/>).</summary>
[ComVisible(true)]
internal sealed class WidgetProviderFactory(ServerLifetime lifetime) : IClassFactory
{
    private const int NoAggregation = unchecked((int)0x80040110), ServerStopping = unchecked((int)0x80004008);

    public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (outer != IntPtr.Zero) return NoAggregation;
        if (!lifetime.TryPin()) return ServerStopping; // COM retries the activation on a fresh server
        var connections = new ProviderConnections(lifetime);
        var provider = IntPtr.Zero;
        try
        {
            provider = MarshalInspectable<IWidgetProvider>.FromManaged(new WidgetProvider(connections));
            var hr = Marshal.QueryInterface(provider, in iid, out result);
            if (hr < 0) connections.Abandon();
            return hr;
        }
        catch (Exception e)
        {
            connections.Abandon();
            return Marshal.GetHRForException(e);
        }
        finally
        {
            if (provider != IntPtr.Zero) Marshal.Release(provider);
        }
    }

    public int LockServer(bool locked)
    {
        if (!locked)
        {
            lifetime.Unpin();
            return 0;
        }

        return lifetime.TryPin() ? 0 : ServerStopping;
    }
}
