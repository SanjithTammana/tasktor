using Tasktor.Contracts.Platform;
using Tasktor.Core.Apps;

namespace Tasktor.Core.Actions;

/// <summary>One A3 guard: a reason to refuse a process action, or null to pass.</summary>
public abstract class ProcessGuard
{
    public abstract string Name { get; }

    /// <param name="requested">The key the action was built for.</param>
    /// <param name="current">The process as re-read just now (null = gone).</param>
    public abstract string? Check(string action, ProcKey requested, ProcessRow? current);
}

/// <summary>Fresh check at action time: PID, creation time and exe must match ("Process changed, refresh").</summary>
public sealed class IdentityRecheckGuard : ProcessGuard
{
    public override string Name => "identity";

    public override string? Check(string action, ProcKey requested, ProcessRow? current) =>
        current is null ? "Already exited"
        : current.Key != requested ? "Process changed, refresh"
        : null;
}

/// <summary>Own-user processes only.</summary>
public sealed class OwnUserGuard : ProcessGuard
{
    public override string Name => "own_user";

    public override string? Check(string action, ProcKey requested, ProcessRow? current) =>
        current is { OwnUser: false } ? "Another user's process" : null;
}

/// <summary>Same integrity only: elevated rows are greyed ("Runs as admin — use Task Manager"); Tasktor never elevates.</summary>
public sealed class IntegrityGuard : ProcessGuard
{
    public override string Name => "integrity";

    public override string? Check(string action, ProcKey requested, ProcessRow? current) =>
        current is { Elevated: true } ? "Runs as admin — use Task Manager" : null;
}

/// <summary>Protected processes refuse terminate-class rights.</summary>
public sealed class ProtectedGuard : ProcessGuard
{
    public override string Name => "protected";

    public override string? Check(string action, ProcKey requested, ProcessRow? current) =>
        current is { Protected: true } ? "Protected by Windows" : null;
}

/// <summary>Critical-process denylist (End, Freeze); Freeze also spares the shell / Widgets hosts Tasktor's viewers need.</summary>
public sealed class DenylistGuard : ProcessGuard
{
    public static readonly IReadOnlySet<string> FreezeDenylist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "widgets.exe", "widgetservice.exe", "widgetboard.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        "searchhost.exe", "sihost.exe", "ctfmon.exe", "tasktor-shell.exe", "tasktor.flyout.exe", "tasktor.widget.exe",
    };

    public override string Name => "denylist";

    public override string? Check(string action, ProcKey requested, ProcessRow? current)
    {
        if (current is null)
        {
            return null;
        }

        // Every action that changes a critical process is refused (End, Close, Freeze, priority, EcoQoS, caps);
        // only read-only actions (open file location) pass.
        var mutates = action is not ("proc.open_location" or "proc.open_taskmgr");
        if (mutates && AppIdentity.IsCritical(current.Exe))
        {
            return "Critical Windows process";
        }

        if (action is "proc.freeze" or "job.pause" && FreezeDenylist.Contains(current.Exe))
        {
            return "Needed by Windows' shell";
        }

        return null;
    }
}

/// <summary>A3's chain of responsibility: every process action passes every guard first.</summary>
public sealed class GuardChain
{
    private readonly ProcessGuard[] _guards;

    public GuardChain(params ProcessGuard[] guards) => _guards = guards;

    public static GuardChain Default() =>
        new(new IdentityRecheckGuard(), new OwnUserGuard(), new IntegrityGuard(), new ProtectedGuard(), new DenylistGuard());

    /// <summary>Null when every guard passes; otherwise (guard name, reason).</summary>
    public (string Guard, string Reason)? Check(string action, ProcKey requested, ProcessRow? current)
    {
        foreach (var g in _guards)
        {
            if (g.Check(action, requested, current) is { } reason)
            {
                return (g.Name, reason);
            }
        }

        return null;
    }
}
