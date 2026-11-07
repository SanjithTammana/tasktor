using System.Security.Cryptography;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Workflow;

namespace Tasktor.Core.Workflow;

/// <summary>One activity as the assigner sees it: hashed tokens with ms weights, the dense columns, the app context.</summary>
public sealed record ActivityFeatures(IReadOnlyList<int> Buckets, IReadOnlyList<double> WeightsMs, IReadOnlyList<double> Dense, string App, string? Prev, string? Next);

/// <summary>
/// A validated model directory (D-C10), read-only after open: the manifest, the sidecars (hash-checked) and the
/// <see cref="ExemplarAssigner"/> math. Identical to the Python reference within 1e-5 (parity test on the fixture model).
/// </summary>
public sealed class WorkflowModel
{
    private readonly int[] _buckets;
    private readonly double[] _idf;
    private readonly double[] _svd;
    private readonly double[] _mean;
    private readonly double[] _comps;
    private readonly double[] _exemplars;
    private readonly double[] _wfExemplars;
    private readonly Dictionary<string, int> _apps;
    private readonly Dictionary<string, int> _ctx;
    private readonly int _lsa;

    private WorkflowModel(string dir, ModelManifest m, Dictionary<string, byte[]> files)
    {
        Directory = dir;
        Manifest = m;
        _buckets = Ints(files["buckets.i32"]);
        _idf = Floats(files["idf.f32"]);
        _svd = Floats(files["svd.f32"]);
        _lsa = _buckets.Length == 0 ? 64 : _svd.Length / _buckets.Length;
        var pca = Floats(files["pca.f32"]);
        var cols = m.PcaDims + 1;
        _mean = new double[m.Dims];
        _comps = new double[m.Dims * m.PcaDims];
        for (var r = 0; r < m.Dims; r++)
        {
            _mean[r] = pca[r * cols];
            for (var c = 0; c < m.PcaDims; c++)
            {
                _comps[(r * m.PcaDims) + c] = pca[(r * cols) + 1 + c];
            }
        }

        _exemplars = Floats(files["exemplars.f32"]);
        _wfExemplars = files.TryGetValue("wf_exemplars.f32", out var wf) ? Floats(wf) : [];
        _apps = m.Apps.Select((a, i) => (a, i)).ToDictionary(x => x.a, x => x.i, StringComparer.Ordinal);
        _ctx = m.ContextApps.Select((a, i) => (a, i)).ToDictionary(x => x.a, x => x.i, StringComparer.Ordinal);
    }

    public string Directory { get; }

    public ModelManifest Manifest { get; }

    public string Version => Manifest.Version;

