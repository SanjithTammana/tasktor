using System.Globalization;
using System.Text.RegularExpressions;

namespace Tasktor.Shell;

/// <summary>
/// <c>tasktor-shell</c>'s command line: <c>--pipe &lt;name&gt;</c> (a check agent's pipe) and <c>--only-pid &lt;pid&gt;</c>
/// (repeatable: list only those processes' windows; every other window is skipped before its title is read).
/// </summary>
internal sealed record ShellArgs(string? Pipe, IReadOnlyList<uint> OnlyPids)
{
    public static ShellArgs Parse(IReadOnlyList<string> args)
    {
        string? pipe = null;
        var pids = new List<uint>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--pipe" && Regex.IsMatch(args[i + 1], "^[A-Za-z0-9._-]{1,64}$"))
            {
                pipe = args[i + 1];
            }
            else if (args[i] == "--only-pid" && uint.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                pids.Add(pid);
            }
        }

        return new ShellArgs(pipe, pids);
    }

    /// <summary>Whether a process's windows may be listed (always, without <c>--only-pid</c>).</summary>
    public bool Includes(uint pid) => OnlyPids.Count == 0 || OnlyPids.Contains(pid);
}
