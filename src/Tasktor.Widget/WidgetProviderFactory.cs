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

[ComVisible(true)]
internal sealed class WidgetProviderFactory : IClassFactory
{
    public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (outer != IntPtr.Zero) return unchecked((int)0x80040110);
        var provider = IntPtr.Zero;
        try
        {
            provider = MarshalInspectable<IWidgetProvider>.FromManaged(new WidgetProvider());
            return Marshal.QueryInterface(provider, in iid, out result);
        }
        catch (Exception e)
        {
            return Marshal.GetHRForException(e);
        }
        finally
        {
            if (provider != IntPtr.Zero) Marshal.Release(provider);
        }
    }

    public int LockServer(bool locked) => 0;
}