    /// <summary>Open and check every sidecar's SHA-256 against the manifest. Throws on any mismatch.</summary>
    public static WorkflowModel Open(string dir)
    {
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(dir, "model.json")), ContractsJsonContext.Default.ModelManifest)
            ?? throw new InvalidDataException("empty model.json");
        if (manifest.Schema != ContractIds.Model)
        {
            throw new InvalidDataException("not a tasktor.model/1 file");
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var f in manifest.Files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir, Path.GetFileName(f.Name)));
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), f.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("sidecar hash mismatch: " + f.Name);
            }

            files[f.Name] = bytes;
        }

        foreach (var required in new[] { "buckets.i32", "idf.f32", "svd.f32", "pca.f32", "exemplars.f32" })
        {
            if (!files.ContainsKey(required))
            {
                throw new InvalidDataException("missing sidecar " + required);
            }
        }

        return new WorkflowModel(dir, manifest, files);
    }

    private static int[] Ints(byte[] b) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(b).ToArray();

    private static double[] Floats(byte[] b) => [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(b).ToArray().Select(f => (double)f)];

    private static void L2(Span<double> v)
    {
        double s = 0;
        foreach (var x in v)
        {
            s += x * x;
        }

        var n = Math.Sqrt(s);
        if (n > 0)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] /= n;
            }
        }
    }

    /// <summary>The weighted block vector before PCA (the reference's <c>Model.vector</c>).</summary>
    public double[] Vector(ActivityFeatures a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var m = Manifest;
        var w = m.Weights;
        var x = new double[m.Dims];
        var tf = new double[_buckets.Length];
        for (var k = 0; k < a.Buckets.Count; k++)
        {
            var i = Array.BinarySearch(_buckets, a.Buckets[k]);
            if (i >= 0 && a.WeightsMs[k] > 0)
            {
                tf[i] = Math.Log(1 + (a.WeightsMs[k] / 1000.0)) * _idf[i];
            }
        }

        L2(tf);
        var text = new double[_lsa];
        if (tf.Any(v => v != 0))
        {
            for (var i = 0; i < tf.Length; i++)
            {
                if (tf[i] == 0)
                {
                    continue;
                }

                for (var j = 0; j < _lsa; j++)
                {
                    text[j] += tf[i] * _svd[(i * _lsa) + j];
                }
            }

            L2(text);
        }

        var o = 0;
        for (var j = 0; j < _lsa; j++)
        {
            x[o++] = text[j] * w.Text;
        }

        var apps = _apps.Count;
        x[o + (_apps.TryGetValue(a.App, out var ai) ? ai : apps)] = 1.0 * w.App;
        o += apps + 1;

        var d = a.Dense;
        var h = d[0];
        Span<double> time = [Math.Sin(2 * Math.PI * h / 24), Math.Cos(2 * Math.PI * h / 24), d[1], Math.Log(1 + Math.Max(d[2], 0.0)) / Math.Log(1 + 60.0)];
        L2(time);
        for (var j = 0; j < 4; j++)
        {
            x[o++] = time[j] * w.Time;
        }

        var c = _ctx.Count;
        var ctx = new double[2 * (c + 1)];
        if (a.Prev is not null)
        {
            ctx[_ctx.TryGetValue(a.Prev, out var pi) ? pi : c] = 1;
        }

        if (a.Next is not null)
        {
            ctx[c + 1 + (_ctx.TryGetValue(a.Next, out var ni) ? ni : c)] = 1;
        }

        L2(ctx);
        foreach (var v in ctx)
        {
            x[o++] = v * w.Context;
        }

        Span<double> shape = [d[3], Math.Min(d[4], 20.0) / 20.0, Math.Min(d[5], 5.0) / 5.0, d[6]];
        L2(shape);
        for (var j = 0; j < 4; j++)
        {
            x[o++] = shape[j] * w.Shape;
        }

        return x;
    }

    public double[] Project(double[] x)
    {
        ArgumentNullException.ThrowIfNull(x);
        var k = Manifest.PcaDims;
        var y = new double[k];
        for (var r = 0; r < x.Length; r++)
        {
            var v = x[r] - _mean[r];
            for (var c = 0; c < k; c++)
            {
                y[c] += v * _comps[(r * k) + c];
            }
        }

        return y;
    }

    /// <summary>Nearest exemplar per cluster; confidence = clamp(1 − d / r_c, 0, 1); d &gt; r_c → none.</summary>
    public (string? Cluster, double Confidence) Assign(double[] y) => Nearest(Manifest.Clusters, _exemplars, y);

    public (string? Workflow, double Confidence) AssignWindow(double[] share) => Nearest(Manifest.Workflows, _wfExemplars, share);

    private static (string?, double) Nearest(IReadOnlyList<ModelCluster> clusters, double[] ex, double[] y)
    {
        string? best = null;
        var conf = 0.0;
        var dim = y.Length;
        foreach (var cl in clusters)
        {
            if (cl.ExemplarCount == 0 || cl.Status is "retired" or "hidden")
            {
                continue;
            }

            var d = double.MaxValue;
            for (var e = cl.ExemplarStart; e < cl.ExemplarStart + cl.ExemplarCount; e++)
            {
                double s = 0;
                for (var j = 0; j < dim; j++)
                {
                    var diff = ex[(e * dim) + j] - y[j];
                    s += diff * diff;
                }

                d = Math.Min(d, Math.Sqrt(s));
            }

            if (d > cl.Radius)
            {
                continue;
            }

            var c = cl.Radius > 0 ? Math.Clamp(1 - (d / cl.Radius), 0, 1) : 0;
            if (best is null || c > conf || (c == conf && string.CompareOrdinal(cl.Id, best) < 0))
            {
                (best, conf) = (cl.Id, c);
            }
        }

        return (best, conf);
    }

    public ModelCluster? Cluster(string? id) => id is null ? null : Manifest.Clusters.FirstOrDefault(c => c.Id == id);

    public ModelCluster? Workflow(string? id) => id is null ? null : Manifest.Workflows.FirstOrDefault(c => c.Id == id);
}

