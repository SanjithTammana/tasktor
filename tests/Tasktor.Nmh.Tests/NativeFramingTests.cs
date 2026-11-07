using System.Text;

namespace Tasktor.Nmh.Tests;

public sealed class NativeFramingTests
{
    [Fact]
    public async Task RoundTripUsesByteLengthAndLittleEndian()
    {
        using var stream = new MemoryStream();
        var bytes = Encoding.UTF8.GetBytes("{\"x\":\"é\"}");
        await NativeFraming.WriteAsync(stream, bytes);
        Assert.Equal((byte)bytes.Length, stream.ToArray()[0]);
        Assert.Equal(new byte[] { 0, 0, 0 }, stream.ToArray()[1..4]);
        stream.Position = 0;
        Assert.Equal(bytes, await NativeFraming.ReadAsync(stream));
        Assert.Null(await NativeFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task RejectsOversizedAndTruncatedFrames()
    {
        using var oversized = new MemoryStream([1, 0, 16, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeFraming.ReadAsync(oversized));
        using var truncatedHeader = new MemoryStream([1, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeFraming.ReadAsync(truncatedHeader));
        using var truncatedBody = new MemoryStream([2, 0, 0, 0, 1]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeFraming.ReadAsync(truncatedBody));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeFraming.WriteAsync(output, new byte[NativeFraming.MaxMessageBytes + 1]));
        Assert.Equal(0, output.Length);
    }
}
