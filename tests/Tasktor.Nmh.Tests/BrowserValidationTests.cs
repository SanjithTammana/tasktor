using System.Text;
using Tasktor.Contracts.Capture;

namespace Tasktor.Nmh.Tests;

public sealed class BrowserValidationTests
{
    [Theory]
    [InlineData("example.com", true)]
    [InlineData("example.com/path", false)]
    [InlineData("example.com:443", false)]
    [InlineData("Example.com", false)]
    public void HostIsBareAndLowercase(string host, bool valid) => Assert.Equal(valid, BrowserValidation.ValidHost(host));

    [Fact]
    public void RejectsUnknownTypeAndExtraPersonalData()
    {
        Assert.Null(BrowserValidation.Read(Encoding.UTF8.GetBytes("{\"type\":\"other\"}")));
        Assert.Null(BrowserValidation.Read(Encoding.UTF8.GetBytes("{\"type\":\"focus\",\"at\":\"2026-01-01T00:00:00Z\",\"tab\":1,\"field\":\"text\",\"value\":\"secret\"}")));
        Assert.IsType<BrowserFocus>(BrowserValidation.Read(Encoding.UTF8.GetBytes("{\"type\":\"focus\",\"at\":\"2026-01-01T00:00:00Z\",\"tab\":1,\"field\":\"secure\"}")));
    }
}
