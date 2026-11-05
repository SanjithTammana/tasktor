namespace Tasktor.Nmh.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void RegistrationIsPerUserAndUsesOnlyTheSpecifiedExtension()
    {
        var manifest = Registration.Manifest("host.exe", "abcdefghijklmnopabcdefghijklmnop");
        Assert.Equal("com.tasktor.nmh", manifest.Name);
        Assert.Equal(["chrome-extension://abcdefghijklmnopabcdefghijklmnop/"], manifest.AllowedOrigins);
        var entries = Registration.Entries("chrome.json", "edge.json");
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry => Assert.Contains("NativeMessagingHosts", entry.Key));
    }
}