/// <summary>
/// Validates a staged model and swaps it in (D-C10): schema, sidecar hashes, the prereg hash and feature-spec version
/// match this build, and <c>eval.json</c> says the activation gates passed; then staging → <c>models/&lt;ver&gt;</c>. The
/// previous 2 versions stay for rollback. A model whose eval failed activation is never swapped in.
/// </summary>
public sealed class ModelLoader
{
    private readonly string _modelsDir;
    private readonly string _preregHash;

    public ModelLoader(string modelsDir, string preregHash)
    {
        _modelsDir = modelsDir;
        _preregHash = preregHash;
    }

    public WorkflowModel? Current { get; private set; }

    public string CurrentFile => Path.Combine(_modelsDir, "current.txt");

    public static string HashOf(byte[] preregBytes) => Convert.ToHexStringLower(SHA256.HashData(preregBytes));

    public string? Validate(string staging)
    {
        try
        {
            var model = WorkflowModel.Open(staging);
            if (model.Manifest.PreregHash != _preregHash)
            {
                return "prereg changed";
            }

            if (model.Manifest.FeatureSpec != new FeatureSpec().Version)
            {
                return "feature spec version";
            }

            var eval = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(staging, "eval.json")), ContractsJsonContext.Default.EvalReport);
            if (eval is not { Activated: true } || !model.Manifest.Activated)
            {
                return "activation gates failed";
            }

            return null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return e is JsonException ? "unreadable manifest" : e.Message;
        }
    }

    /// <summary>Move a validated staging dir into place and make it current; keeps the 2 previous versions.</summary>
    public WorkflowModel Swap(string staging)
    {
        var version = WorkflowModel.Open(staging).Version;
        var target = Path.Combine(_modelsDir, Storage.StoreLayout.SafeName(version));
        Directory.CreateDirectory(_modelsDir); // the first refit
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.Move(staging, target);
        Current = WorkflowModel.Open(target);
        var history = (File.Exists(CurrentFile) ? File.ReadAllLines(CurrentFile) : []).Where(l => l.Length > 0 && l != version).Prepend(version).ToList();
        Storage.AtomicFile.Write(CurrentFile, System.Text.Encoding.UTF8.GetBytes(string.Join('\n', history.Take(3))));
        foreach (var old in history.Skip(3))
        {
            var dir = Path.Combine(_modelsDir, Storage.StoreLayout.SafeName(old));
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        return Current;
    }

    /// <summary>At start: open the current version (or the newest previous one that still validates).</summary>
    public WorkflowModel? LoadCurrent()
    {
        foreach (var v in File.Exists(CurrentFile) ? File.ReadAllLines(CurrentFile) : [])
        {
            try
            {
                return Current = WorkflowModel.Open(Path.Combine(_modelsDir, Storage.StoreLayout.SafeName(v)));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
            {
                // try the previous version (rollback)
            }
        }

        return null;
    }

    public void Rollback()
    {
        var history = File.Exists(CurrentFile) ? File.ReadAllLines(CurrentFile).Where(l => l.Length > 0).ToList() : [];
        if (history.Count > 1)
        {
            Storage.AtomicFile.Write(CurrentFile, System.Text.Encoding.UTF8.GetBytes(string.Join('\n', history.Skip(1))));
            LoadCurrent();
        }
    }
}
