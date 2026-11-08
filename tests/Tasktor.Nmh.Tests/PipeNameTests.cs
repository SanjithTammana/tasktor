using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Nmh.Tests;

public sealed class PipeNameTests
{
    [Fact]
    public void CaptureNameExtendsCurrentUserName() => Assert.Equal(PipeName.ForCurrentUser() + "-capture", PipeName.ForCapture());
}
