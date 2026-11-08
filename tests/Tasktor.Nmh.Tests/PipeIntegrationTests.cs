using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Tasktor.Nmh.Tests;

public sealed class PipeIntegrationTests
{
    [Fact]
    public async Task NativeMessagesReachCaptureAndWelcomeReturns()
    {
        var name = "tasktor-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name + "-capture", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add(typeof(NativeFraming).Assembly.Location);
        process.StartInfo.Environment["TASKTOR_PIPE"] = name;
        Assert.True(process.Start());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var hello = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"at\":\"2026-01-01T00:00:00Z\",\"contract\":1,\"client\":\"nmh\",\"browser\":\"edge\",\"extension_version\":\"1\"}");
            await NativeFraming.WriteAsync(process.StandardInput.BaseStream, hello, timeout.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token);
            Assert.Contains("\"type\":\"hello\"", line);
            var welcome = "{\"type\":\"welcome\",\"at\":\"2026-01-01T00:00:00Z\",\"contract\":1,\"blocks\":{\"paused\":false,\"excluded_hosts\":[],\"paused_tabs\":[]}}\n";
            await server.WriteAsync(Encoding.UTF8.GetBytes(welcome), timeout.Token);
            await server.FlushAsync(timeout.Token);
            var response = await NativeFraming.ReadAsync(process.StandardOutput.BaseStream, timeout.Token);
            Assert.Contains("\"type\":\"welcome\"", Encoding.UTF8.GetString(response!));
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
