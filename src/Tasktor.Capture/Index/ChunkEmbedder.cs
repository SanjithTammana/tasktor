using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tasktor.Capture.Index;

/// <summary>C21.2: turns chunk (or query) texts into int8 vectors; null when it cannot (no model, a failure, too slow).</summary>
public interface IChunkEmbedder
{
    Task<IReadOnlyList<(long Id, sbyte[] Q, float S)>?> EmbedAsync(IReadOnlyList<(long Id, string Text)> items, CancellationToken cancel);
}

/// <summary>
/// The content worker as the encoder (D-C21: batch, never resident): <c>python -I -m tasktor_worker embed --model DIR</c>,
/// texts in as JSON lines on stdin, vectors out on stdout, at BelowNormal priority and killed at the timeout. Used only
/// when the model folder holds <c>model.onnx</c>, <c>vocab.txt</c> and a passing gate G8 report (<c>g8.json</c>), so an
/// encoder that has not beaten hashed TF-IDF on the owner's check set is never used (D-C20). No network: a local process.
/// </summary>
public sealed class WorkerEmbedder : IChunkEmbedder
{
    private readonly string _python;
    private readonly string _workerDir;
    private readonly string _model;

    private WorkerEmbedder(string python, string workerDir, string model) => (_python, _workerDir, _model) = (python, workerDir, model);

    /// <summary>The embedder, or null when Python, the worker, the model or a passing G8 report is missing.</summary>
    public static WorkerEmbedder? TryCreate(string python, string workerDir, string modelDir)
    {
        if (!File.Exists(python) || !Directory.Exists(Path.Combine(workerDir, "tasktor_worker")) || !File.Exists(Path.Combine(modelDir, "model.onnx"))
            || !File.Exists(Path.Combine(modelDir, "vocab.txt")) || !G8Passed(Path.Combine(modelDir, "g8.json")))
        {
            return null;
        }

        return new WorkerEmbedder(python, workerDir, modelDir);
    }

    /// <summary>True only for a G8 report that says it passed (the worker's <c>g8</c> command writes it).</summary>
    public static bool G8Passed(string reportPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            return doc.RootElement.TryGetProperty("gate", out var g) && g.GetString() == "G8"
                && doc.RootElement.TryGetProperty("pass", out var p) && p.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<(long Id, sbyte[] Q, float S)>?> EmbedAsync(IReadOnlyList<(long Id, string Text)> items, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return [];
        var info = new ProcessStartInfo(_python)
        {
            ArgumentList = { "-I", "-m", "tasktor_worker", "embed", "--model", _model },
            WorkingDirectory = _workerDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var name in new[] { "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP" }) info.Environment.Remove(name);
        using var process = Process.Start(info);
        if (process is null) return null;
        try
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // it may already have exited; the result tells
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancel);
            var stderr = process.StandardError.ReadToEndAsync(cancel); // drained, never logged (texts never reach it)
            foreach (var (id, text) in items)
            {
                await process.StandardInput.WriteLineAsync(Line(id, text).AsMemory(), cancel).ConfigureAwait(false);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return process.ExitCode == 0 ? Parse(await stdout.ConfigureAwait(false)) : null;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception k) when (k is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // already gone
            }

            return null;
        }
    }

    private static string Line(long id, string text)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteString("text", text);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>The worker's lines: {"id", "q" (base64 int8), "s"}; a malformed line is skipped.</summary>
    public static List<(long Id, sbyte[] Q, float S)> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var list = new List<(long, sbyte[], float)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var bytes = Convert.FromBase64String(r.GetProperty("q").GetString() ?? string.Empty);
                var q = new sbyte[bytes.Length];
                Buffer.BlockCopy(bytes, 0, q, 0, bytes.Length);
                var s = r.GetProperty("s").GetSingle();
                if (q.Length > 0 && float.IsFinite(s) && s > 0) list.Add((r.GetProperty("id").GetInt64(), q, s));
            }
            catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                // skipped
            }
        }

        return list;
    }

    public override string ToString() => "worker embedder (" + _model.Length.ToString(CultureInfo.InvariantCulture) + ")";
}
