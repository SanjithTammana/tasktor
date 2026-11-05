using Tasktor.Contracts.Workflow;
using Tasktor.Core.Learning;
using Tasktor.Core.Workflow;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>Builds the synthetic extract fixture (tests/fixtures/workflow/extract) the Python worker fits.</summary>
public sealed class WorkflowFixtureTests
{
    public static readonly string ExtractDir = Path.Combine(RepoPaths.Root, "tests", "fixtures", "workflow", "extract");

    internal static (string Hash, int Count, StoreSet Stores) Build(string root)
    {
        var set = new StoreSet(new EventStore(Path.Combine(root, "events.db")), new EpisodeStore(Path.Combine(root, "episodes.db")));
        var writer = StoreWriter.Inline(set);
        var hasher = new KeyedHasher(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), "tok");
        var pipe = new TrackingPipeline(new PrivacyGate(new Redactor([])), new TokenBuckets(hasher), writer, new PreregFile());
        long last = 0;
        foreach (var e in SyntheticWork.Days())
        {
            pipe.On(e);
            last = e.MonoMs;
        }

        pipe.Flush(last + 1000);
        var dir = Path.Combine(root, "extract");
        var (_, hash, count) = TrainingExtract.Write(set.Episodes, dir, "v1", SyntheticWork.Start.AddDays(15));
        return (hash, count, set);
    }

    [Fact]
    public void The_synthetic_extract_fixture_is_current_and_passes_g0()
    {
        using var tmp = new TempStore();
        var (hash, count, set) = Build(tmp.Root);
        using (set)
        {
            Assert.True(count >= 300, "G0 needs ≥ 300 activities, got " + count);
            var days = set.Episodes.Db.Scalar("SELECT COUNT(DISTINCT day) FROM activity");
            Assert.True(days >= 7);
        }

        if (Environment.GetEnvironmentVariable("TASKTOR_REGEN_FIXTURES") == "1")
        {
            Directory.CreateDirectory(ExtractDir);
            foreach (var f in Directory.GetFiles(Path.Combine(tmp.Root, "extract")))
            {
                File.Copy(f, Path.Combine(ExtractDir, Path.GetFileName(f)), overwrite: true);
            }
        }

        foreach (var f in new[] { "tokens.csr", "dense.f32", "days.i32", "apps.i32" })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(ExtractDir, f)), File.ReadAllBytes(Path.Combine(tmp.Root, "extract", f)));
        }
    }
}
