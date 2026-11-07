using System.Diagnostics;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Agent.Host;

/// <summary>
/// Opens the shelf (U4 amended D-G11): signals the running flyout process, or starts one from the install folder. Hover
/// fires often, so a start is not repeated within <see cref="StartCooldown"/> while the new process comes up.
/// </summary>
internal sealed class FlyoutLauncher(string installDir)
{
    public static readonly TimeSpan StartCooldown = TimeSpan.FromSeconds(3);
    public const string ExeName = "Tasktor.Flyout.exe";

    private long _lastStart = long.MinValue;

    /// <summary>Where the flyout lives: next to the agent, or in a <c>flyout</c> folder beside it.</summary>
    public static string? Find(string installDir, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        foreach (var path in new[] { Path.Combine(installDir, ExeName), Path.Combine(installDir, "flyout", ExeName) })
        {
            if (exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public bool ShowShelf() => Show(ViewerSignal.ShowShelf);

    public bool ShowCommand() => Show(ViewerSignal.ShowCommand);

    /// <summary>Signal <paramref name="surface"/> to the running flyout, or start one that opens it.</summary>
    public bool Show(string surface)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (Signal(surface))
        {
            return true;
        }

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastStart) < (long)StartCooldown.TotalMilliseconds)
        {
            return true; // starting already; the new process shows the shelf itself
        }

        if (Find(installDir, File.Exists) is not { } exe)
        {
            return false;
        }

        Interlocked.Exchange(ref _lastStart, now);
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            if (ViewerSignal.ArgFor(surface) is { } arg)
            {
                start.ArgumentList.Add(arg);
            }

            using var p = Process.Start(start);
            return p is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Signal(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(ViewerSignal.Instance, out var instance))
            {
                return false;
            }

            instance.Dispose();
            if (!EventWaitHandle.TryOpenExisting(name, out var signal))
            {
                return false;
            }

            using (signal)
            {
                return signal.Set();
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}
