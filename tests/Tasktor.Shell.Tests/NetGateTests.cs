using Tasktor.Tests.Shared;

namespace Tasktor.Shell.Tests;

/// <summary>G-capture-net also covers the window shell (it reads window titles).</summary>
public sealed class NetGateTests
{
    [Fact]
    public void The_shell_references_and_imports_nothing_that_networks() =>
        NetGate.AssertNoNetwork(Path.Combine(AppContext.BaseDirectory, "tasktor-shell.dll"));
}
