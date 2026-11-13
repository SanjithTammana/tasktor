using System.Runtime.InteropServices;

namespace Tasktor.Widget;

/// <summary>COM's hook for out-of-process servers: the stub manager reports each strong connection made and released.</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000019-0000-0000-C000-000000000046")]
internal interface IExternalConnection
{
    [PreserveSig] uint AddConnection(uint extconn, uint reserved);

    [PreserveSig] uint ReleaseConnection(uint extconn, uint reserved, [MarshalAs(UnmanagedType.Bool)] bool lastReleaseCloses);
}

/// <summary>The provider's <see cref="IExternalConnection"/>, reached through its <c>ICustomQueryInterface</c>.</summary>
[ComVisible(true)]
internal sealed class ExternalConnection(ProviderConnections connections) : IExternalConnection
{
    public static readonly Guid Iid = typeof(IExternalConnection).GUID;

    public uint AddConnection(uint extconn, uint reserved) => connections.AddConnection(extconn);

    public uint ReleaseConnection(uint extconn, uint reserved, bool lastReleaseCloses) => connections.ReleaseConnection(extconn);
}
