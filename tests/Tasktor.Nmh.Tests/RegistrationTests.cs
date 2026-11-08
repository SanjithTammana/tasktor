namespace Tasktor.Nmh.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void The_fixed_id_is_the_one_the_extensions_key_gives()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "extension", "manifest.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "extension", "manifest.json")));
        Assert.Equal(Registration.ExtensionId, Registration.IdFromKey(manifest.RootElement.GetProperty("key").GetString()!));
        Assert.Equal(["chrome-extension://" + Registration.ExtensionId + "/"], Registration.Manifest("host.exe").AllowedOrigins); // the default
    }

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
