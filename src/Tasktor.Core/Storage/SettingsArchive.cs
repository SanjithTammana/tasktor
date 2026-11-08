using System.IO.Compression;
using System.Text.Json;
using Tasktor.Core.Rules;

namespace Tasktor.Core.Storage;

/// <summary>
/// <c>tasktor settings export | import | reset</c> (U1 D-H8, U2 H-S6, U3 H-W4). Export bundles config, rules, presets, the
/// plugin list and consents; learned state only with <c>--include-learned</c>, notes only with <c>--include-notes</c>.
/// Never plugin code, <c>track\</c>, <c>capture\</c> or the install key. Import validates every entry before replacing.
/// </summary>
public sealed class SettingsArchive
{
    private readonly StoreLayout _layout;

    public SettingsArchive(StoreLayout layout) => _layout = layout;

    public IReadOnlyList<string> Export(string zipPath, bool includeLearned, bool includeNotes)
    {
        ArgumentException.ThrowIfNullOrEmpty(zipPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var entries = new List<string>();
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Add(string path, string name)
        {
            if (File.Exists(path))
            {
                zip.CreateEntryFromFile(path, name);
                entries.Add(name);
            }
        }

        Add(_layout.Config, "config.json");
        Add(_layout.Rules, "rules.json");
        Add(_layout.Presets, "presets.json");
        Add(_layout.Consents, "state/consents.json");
        if (Directory.Exists(_layout.PluginsDir))
        {
            var list = string.Join("\n", Directory.GetDirectories(_layout.PluginsDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            var e = zip.CreateEntry("plugins.txt");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write(list);
            }

            entries.Add("plugins.txt");
        }

        if (includeLearned)
        {
            Add(_layout.JobKinds, "state/job-kinds.json");
            Add(_layout.LearnedVolumes, "state/volume-kinds.json");
        }

        if (includeNotes && Directory.Exists(_layout.NotesDir))
        {
            foreach (var f in Directory.GetFiles(_layout.NotesDir).Order(StringComparer.Ordinal))
            {
                Add(f, "notes/" + Path.GetFileName(f));
            }
        }

        return entries;
    }

    /// <summary>Validate, then replace. Throws <see cref="InvalidDataException"/> and changes nothing when anything is wrong.</summary>
    public IReadOnlyList<string> Import(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["config.json"] = _layout.Config,
            ["rules.json"] = _layout.Rules,
            ["presets.json"] = _layout.Presets,
            ["state/consents.json"] = _layout.Consents,
            ["state/job-kinds.json"] = _layout.JobKinds,
            ["state/volume-kinds.json"] = _layout.LearnedVolumes,
        };
        var staged = new List<(string Target, byte[] Bytes)>();
        foreach (var e in zip.Entries)
        {
            if (e.FullName == "plugins.txt")
            {
                continue;
            }

            string target;
            if (e.FullName.StartsWith("notes/", StringComparison.Ordinal) && e.FullName.Count(ch => ch == '/') == 1)
            {
                target = Path.Combine(_layout.NotesDir, StoreLayout.SafeName(e.FullName[6..]));
            }
            else if (!allowed.TryGetValue(e.FullName, out target!))
            {
                throw new InvalidDataException("unexpected entry in the settings archive: " + e.FullName);
            }

            if (e.Length > 4 * 1024 * 1024)
            {
                throw new InvalidDataException("entry too large: " + e.FullName);
            }

            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            try
            {
                switch (e.FullName)
                {
                    case "config.json":
                        SettingsStore.Parse(text);
                        break;
                    case "rules.json":
                        var set = RulesLoader.Load(null, text);
                        if (set.Errors.FirstOrDefault(er => er.Id is null) is { } fatal)
                        {
                            throw new InvalidDataException(fatal.ToString());
                        }

                        break;
                    case "presets.json":
                        JsonSerializer.Deserialize(text, Contracts.ContractsJsonContext.Default.PresetsFile);
                        break;
                    case "state/consents.json":
                        JsonSerializer.Deserialize(text, CoreJsonContext.Default.ConsentsFile);
                        break;
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(e.FullName + ": " + ex.Message.Split('\n')[0]);
            }

            staged.Add((target, bytes));
        }

        foreach (var (target, bytes) in staged)
        {
            AtomicFile.WriteWithBackup(target, bytes);
        }

        return staged.Select(s => s.Target).ToList();
    }

    /// <summary>Restore default settings and rules (each with a <c>.bak</c>); keeps all data.</summary>
    public void Reset()
    {
        AtomicFile.WriteWithBackup(_layout.Config, System.Text.Encoding.UTF8.GetBytes("{ \"schema_version\": 1 }\n"));
        if (File.Exists(_layout.Rules))
        {
            File.Copy(_layout.Rules, _layout.Rules + ".bak", overwrite: true);
            File.Delete(_layout.Rules);
        }
    }

    /// <summary><c>tasktor log bundle</c> (H6): logs + config, no data folders. Returns the file list (shown first).</summary>
    public IReadOnlyList<string> LogBundle(string zipPath)
    {
        var files = new List<string>();
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        if (Directory.Exists(_layout.LogDir))
        {
            foreach (var f in Directory.GetFiles(_layout.LogDir, "*.log").Order(StringComparer.Ordinal))
            {
                zip.CreateEntryFromFile(f, "log/" + Path.GetFileName(f));
                files.Add("log/" + Path.GetFileName(f));
            }
        }

        if (File.Exists(_layout.Config))
        {
            zip.CreateEntryFromFile(_layout.Config, "config.json");
            files.Add("config.json");
        }

        return files;
    }
}
