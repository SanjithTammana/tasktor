using Tasktor.Core.Storage;

namespace Tasktor.Agent.Host;

/// <summary>Builds the real agent: the host plus the Windows adapters and every feature. Grows milestone by milestone.</summary>
internal static class Composition
{
    public static AgentHost Create(StoreLayout store)
    {
        var runner = new Core.Plugins.ChildProcessRunner(AppContext.BaseDirectory);
        if (OperatingSystem.IsWindows())
        {
            runner.AfterStart = Windows.Processes.JobObject.ForWorker;
        }

        var host = new AgentHost(new AgentOptions
        {
            Store = store,
            KeyProtection = OperatingSystem.IsWindows() ? Windows.Security.Dpapi.Protection : KeyProtection.None,
            ProcessControl = new Core.Plugins.PortableProcessControl(runner),
        });
        return host;
    }
}
