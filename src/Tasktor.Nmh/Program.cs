using System.IO.Pipes;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Capture;
using Tasktor.Core.Storage;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Nmh;

internal static class Program
{
    private static long s_rejected;
    private static async Task Main(string[] args)
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var pipe = new NamedPipeClientStream(".", PipeName.ForCapture(), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var queue = new MessageQueue(Path.Combine(StoreLayout.Resolve(exeDir: AppContext.BaseDirectory).CaptureDir, "nmh-queue.jsonl"));
        try { await pipe.ConnectAsync(250).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { }
        using var stop = new CancellationTokenSource();
        using var outputLock = new SemaphoreSlim(1, 1);
        var reader = pipe.IsConnected ? ReadPipeAndStopAsync(pipe, output, outputLock, stop) : Task.CompletedTask;
        try
        {
            while (await NativeFraming.ReadAsync(input, stop.Token).ConfigureAwait(false) is { } bytes)
            {
                var message = BrowserValidation.Read(bytes);
                if (message is null)
                {
                    Interlocked.Increment(ref s_rejected);
                    try
                    {
                        using var invalid = JsonDocument.Parse(bytes);
                        if (invalid.RootElement.TryGetProperty("type", out var type) && type.GetString() == "block_request" &&
                            invalid.RootElement.TryGetProperty("request_id", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            BrowserMessage ack = new BrowserAck { At = DateTimeOffset.UtcNow, RequestId = id.GetString()!, Ok = false, Reason = "invalid" };
                            await WriteBrowserAsync(output, outputLock, JsonSerializer.SerializeToUtf8Bytes(ack, ContractsJsonContext.Default.BrowserMessage), stop.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
                    continue;
                }
                var line = JsonSerializer.Serialize(message, ContractsJsonContext.Default.BrowserMessage);
                if (System.Text.Encoding.UTF8.GetByteCount(line) > NativeFraming.MaxMessageBytes) { Interlocked.Increment(ref s_rejected); continue; }
                if (pipe.IsConnected)
                {
                    try { await PipeFraming.WriteAsync(pipe, line, stop.Token).ConfigureAwait(false); continue; }
                    catch (IOException) { }
                }
                if (message is BrowserTab or BrowserFocus or BrowserVisibility or BrowserTabClosed) queue.Append(line);
                if (message is BrowserBlockRequest request)
                {
                    BrowserMessage ack = new BrowserAck { At = DateTimeOffset.UtcNow, RequestId = request.RequestId, Ok = false, Reason = "capture_not_running" };
                    await WriteBrowserAsync(output, outputLock, JsonSerializer.SerializeToUtf8Bytes(ack, ContractsJsonContext.Default.BrowserMessage), stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pipe.DisposeAsync().ConfigureAwait(false);
            try { await reader.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static async Task ReadPipeAndStopAsync(Stream pipe, Stream output, SemaphoreSlim outputLock, CancellationTokenSource stop)
    {
        try { await ReadPipeAsync(pipe, output, outputLock, stop.Token).ConfigureAwait(false); }
        finally { await stop.CancelAsync().ConfigureAwait(false); }
    }

    private static async Task ReadPipeAsync(Stream pipe, Stream output, SemaphoreSlim outputLock, CancellationToken ct)
    {
        var line = new MemoryStream();
        var buffer = new byte[4096];
        var dropping = false;
        try
        {
            int count;
            while ((count = await pipe.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        if (!dropping && line.Length > 0)
                        {
                            var payload = line.ToArray();
                            try
                            {
                                var response = JsonSerializer.Deserialize(payload, ContractsJsonContext.Default.BrowserMessage);
                                if (response is BrowserWelcome or BrowserBlocks or BrowserAck) await WriteBrowserAsync(output, outputLock, payload, ct).ConfigureAwait(false);
                                else Interlocked.Increment(ref s_rejected);
                            }
                            catch (Exception ex) when (ex is JsonException or NotSupportedException) { Interlocked.Increment(ref s_rejected); }
                        }
                        else if (dropping) Interlocked.Increment(ref s_rejected);
                        line.SetLength(0);
                        dropping = false;
                    }
                    else if (!dropping)
                    {
                        if (line.Length == NativeFraming.MaxMessageBytes) { line.SetLength(0); dropping = true; }
                        else line.WriteByte(buffer[i]);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static async Task WriteBrowserAsync(Stream output, SemaphoreSlim gate, byte[] bytes, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await NativeFraming.WriteAsync(output, bytes, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
