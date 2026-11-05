using System.Diagnostics;

namespace Tasktor.Windows.Shell;

/// <summary>Runs the optional FancyZones CLI with an argument list and a bounded wait.</summary>
public static class FancyZonesBridge
{
    public static IReadOnlyList<string> Arguments(string monitor, string layout) => ["set-layout", layout, "--monitor", monitor];

    public static string? Find()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        {
            var path = Path.Combine(root, "PowerToys", "PowerToys.FancyZones.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static bool Apply(string executable, string monitor, string layout)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in Arguments(monitor, layout)) info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return false;
            if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); return false; }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return false; }
    }
}
