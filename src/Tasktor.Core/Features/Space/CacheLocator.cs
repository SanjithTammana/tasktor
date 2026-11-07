namespace Tasktor.Core.Features.Space;

/// <summary>A named tool cache found on this PC, with the tool's own cleanup command (never run by Tasktor).</summary>
public sealed record NamedCache(string Name, string Path, string? Cleanup);

/// <summary>
/// I7 <c>CacheLocator</c> (a resolver, not a source): each named cache from its tool's environment variable first, then
/// the documented default; only paths that exist are returned. Only when neither gives a path that exists does it ask
/// the tool's CLI (OQ-S6: report time only, 5 s timeout, no network; the composition supplies <c>cli</c>).
/// </summary>
public sealed class CacheLocator
{
    private readonly Func<string, string?> _env;
    private readonly Func<string, string?> _folder;
    private readonly Func<string, bool> _exists;
    private readonly Func<IReadOnlyList<string>, string?>? _cli;

    /// <param name="env">Environment variables.</param>
    /// <param name="folder">Known Folders: <c>UserProfile</c>, <c>LocalAppData</c>.</param>
    /// <param name="exists">Directory exists.</param>
    /// <param name="cli">Runs a tool's CLI (argv) and returns its first stdout line, or null; 5 s timeout.</param>
    public CacheLocator(Func<string, string?> env, Func<string, string?> folder, Func<string, bool> exists, Func<IReadOnlyList<string>, string?>? cli = null)
    {
        _env = env;
        _folder = folder;
        _exists = exists;
        _cli = cli;
    }

    private string? Under(string known, params string[] parts) => _folder(known) is { Length: > 0 } root ? Path.Combine([root, .. parts]) : null;

    private string? Env(string name, params string[] parts) => _env(name) is { Length: > 0 } v ? (parts.Length == 0 ? v : Path.Combine([v, .. parts])) : null;

    public List<NamedCache> Locate()
    {
        var specs = new (string Name, Func<string?>[] Candidates, string[]? Cli, string? Cleanup)[]
        {
            ("pip", [() => Env("PIP_CACHE_DIR"), () => Under("LocalAppData", "pip", "Cache")], ["pip", "cache", "dir"], "pip cache purge"),
            ("npm", [() => Env("npm_config_cache"), () => Under("LocalAppData", "npm-cache")], ["npm", "config", "get", "cache"], "npm cache clean --force"),
            ("Hugging Face", [() => Env("HF_HUB_CACHE"), () => Env("HF_HOME", "hub"), () => Under("UserProfile", ".cache", "huggingface", "hub")], null, "huggingface-cli delete-cache"),
            ("conda", [() => Env("CONDA_PKGS_DIRS"), () => Under("UserProfile", ".conda", "pkgs"), () => Under("UserProfile", "miniconda3", "pkgs"), () => Under("UserProfile", "anaconda3", "pkgs")], null, "conda clean --all"),
            ("Gradle", [() => Env("GRADLE_USER_HOME", "caches"), () => Under("UserProfile", ".gradle", "caches")], null, null),
            ("Maven", [() => Under("UserProfile", ".m2", "repository")], null, null),
            ("NuGet", [() => Env("NUGET_PACKAGES"), () => Under("UserProfile", ".nuget", "packages")], null, "dotnet nuget locals all --clear"),
            ("Cargo", [() => Env("CARGO_HOME", "registry"), () => Under("UserProfile", ".cargo", "registry")], null, null),
            ("uv", [() => Env("UV_CACHE_DIR"), () => Under("LocalAppData", "uv", "cache")], ["uv", "cache", "dir"], "uv cache clean"),
        };
        var found = new List<NamedCache>();
        foreach (var (name, candidates, cli, cleanup) in specs)
        {
            var path = candidates.Select(c => c()).FirstOrDefault(p => p is not null && _exists(p));
            if (path is null && cli is not null && _cli?.Invoke(cli) is { Length: > 0 } fromCli && _exists(fromCli.Trim()))
            {
                path = fromCli.Trim();
            }

            if (path is not null)
            {
                found.Add(new NamedCache(name, path, cleanup));
            }
        }

        return found;
    }
}
