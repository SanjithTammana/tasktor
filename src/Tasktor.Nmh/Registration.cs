using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Tasktor.Nmh;

public sealed record NativeHostManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("allowed_origins")] string[] AllowedOrigins);

public sealed record RegistryEntry(string Key, string ManifestPath);

[JsonSerializable(typeof(NativeHostManifest))]
internal sealed partial class RegistrationJsonContext : JsonSerializerContext;

public static class Registration
{
    public const string HostName = "com.tasktor.nmh";
    public const string ExtensionIdPlaceholder = "<extension-id>";

    public static NativeHostManifest Manifest(string executablePath, string extensionId = ExtensionIdPlaceholder) =>
        new(HostName, "Tasktor browser companion", executablePath, "stdio", ["chrome-extension://" + extensionId + "/"]);

    public static string ManifestJson(string executablePath, string extensionId = ExtensionIdPlaceholder) =>
        JsonSerializer.Serialize(Manifest(executablePath, extensionId), RegistrationJsonContext.Default.NativeHostManifest);

    public static RegistryEntry[] Entries(string chromeManifestPath, string edgeManifestPath) =>
    [
        new(@"Software\Google\Chrome\NativeMessagingHosts\" + HostName, chromeManifestPath),
        new(@"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName, edgeManifestPath),
    ];

    [SupportedOSPlatform("windows")]
    public static void WriteCurrentUser(IEnumerable<RegistryEntry> entries)
    {
        foreach (var entry in entries)
        {
            using var key = Registry.CurrentUser.CreateSubKey(entry.Key);
            key.SetValue(null, entry.ManifestPath);
        }
    }
}
