using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Hosts.Common.Tests;

public sealed class PipeClientTests
{
    [Fact]
    public async Task Agent_loss_is_caught_by_the_viewers_pipe_failure_handling()
    {
        // The deep window's pulls treat IOException as "try again later"; agent loss must take that path, not escape.
        var missing = "tasktor-test-absent-" + Guid.NewGuid().ToString("N");
        var caught = await Record.ExceptionAsync(async () =>
        {
            try
            {
                await using var client = await PipeClient.ConnectAsync("flyout", missing, 200);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                throw new InvalidDataException("handled", e);
            }
        });

        var handled = Assert.IsType<InvalidDataException>(caught);
        Assert.IsType<AgentNotRunningException>(handled.InnerException);
    }
}
