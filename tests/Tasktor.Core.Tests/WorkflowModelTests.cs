using System.Text.Json;
using Tasktor.Core.Workflow;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>D-C10 parity: the C# assigner gives the Python reference's ids and confidences (within 1e-5) on the fixture model.</summary>
public sealed class WorkflowModelTests
{
    private static readonly string Fixtures = Path.Combine(RepoPaths.Root, "tests", "fixtures", "workflow");

    private static List<ActivityFeatures> ExtractRows()
    {
        var dir = Path.Combine(Fixtures, "extract");
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")));
        var apps = meta.RootElement.GetProperty("apps").EnumerateArray().Select(a => a.GetString()!).ToList();
        var csr = File.ReadAllBytes(Path.Combine(dir, "tokens.csr"));
        var n = BitConverter.ToInt32(csr, 0);
        var nnz = BitConverter.ToInt32(csr, 4);
        int I(int k) => BitConverter.ToInt32(csr, 8 + (4 * k));
        var bucketAt = 8 + (4 * (n + 1));
        var weightAt = bucketAt + (4 * nnz);
        var dense = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(dir, "dense.f32"))).ToArray();
        var ids = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(File.ReadAllBytes(Path.Combine(dir, "apps.i32"))).ToArray();
        var rows = new List<ActivityFeatures>();
        for (var i = 0; i < n; i++)
        {
            var (a, b) = (I(i), I(i + 1));
            var buckets = Enumerable.Range(a, b - a).Select(k => BitConverter.ToInt32(csr, bucketAt + (4 * k))).ToList();
            var weights = Enumerable.Range(a, b - a).Select(k => (double)BitConverter.ToSingle(csr, weightAt + (4 * k))).ToList();
            string? App(int col) => ids[(i * 3) + col] < 0 ? null : apps[ids[(i * 3) + col]];
            rows.Add(new ActivityFeatures(buckets, weights, [.. dense.Skip(i * 7).Take(7).Select(f => (double)f)], App(0)!, App(1), App(2)));
        }

        return rows;
    }

    [Fact]
    public void The_csharp_assigner_matches_the_python_reference_on_every_fixture_activity()
    {
        var model = WorkflowModel.Open(Path.Combine(Fixtures, "model"));
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "model", "expected.json")));
        var want = expected.RootElement.GetProperty("activities").EnumerateArray().ToList();
        var rows = ExtractRows();
        Assert.Equal(want.Count, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var y = model.Project(model.Vector(rows[i]));
            var wy = want[i].GetProperty("y").EnumerateArray().Select(v => v.GetDouble()).ToList();
            for (var j = 0; j < y.Length; j++)
            {
                Assert.True(Math.Abs(y[j] - wy[j]) < 1e-5, $"activity {i} dim {j}: {y[j]} vs {wy[j]}");
            }

            var (cluster, conf) = model.Assign(y);
            var wc = want[i].GetProperty("cluster");
            Assert.Equal(wc.ValueKind == JsonValueKind.Null ? null : wc.GetString(), cluster);
            Assert.True(Math.Abs(conf - want[i].GetProperty("confidence").GetDouble()) < 1e-5, $"activity {i} confidence");
        }
    }

    [Fact]
    public void A_changed_sidecar_is_refused_and_a_model_that_failed_activation_is_never_swapped_in()
    {
        using var tmp = new TempStore();
        var staging = Path.Combine(tmp.Root, "staging", "v1");
        Directory.CreateDirectory(staging);
        foreach (var f in Directory.GetFiles(Path.Combine(Fixtures, "model")))
        {
            File.Copy(f, Path.Combine(staging, Path.GetFileName(f)));
        }

        var prereg = ModelLoader.HashOf(File.ReadAllBytes(Path.Combine(RepoPaths.Root, "worker", "prereg.json")));
        var loader = new ModelLoader(Path.Combine(tmp.Root, "models"), prereg);
        Assert.Null(loader.Validate(staging));
        Assert.Equal("prereg changed", new ModelLoader(Path.Combine(tmp.Root, "models"), new string('0', 64)).Validate(staging));

        var eval = Path.Combine(staging, "eval.json");
        var good = File.ReadAllText(eval);
        File.WriteAllText(eval, good.Replace("\"activated\": true", "\"activated\": false", StringComparison.Ordinal));
        Assert.Equal("activation gates failed", loader.Validate(staging));
        File.WriteAllText(eval, good);

        var svd = Path.Combine(staging, "svd.f32");
        var bytes = File.ReadAllBytes(svd);
        File.WriteAllBytes(svd, [.. bytes.Take(bytes.Length - 4), 1, 2, 3, 4]);
        Assert.Contains("hash mismatch", loader.Validate(staging), StringComparison.Ordinal);
        File.WriteAllBytes(svd, bytes);

        var model = loader.Swap(staging);
        Assert.Equal("v1", model.Version);
        Assert.False(Directory.Exists(staging));
        Assert.Equal("v1", new ModelLoader(Path.Combine(tmp.Root, "models"), prereg).LoadCurrent()?.Version);
    }
}
