namespace Tasktor.Tests.Shared;

/// <summary>Locates the repository root (the folder holding <c>Tasktor.sln</c>) from the test binaries.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = Find();

    public static string Fixtures => Path.Combine(Root, "tests", "fixtures");

    public static string Schemas => Path.Combine(Root, "schemas");

    private static string Find()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Tasktor.sln")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? throw new InvalidOperationException("Tasktor.sln not found above " + AppContext.BaseDirectory);
    }
}
