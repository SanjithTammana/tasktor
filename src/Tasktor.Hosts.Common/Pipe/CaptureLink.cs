using System.IO.Pipes;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Capture;

namespace Tasktor.Hosts.Common.Pipe;

/// <summary>
/// The agent's link to <c>tasktor-capture</c>'s control pipe: connect, send one <see cref="CaptureCommand"/>, read one
/// <see cref="CaptureStatus"/>. Throws <see cref="TimeoutException"/> / <see cref="IOException"/> when the host is not
/// running; the capture card then says so.
/// </summary>
public static class CaptureLink
{
    public static async Task<CaptureStatus> SendAsync(CaptureCommand cmd, CancellationToken ct, string? pipeName = null, int connectMs = 500)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        await using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName.ForCapture() + "-ctl", PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectMs, ct).ConfigureAwait(false);
        await PipeFraming.WriteAsync(pipe, JsonSerializer.Serialize(cmd, ContractsJsonContext.Default.CaptureCommand), ct).ConfigureAwait(false);
        var line = await new PipeFraming.LineReader(pipe).ReadLineAsync(ct).ConfigureAwait(false) ?? throw new IOException("the capture host closed the link");
        return JsonSerializer.Deserialize(line, ContractsJsonContext.Default.CaptureStatus) ?? throw new IOException("empty status");
    }
}
