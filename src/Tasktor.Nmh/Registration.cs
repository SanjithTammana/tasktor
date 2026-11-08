using System.Runtime.Versioning;
using System.Security.Cryptography;
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

    /// <summary>
    /// The companion's id, fixed by the public <c>key</c> in <c>extension/manifest.json</c> (Chromium derives it from the key,
    /// so the unpacked extension has this id wherever it is loaded from). Only this origin may start the host.
    /// </summary>
    public const string ExtensionId = "ompmmjcnglejigijghnlgcmdceaknbam";

    /// <summary>Chromium's extension id for a public key: the first 128 bits of SHA-256 over the DER key, as letters a–p.</summary>
    public static string IdFromKey(string base64Key)
    {
        var hash = SHA256.HashData(Convert.FromBase64String(base64Key));
        return string.Concat(hash.AsSpan(0, 16).ToArray().Select(b => $"{(char)('a' + (b >> 4))}{(char)('a' + (b & 15))}"));
    }

    public static NativeHostManifest Manifest(string executablePath, string extensionId = ExtensionId) =>
        new(HostName, "Tasktor browser companion", executablePath, "stdio", ["chrome-extension://" + extensionId + "/"]);

    public static string ManifestJson(string executablePath, string extensionId = ExtensionId) =>
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

    /// <summary>
    /// <c>tasktor-nmh --register</c>: writes the host manifest (this exe, the companion's id only) into <paramref name="dir"/>
    /// and points Chrome's and Edge's per-user keys at it. No admin rights; nothing outside HKCU and the data folder.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string Register(string executablePath, string dir)
    {
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, HostName + ".json");
        File.WriteAllText(path, ManifestJson(executablePath));
        WriteCurrentUser(Entries(path, path));
        return path;
    }

    /// <summary><c>tasktor-nmh --unregister</c>: removes both keys (the manifest file goes with the data folder).</summary>
    [SupportedOSPlatform("windows")]
    public static void Unregister()
    {
        foreach (var entry in Entries(string.Empty, string.Empty))
        {
            Registry.CurrentUser.DeleteSubKeyTree(entry.Key, throwOnMissingSubKey: false);
        }
    }
}
