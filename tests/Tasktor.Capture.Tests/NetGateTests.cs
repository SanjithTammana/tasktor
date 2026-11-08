using Tasktor.Tests.Shared;

namespace Tasktor.Capture.Tests;

/// <summary>G-capture-net (U1 D-H11, D-H12): the capture binary holds no network code, so nothing it records can leave the PC by itself.</summary>
public sealed class NetGateTests
{
    [Fact]
    public void The_capture_host_references_and_imports_nothing_that_networks() =>
        NetGate.AssertNoNetwork(Path.Combine(AppContext.BaseDirectory, "tasktor-capture.dll"));
}
