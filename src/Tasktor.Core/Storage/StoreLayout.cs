namespace Tasktor.Core.Storage;

/// <summary>
/// Every path in the store (U1 D-H8); nothing else builds a path. The root is <c>%LOCALAPPDATA%\Tasktor</c> from the
/// LocalAppData Known Folder (never Roaming, never a literal path), unless <c>--data-dir</c>, a <c>portable.flag</c> next
/// to the exe, or <c>TASKTOR_DATA_DIR</c> says otherwise.
/// </summary>
public sealed class StoreLayout
{
    public const string PortableFlag = "portable.flag";

    public StoreLayout(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>Resolve the root: explicit <paramref name="dataDir"/>, then a portable flag beside <paramref name="exeDir"/>, then the environment, then LocalAppData.</summary>
    public static StoreLayout Resolve(string? dataDir = null, string? exeDir = null)
    {
        if (!string.IsNullOrEmpty(dataDir))
        {
            return new StoreLayout(dataDir);
        }

        if (!string.IsNullOrEmpty(exeDir) && File.Exists(Path.Combine(exeDir, PortableFlag)))
        {
            return new StoreLayout(Path.Combine(exeDir, "data"));
        }

        if (Environment.GetEnvironmentVariable("TASKTOR_DATA_DIR") is { Length: > 0 } env)
        {
            return new StoreLayout(env);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return new StoreLayout(Path.Combine(local, "Tasktor"));
    }

    public string Config => Path.Combine(Root, "config.json");

    public string Rules => Path.Combine(Root, "rules.json");

    public string Snapshot => Path.Combine(Root, "snapshot.json");

    public string Presets => Path.Combine(Root, "presets.json");

    public string StateDir => Path.Combine(Root, "state");

    public string InstallKey => Path.Combine(StateDir, "install.key");

    public string Consents => Path.Combine(StateDir, "consents.json");

    public string Pause => Path.Combine(StateDir, "pause.json");

    public string JobKinds => Path.Combine(StateDir, "job-kinds.json");

    public string Frozen => Path.Combine(StateDir, "frozen.json");

    /// <summary>The names of the CPU-cap Job Objects this agent holds, so a restart can release what a crash left capped.</summary>
    public string Caps => Path.Combine(StateDir, "caps.txt");

    public string SelfUse => Path.Combine(StateDir, "selfuse.json");

    public string Window => Path.Combine(StateDir, "window.json");

    public string Focus => Path.Combine(StateDir, "focus.json");

    public string Projects => Path.Combine(StateDir, "projects.json");

    /// <summary>I14 drop counts per hour (30 days), for the card and the <c>connectivity_hourly</c> export.</summary>
    public string Connectivity => Path.Combine(StateDir, "connectivity.json");

    public string CaptureBlocks => Path.Combine(StateDir, "capture-blocks.json");

    public string LearnedVolumes => Path.Combine(StateDir, "volume-kinds.json");

    public string JobsDir => Path.Combine(Root, "jobs");

    public string JobFile(string id) => Path.Combine(JobsDir, SafeName(id) + ".json");

    public string JobControlFile(string id) => Path.Combine(JobsDir, SafeName(id) + ".control.json");

    public string AgentsDir => Path.Combine(Root, "agents");

    public string AgentFile(string session) => Path.Combine(AgentsDir, SafeName(session) + ".json");

    public string TrackDir => Path.Combine(Root, "track");

    public string EventsDb => Path.Combine(TrackDir, "events.db");

    public string EpisodesDb => Path.Combine(TrackDir, "episodes.db");

    public string ExtractDir => Path.Combine(TrackDir, "extract");

    public string ModelsDir => Path.Combine(TrackDir, "models");

    public string CaptureDir => Path.Combine(Root, "capture");

    public string VaultDb => Path.Combine(CaptureDir, "vault.db");

    public string IndexDb => Path.Combine(CaptureDir, "index.db");

    public string PluginsDir => Path.Combine(Root, "plugins");

    public string PluginDir(string id) => Path.Combine(PluginsDir, SafeName(id));

    public string PluginDataDir => Path.Combine(Root, "plugin-data");

    public string PluginData(string id) => Path.Combine(PluginDataDir, SafeName(id));

    public string NotesDir => Path.Combine(Root, "notes");

    public string LogDir => Path.Combine(Root, "log");

    public string CacheDir => Path.Combine(Root, "cache");

    /// <summary>A file or folder name from untrusted text: <c>[A-Za-z0-9._-]</c> only, ≤ 64 chars, never "." or "..".</summary>
    public static string SafeName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Span<char> buf = stackalloc char[Math.Min(text.Length, 64)];
        for (var i = 0; i < buf.Length; i++)
        {
            var c = text[i];
            buf[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_';
        }

        var s = new string(buf);
        return s.Length == 0 || s.Trim('.').Length == 0 ? "_" : s;
    }

    /// <summary>True when <paramref name="id"/> is a valid progress-file id (B4: <c>[A-Za-z0-9._-]{1,64}</c>).</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && id.Trim('.').Length > 0;
}
