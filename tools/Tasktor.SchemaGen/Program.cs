namespace Tasktor.SchemaGen;

/// <summary>
/// Writes <c>schemas/*.json</c> from the contract records. Usage: <c>Tasktor.SchemaGen [schemas-dir]</c>.
/// <c>--check</c> exits 1 when any file is missing or differs (CI: schemas must be current).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var check = args.Contains("--check");
        var dir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? FindSchemasDir();
        Directory.CreateDirectory(dir);
        var stale = 0;
        foreach (var entry in SchemaCatalog.Entries)
        {
            var path = Path.Combine(dir, entry.File);
            var text = SchemaCatalog.Generate(entry);
            var current = File.Exists(path) ? File.ReadAllText(path) : null;
            if (current == text)
            {
                continue;
            }

            stale++;
            if (check)
            {
                Console.Error.WriteLine("stale: " + entry.File);
            }
            else
            {
                File.WriteAllText(path, text);
                Console.WriteLine("wrote " + entry.File);
            }
        }

        return check && stale > 0 ? 1 : 0;
    }

    private static string FindSchemasDir()
    {
        var d = new DirectoryInfo(Environment.CurrentDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Tasktor.sln")))
        {
            d = d.Parent;
        }

        return Path.Combine(d?.FullName ?? Environment.CurrentDirectory, "schemas");
    }
}
