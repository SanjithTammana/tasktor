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

    private static BrowserMessage? Read(string json) => BrowserValidation.Read(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Hello_and_tab_carry_the_private_window_flag()
    {
        var hello = Assert.IsType<BrowserHello>(Read("{\"type\":\"hello\",\"at\":\"2026-01-01T00:00:00Z\",\"contract\":1,\"client\":\"nmh\",\"browser\":\"edge\",\"extension_version\":\"1.0.0\",\"incognito\":true}"));
        Assert.True(hello.Incognito);
        var tab = Assert.IsType<BrowserTab>(Read("{\"type\":\"tab\",\"at\":\"2026-01-01T00:00:00Z\",\"tab\":1,\"window\":1,\"active\":true,\"audible\":false}"));
        Assert.False(tab.Incognito); // absent = a normal tab
        Assert.Null(Read("{\"type\":\"tab\",\"at\":\"2026-01-01T00:00:00Z\",\"tab\":1,\"window\":1,\"active\":true,\"audible\":false,\"incognito\":\"yes\"}"));
    }

    [Fact]
    public void A_tab_may_carry_its_full_address_only_on_its_own_http_host()
    {
        const string head = "{\"type\":\"tab\",\"at\":\"2026-01-01T00:00:00Z\",\"tab\":1,\"window\":1,\"active\":true,\"audible\":false";
        var ok = Assert.IsType<BrowserTab>(Read(head + ",\"host\":\"example.com\",\"url\":\"https://example.com/a?b=1\",\"title\":\"A page\"}"));
        Assert.Equal(("https://example.com/a?b=1", "A page"), (ok.Url, ok.Title));
        Assert.Null(Read(head + ",\"host\":\"example.com\",\"url\":\"https://other.example/a\"}")); // the address must match the host
        Assert.Null(Read(head + ",\"host\":\"example.com\",\"url\":\"file:///c:/secret.txt\"}"));
        Assert.Null(Read(head + ",\"url\":\"https://example.com/\"}")); // no host, no address
        Assert.Null(Read(head + ",\"host\":\"example.com\",\"title\":\"" + new string('t', 301) + "\"}"));
    }

    [Fact]
    public void Page_text_and_media_are_capped()
    {
        const string head = "{\"type\":\"text\",\"at\":\"2026-01-01T00:00:00Z\",\"tab\":3,\"lines\":";
        Assert.Equal(["Heading", "Body line"], Assert.IsType<BrowserText>(Read(head + "[\"Heading\",\"Body line\"]}")).Lines);
        Assert.Null(Read(head + "[\"" + new string('x', BrowserText.MaxLine + 1) + "\"]}"));
        Assert.Null(Read(head + "[" + string.Join(",", Enumerable.Repeat("\"" + new string('x', 400) + "\"", 21)) + "]}")); // > 8 KB
        Assert.Null(Read(head + "[" + string.Join(",", Enumerable.Repeat("\"x\"", BrowserText.MaxLines + 1)) + "]}"));
        Assert.Null(Read(head + "[\"a\"],\"value\":\"typed\"}")); // no extra fields

        var media = Assert.IsType<BrowserMedia>(Read("{\"type\":\"media\",\"at\":\"2026-01-01T00:00:00Z\",\"tab\":3,\"playing\":true,\"position_s\":42.5}"));
        Assert.Equal((true, 42.5), (media.Playing, media.PositionS));
        Assert.Null(Read("{\"type\":\"media\",\"at\":\"2026-01-01T00:00:00Z\",\"tab\":3,\"playing\":true,\"position_s\":-1}"));
    }
}
