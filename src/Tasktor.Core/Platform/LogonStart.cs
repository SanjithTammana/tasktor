namespace Tasktor.Core.Platform;

/// <summary>What <see cref="LogonStart.Apply"/> changed.</summary>
public enum LogonStartChange
{
    None,
    Added,
    Updated,
    Removed,
}

/// <summary>
/// H8 portable build (owner, 2026-11-09: no MSIX, so no startup task): opt-in start at logon through one value,
/// <see cref="ValueName"/>, under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, holding the quoted agent path
/// (and its data folder when one was given). <see cref="Apply"/> brings that value in line with <c>start_with_windows</c>:
/// written when on (rewritten when the folder moved), removed when off. Pure: the registry is behind three delegates.
/// </summary>
public sealed class LogonStart(Func<string?> read, Action<string> write, Action delete)
{
    public const string ValueName = "Tasktor";

    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The Run value's command line. Throws on a quote in a path (Windows paths cannot hold one).</summary>
    public static string Command(string agentExe, string? dataDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentExe);
        if (agentExe.Contains('"', StringComparison.Ordinal) || dataDir?.Contains('"', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("a path with a quote");
        }

        return "\"" + agentExe + "\"" + (dataDir is { Length: > 0 } d ? " --data-dir \"" + d + "\"" : string.Empty);
    }

    /// <summary>
    /// Only the owner's own agent touches the entry: never a check instance (its own <c>--pipe</c>, <c>--switcher-pid</c>
    /// or <c>TASKTOR_PIPE</c>), which runs beside the real one.
    /// </summary>
    public static bool AllowedFor(string? pipe, IReadOnlyList<uint> switcherPids, string? pipeEnv) =>
        pipe is null && (switcherPids?.Count ?? 0) == 0 && string.IsNullOrEmpty(pipeEnv);

    /// <summary>Bring the Run value in line with <paramref name="wanted"/>; <paramref name="command"/> is <see cref="Command"/>'s.</summary>
    public LogonStartChange Apply(bool wanted, string command)
    {
        var current = read();
        if (wanted)
        {
            if (current == command)
            {
                return LogonStartChange.None;
            }

            write(command);
            return current is null ? LogonStartChange.Added : LogonStartChange.Updated;
        }

        if (current is null)
        {
            return LogonStartChange.None;
        }

        delete();
        return LogonStartChange.Removed;
    }
}
