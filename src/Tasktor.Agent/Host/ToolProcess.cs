using System.Diagnostics;

namespace Tasktor.Agent.Host;

/// <summary>Plain process starts for U2: a rule's <c>run</c> command (argv, no shell) and a tool's CLI for its cache path.</summary>
internal static class ToolProcess
{
    /// <summary>D-H6 <c>run</c>: start argv as is (no shell, no window for console tools). Returns false when it could not start.</summary>
    public static bool Start(IReadOnlyList<string> argv)
    {
        try
        {
            var psi = new ProcessStartInfo(argv[0]) { UseShellExecute = false };
            foreach (var a in argv.Skip(1))
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>OQ-S6: a tool's CLI at report time only, 5 s timeout; the first line of stdout, or null.</summary>
    public static string? FirstLine(IReadOnlyList<string> argv)
    {
        try
        {
            var psi = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : argv[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (OperatingSystem.IsWindows())
            {
                // pip / npm / uv are .cmd or .exe shims on Windows: run through cmd /c with a fixed, known argv.
                psi.ArgumentList.Add("/c");
                foreach (var a in argv)
                {
                    psi.ArgumentList.Add(a);
                }
            }
            else
            {
                foreach (var a in argv.Skip(1))
                {
                    psi.ArgumentList.Add(a);
                }
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var read = p.StandardOutput.ReadLineAsync();
            if (!p.WaitForExit(5000))
            {
                p.Kill(entireProcessTree: true);
                return null;
            }

            return read.Wait(500) ? read.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
