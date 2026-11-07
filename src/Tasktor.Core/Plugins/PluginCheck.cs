using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Plugins;

namespace Tasktor.Core.Plugins;

/// <summary>
/// For plugin authors (<c>tasktor plugin check</c>): the same manifest and output checks the agent applies (U1 D-H7), the
/// facts its consent dialog will show, and the command exactly as the agent resolves it, all without installing the plugin.
/// The agent's <see cref="PluginHost"/> resolves commands through here too, so the two cannot drift.
/// </summary>
public static class PluginCheck
{
    /// <summary>The parsed manifest, why it cannot load (null when it can), and the consent dialog's facts.</summary>
    public sealed record ManifestResult(PluginManifest? Manifest, string? Problem, IReadOnlyList<string> Consent);

    public static ManifestResult Manifest(string folder, Version tasktorVersion, string? python = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var path = Path.Combine(folder, "plugin.json");
        if (!File.Exists(path))
        {
            return new ManifestResult(null, "no plugin.json in " + folder, []);
        }

        PluginManifest? m;
        try
        {
            m = JsonSerializer.Deserialize(File.ReadAllBytes(path), ContractsJsonContext.Default.PluginManifest);
        }
        catch (JsonException ex)
        {
            return new ManifestResult(null, "plugin.json: " + ex.Message.Split('\n')[0], []);
        }

        if (m is null)
        {
            return new ManifestResult(null, "empty plugin.json", []);
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        var problem = PluginValidation.CheckManifest(m, name, tasktorVersion);
        static string List(IReadOnlyList<string>? items) => items is { Count: > 0 } ? string.Join(", ", items) : "nothing";
        IReadOnlyList<string> consent =
        [
            m.Name + " " + m.Version + (m.Author is { Length: > 0 } a ? " by " + a : string.Empty),
            "Reads: " + List(m.Reads),
            m.Network is { Count: > 0 } ? "Contacts: " + List(m.Network) : "Declares no network use",
            "Actions: " + List([.. (m.Actions ?? []).Select(x => x.Label)]),
            "Runs: " + string.Join(" ", ResolveCommand(m, folder, python ?? PathPython).Select(Quote)),
        ];
        return new ManifestResult(m, problem, consent);
    }

    /// <summary>The command with <c>{python}</c> substituted; a first part that is a file in the plugin folder becomes its full path.</summary>
    public static List<string> ResolveCommand(PluginManifest manifest, string dir, string python)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var list = manifest.Command.Select(c => c == "{python}" ? python : c).ToList();
        var first = Path.Combine(dir, list[0]);
        if (!Path.IsPathRooted(list[0]) && File.Exists(first))
        {
            list[0] = first;
        }

        return list;
    }

    /// <summary>With no bundled runtime (development, Linux): the first Python on PATH.</summary>
    public static string PathPython => s_pathPython.Value;

    private static readonly Lazy<string> s_pathPython = new(() =>
    {
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "python.exe", "py.exe" } : ["python3", "python"])
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p))
                {
                    return p;
                }
            }
        }

        return "python";
    });

    /// <summary>The stdin a run gets (<c>tasktor.plugin-input/1</c>), as the agent builds it.</summary>
    public static PluginInput Input(PluginCall call, IReadOnlyDictionary<string, string> settings, string snapshotPath, string dataDir, string? action = null) => new()
    {
        Call = call,
        Action = action,
        Settings = settings,
        SnapshotPath = snapshotPath,
        DataDir = dataDir,
    };

    /// <summary>The agent's output check; the cards as the snapshot would carry them, or why the output was refused.</summary>
    public static (IReadOnlyList<Card>? Cards, string? Error) Output(PluginManifest manifest, string stdout, DateTimeOffset now) =>
        PluginValidation.CheckOutput(manifest, stdout, now);

    internal static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) ? "\"" + s + "\"" : s;
}
