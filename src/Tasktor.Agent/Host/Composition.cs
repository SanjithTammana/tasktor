using Tasktor.Core.Storage;

namespace Tasktor.Agent.Host;

/// <summary>Builds the real agent: the host plus the Windows adapters and every feature. Grows milestone by milestone.</summary>
internal static class Composition
{
    public static AgentHost Create(StoreLayout store)
    {
        var host = new AgentHost(new AgentOptions
        {
            Store = store,
            KeyProtection = OperatingSystem.IsWindows() ? Windows.Security.Dpapi.Protection : KeyProtection.None,
        });
        return host;
    }
}
