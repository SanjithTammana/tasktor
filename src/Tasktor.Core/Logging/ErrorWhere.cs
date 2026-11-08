using System.Diagnostics;

namespace Tasktor.Core.Logging;

/// <summary>
/// Where an exception was thrown, for the log: the innermost few frames as <c>Type.Method</c>, nothing else. Code names
/// are not personal data (G-redact); messages, file paths and arguments are never included. Uses
/// <see cref="DiagnosticMethodInfo"/>, which works under Native AOT (frames without metadata are skipped).
/// </summary>
public static class ErrorWhere
{
    public const int MaxFrames = 3;

    public static string Of(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var frames = new StackTrace(ex, fNeedFileInfo: false).GetFrames();
        var names = frames
            .Select(DiagnosticMethodInfo.Create)
            .OfType<DiagnosticMethodInfo>()
            .Select(m => Short(m.DeclaringTypeName) + "." + m.Name)
            .Take(MaxFrames)
            .ToList();
        return names.Count == 0 ? "-" : string.Join("<", names);
    }

    // "Namespace.Outer+Inner`1[[...]]" → "Inner": enough to find the method, no assembly noise.
    private static string Short(string type)
    {
        var t = type;
        var generic = t.IndexOf('[', StringComparison.Ordinal);
        if (generic >= 0)
        {
            t = t[..generic];
        }

        var cut = Math.Max(t.LastIndexOf('.'), t.LastIndexOf('+'));
        return cut >= 0 ? t[(cut + 1)..] : t;
    }
}
